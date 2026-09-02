using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 圖層效果堆疊的快取：堆疊套用後的像素＋髒區。
///
/// **座標系＝圖層座標，範圍＝圖層內容（含效果外擴），與畫布無關。**
/// 這是「移動圖層時效果跟不上／殘留」的根本解法：Offset 只是合成時的平移，
/// 快取本身在圖層座標裡完全不變 —— 平移一層不需要重算任何效果，
/// 拖曳覆疊也能直接拿快取的快照畫（外框、陰影、漸層在拖曳中都看得到）。
/// 存取一律在 Document.SyncRoot 內。
/// </summary>
public sealed class LayerEffectCache : IDisposable
{
    public TileSurface Surface { get; } = new();

    /// <summary>已至少完整算過一次（合成器才會拿快取而不是基底像素）。</summary>
    public bool Rendered { get; internal set; }

    internal bool DirtyAll = true;
    internal SKRectI Dirty = SKRectI.Empty;      // 圖層座標（原始髒區，不含效果外擴；外擴在取工作時依當時的堆疊算）
    internal SKRectI LastRegion = SKRectI.Empty; // 上次計算的範圍（圖層座標）
    internal SKRectI LastCanvas = SKRectI.Empty; // 上次計算時畫布在圖層座標的範圍（只有「位置相關」效果在乎）
    internal SKPointI LastOffset;                // 上次計算時的圖層 Offset（物件是 doc 座標，Offset 變了物件在圖層座標就動了）
    internal bool HasLastOffset;

    public bool HasPending => DirtyAll || !Dirty.IsEmpty;

    /// <summary>已被取走、還沒寫回的工作數（worker 鎖外計算中）。同步等待者靠它判斷「真的算完了」。</summary>
    internal int InFlight;

    public void MarkDirty(SKRectI layerRect)
    {
        if (layerRect.Width <= 0 || layerRect.Height <= 0) return;
        if (DirtyAll) return;
        Dirty = Dirty.IsEmpty ? layerRect : SKRectI.Union(Dirty, layerRect);
    }

    public void MarkAllDirty()
    {
        DirtyAll = true;
        Dirty = SKRectI.Empty;
    }

    internal void ClearTiles()
    {
        foreach (var idx in Surface.Tiles.Keys.ToList()) Surface.RemoveTile(idx);
    }

    public void Dispose() => Surface.Dispose();
}

/// <summary>
/// 在合成器 worker 上把圖層效果堆疊算進快取：鎖內抓來源像素與堆疊快照、鎖外計算、鎖內寫回。
///
/// 失效邏輯（殘留像素的根本解）：來源在 D 變了，輸出會變的範圍是 D 外擴「總 margin」
/// （外框／陰影／光暈會把顏色畫到內容之外），所以**寫回範圍 = D + margin**、
/// 計算範圍 = 寫回範圍再 + margin；寫回範圍內沒算到的地方一律清成透明，
/// 舊內容搬走後留下的外框才不會殘留。有「整層來源」效果時整層重算並先清空快取。
/// </summary>
public static class LayerEffectRenderer
{
    /// <summary>某層的效果快取剛寫回（worker 執行緒上觸發）：縮圖等「不走合成器」的畫面靠它更新。</summary>
    public static event Action<RasterLayer>? LayerRendered;

    private sealed class Job
    {
        public required RasterLayer Layer;
        public required SKRectI Region;   // 快取涵蓋的範圍（圖層座標：內容＋效果外擴）
        public required SKRectI Compute;  // 這次算的範圍（圖層座標）
        public required SKRectI Write;    // 寫回的範圍（圖層座標；Compute 以外的部分清成透明）
        public required bool Full;        // 整層重算（寫回前先清空快取）
        public required uint[] Pixels;    // Compute 範圍的基底像素
        public required List<LayerEffect> Effects;
        public required List<byte[]?> Masks; // 每個效果在 Compute 範圍內的遮罩（null = 整層）
        public required SKSizeI DocSize;
    }

    /// <summary>算完所有待處理的圖層；回傳是否有任何圖層被更新（呼叫端據此決定要不要再跑一輪）。</summary>
    public static bool RenderPending(Document doc, CancellationToken ct = default)
    {
        var any = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Job? job;
            lock (doc.SyncRoot)
            {
                job = TakeJobLocked(doc);
            }
            if (job == null) return any;

            uint[] result;
            try
            {
                result = Compute(job, ct);
            }
            catch
            {
                lock (doc.SyncRoot) AbandonLocked(doc, job); // 取消／炸掉：把工作還回去，等待者才不會卡死
                throw;
            }

            lock (doc.SyncRoot)
            {
                WriteBackLocked(doc, job, result);
            }
            any = true;
        }
    }

    /// <summary>算到一半被取消：髒區還回去、飛行中計數歸還。</summary>
    private static void AbandonLocked(Document doc, Job job)
    {
        var cache = job.Layer.FxCache;
        cache.InFlight--;
        if (job.Full) cache.MarkAllDirty(); else cache.MarkDirty(job.Write);
        Monitor.PulseAll(doc.SyncRoot);
    }

    /// <summary>
    /// 同步把某一層算到最新（烙印／匯出／拖曳快照前用）。
    /// 合成器 worker 可能已經取走這層的工作、正在鎖外計算 —— 那就等它寫回，不然會拿到舊快取。
    /// </summary>
    public static void RenderLayerNow(Document doc, RasterLayer layer)
    {
        while (true)
        {
            Job? job;
            lock (doc.SyncRoot)
            {
                if (!layer.HasActiveEffects || layer.Document != doc) return;
                job = TakeJobLocked(doc, layer);
                if (job == null)
                {
                    if (layer.FxCache.InFlight <= 0) return;
                    Monitor.Wait(doc.SyncRoot, 20); // worker 寫回時 PulseAll；逾時再檢查一次
                    continue;
                }
            }
            uint[] result;
            try
            {
                result = Compute(job, CancellationToken.None);
            }
            catch
            {
                lock (doc.SyncRoot) AbandonLocked(doc, job);
                throw;
            }
            lock (doc.SyncRoot)
            {
                WriteBackLocked(doc, job, result);
            }
        }
    }

    /// <summary>作用中效果的總 margin（有限值相加；整層來源的效果不計）。</summary>
    public static int TotalMargin(RasterLayer layer)
    {
        var margin = 0;
        foreach (var e in layer.Effects)
        {
            if (!e.Enabled) continue;
            var m = e.Effect.SourceMargin;
            if (m > 0) margin += m;
        }
        return margin;
    }

    /// <summary>
    /// 圖層內容在圖層座標的範圍（基底像素 ∪ 物件，不含畫布內編輯中被藏起來的物件）。
    /// </summary>
    public static SKRectI ContentRegion(RasterLayer layer)
    {
        var bounds = layer.Surface.ContentBounds;
        foreach (var el in layer.Elements)
        {
            if (el.Id == layer.HiddenElementId) continue;
            var b = el.Bounds;
            if (b.IsEmpty) continue;
            var lb = new SKRectI(b.Left - layer.Offset.X, b.Top - layer.Offset.Y,
                b.Right - layer.Offset.X, b.Bottom - layer.Offset.Y);
            bounds = bounds.IsEmpty ? lb : SKRectI.Union(bounds, lb);
        }
        return bounds;
    }

    private static Job? TakeJobLocked(Document doc, RasterLayer? only = null)
    {
        foreach (var node in doc.Descendants())
        {
            if (node is not RasterLayer layer) continue;
            if (only != null && !ReferenceEquals(layer, only)) continue;
            var cache = layer.FxCache;
            if (!layer.HasActiveEffects)
            {
                // 沒有作用中的效果：快取沒意義，之後再開時整層重算
                if (cache.Surface.TileCount > 0) cache.ClearTiles();
                cache.MarkAllDirty();
                cache.Rendered = false;
                cache.HasLastOffset = false;
                continue;
            }

            var effects = layer.Effects.Where(e => e.Enabled).ToList();
            var margin = 0;
            var wholeLayer = false;
            var canvasDependent = false;
            foreach (var e in effects)
            {
                var m = e.Effect.SourceMargin;
                if (m == EffectContext.WholeLayer) wholeLayer = true;
                else if (m > 0) margin += m;
                if (!e.Effect.IsPositionIndependent) canvasDependent = true;
            }

            // 物件是 doc 座標：Offset 變了而物件沒跟著動，物件在圖層座標裡就搬家了
            // （像素跟物件一起動時，物件的 ReplaceElement 已經標髒）。只標物件範圍，不整層重算。
            if (cache.HasLastOffset && cache.LastOffset != layer.Offset && layer.HasElements)
            {
                foreach (var el in layer.Elements)
                {
                    var b = el.Bounds;
                    if (b.IsEmpty) continue;
                    cache.MarkDirty(new SKRectI(b.Left - cache.LastOffset.X, b.Top - cache.LastOffset.Y,
                        b.Right - cache.LastOffset.X, b.Bottom - cache.LastOffset.Y));
                    cache.MarkDirty(new SKRectI(b.Left - layer.Offset.X, b.Top - layer.Offset.Y,
                        b.Right - layer.Offset.X, b.Bottom - layer.Offset.Y));
                }
            }
            cache.LastOffset = layer.Offset;
            cache.HasLastOffset = true;

            // 以畫布為框架的效果：畫布相對位置變了就得整層重算（其他效果與畫布無關）
            var canvasInLayer = new SKRectI(-layer.Offset.X, -layer.Offset.Y,
                doc.Width - layer.Offset.X, doc.Height - layer.Offset.Y);
            if (canvasDependent && canvasInLayer != cache.LastCanvas)
            {
                cache.MarkAllDirty();
                cache.LastCanvas = canvasInLayer;
            }

            if (!cache.HasPending) continue;

            // 快取範圍：內容 + 有限 margin；位置相關的效果再聯集畫布
            var region = ContentRegion(layer);
            if (!region.IsEmpty && margin > 0) region.Inflate(margin, margin);
            if (canvasDependent) region = region.IsEmpty ? canvasInLayer : SKRectI.Union(region, canvasInLayer);

            if (region.IsEmpty)
            {
                // 沒內容：快取就是空的（合成器拿到空表面 = 什麼都不畫），不必排工作
                cache.ClearTiles();
                cache.DirtyAll = false;
                cache.Dirty = SKRectI.Empty;
                cache.LastRegion = region;
                cache.Rendered = true;
                continue;
            }

            var full = cache.DirtyAll || wholeLayer;
            SKRectI write;
            SKRectI compute;
            if (full)
            {
                write = region;
                compute = region;
            }
            else
            {
                // 來源在 Dirty 變了 → 輸出在 Dirty+margin 內會變（外框畫到內容外）；
                // 算這塊又需要再往外 margin 的來源。寫回範圍不裁到 region：
                // 內容搬走後 region 縮了，舊位置的外框也要被清掉。
                write = cache.Dirty;
                write.Inflate(margin, margin);
                if (!cache.LastRegion.IsEmpty && !region.Contains(cache.LastRegion))
                    write = SKRectI.Union(write, cache.LastRegion); // 範圍縮了：舊範圍一併清
                compute = write;
                compute.Inflate(margin, margin);
                compute = SKRectI.Intersect(compute, region);
                if (compute.Width <= 0 || compute.Height <= 0) compute = SKRectI.Empty;
            }

            cache.DirtyAll = false;
            cache.Dirty = SKRectI.Empty;
            cache.LastRegion = region;
            cache.InFlight++;

            var masks = new List<byte[]?>(effects.Count);
            foreach (var e in effects)
                masks.Add(e.Mask == null || compute.IsEmpty ? null : ReadMask(e.Mask, compute, layer.Offset));

            return new Job
            {
                Layer = layer,
                Region = region,
                Compute = compute,
                Write = write,
                Full = full,
                Pixels = compute.IsEmpty ? [] : ReadPixelsWithElements(layer, compute),
                Effects = effects,
                Masks = masks,
                DocSize = new SKSizeI(doc.Width, doc.Height),
            };
        }
        return null;
    }

    private static uint[] Compute(Job job, CancellationToken ct)
    {
        var w = job.Compute.Width;
        var h = job.Compute.Height;
        var current = job.Pixels;
        if (job.Compute.IsEmpty) return current;
        for (var i = 0; i < job.Effects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = job.Effects[i];
            var ctx = new EffectContext(job.Compute, job.Compute, current, job.DocSize)
            {
                PrimaryColor = entry.Color,
                Cancellation = ct,
            };
            try
            {
                entry.Effect.Render(ctx);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue; // 單一效果壞掉不該拖垮整層：跳過它
            }

            var output = ctx.Dst;
            var mask = job.Masks[i];
            if (mask != null)
            {
                for (var p = 0; p < w * h; p++)
                {
                    var m = mask[p];
                    if (m == 0) output[p] = current[p];
                    else if (m < 255) output[p] = EffectMath.Lerp256(current[p], output[p], m + (m >> 7));
                }
            }
            current = output;
        }
        return current;
    }

    private static unsafe void WriteBackLocked(Document doc, Job job, uint[] result)
    {
        var layer = job.Layer;
        var cache = layer.FxCache;
        cache.InFlight--;
        Monitor.PulseAll(doc.SyncRoot);
        if (layer.Document != doc) return;
        var write = job.Write;
        var compute = job.Compute;
        var cw = compute.Width;

        if (job.Full) cache.ClearTiles(); // 整層重算：範圍外的舊 tile 一個都不留

        foreach (var idx in TileIndex.CoveringRect(write))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, write);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var inside = compute.IsEmpty ? SKRectI.Empty : SKRectI.Intersect(inter, compute);
            var hasInside = inside.Width > 0 && inside.Height > 0;
            if (!hasInside && cache.Surface.GetTileForRead(idx) == null) continue; // 本來就空，清也不用

            var tile = cache.Surface.GetTileForWrite(idx);
            var dst = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var dstRow = dst + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                var row = new Span<uint>(dstRow, inter.Width);
                if (!hasInside || y < inside.Top || y >= inside.Bottom)
                {
                    row.Clear();
                    continue;
                }
                // 左右在 compute 外的部分清透明，中間拷貝算好的結果
                if (inside.Left > inter.Left) row[..(inside.Left - inter.Left)].Clear();
                if (inside.Right < inter.Right) row[(inside.Right - inter.Left)..].Clear();
                var srcIndex = (y - compute.Top) * cw + (inside.Left - compute.Left);
                result.AsSpan(srcIndex, inside.Width).CopyTo(row.Slice(inside.Left - inter.Left, inside.Width));
            }
            if (tile.IsBlank()) cache.Surface.RemoveTile(idx);
        }

        cache.Rendered = true;
        var docRect = new SKRectI(
            write.Left + layer.Offset.X, write.Top + layer.Offset.Y,
            write.Right + layer.Offset.X, write.Bottom + layer.Offset.Y);
        layer.InvalidateComposite(docRect);
        LayerRendered?.Invoke(layer);
    }

    /// <summary>
    /// 單一物件套上這層效果堆疊的預覽（拖曳覆疊用）：只渲染這個物件、跑一遍堆疊，
    /// 回傳的範圍 = 物件外框外擴總 margin（doc 座標）。遮罩不看（拖曳中的近似）。
    /// 整層來源的效果（例如物件漸層）只看得到這個物件，對單一物件而言結果相同。
    /// </summary>
    public static unsafe uint[] RenderElementPreview(RasterLayer layer, Vectors.VectorElement element, out SKRectI bounds)
    {
        bounds = element.Bounds;
        var margin = TotalMargin(layer);
        bounds.Inflate(margin + 1, margin + 1);
        var pixels = new uint[Math.Max(0, bounds.Width * bounds.Height)];
        if (bounds.Width <= 0 || bounds.Height <= 0) return pixels;

        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, (IntPtr)ptr, bounds.Width * 4);
            if (surface == null) return pixels;
            var canvas = surface.Canvas;
            canvas.Translate(-bounds.Left, -bounds.Top);
            element.Render(canvas);
            canvas.Flush();
        }

        var effects = layer.Effects.Where(e => e.Enabled).ToList();
        if (effects.Count == 0) return pixels;
        var doc = layer.Document;
        var docSize = doc == null ? new SKSizeI(bounds.Width, bounds.Height) : new SKSizeI(doc.Width, doc.Height);
        var job = new Job
        {
            Layer = layer,
            Region = bounds,
            Compute = bounds,
            Write = bounds,
            Full = true,
            Pixels = pixels,
            Effects = effects,
            Masks = effects.Select(_ => (byte[]?)null).ToList(),
            DocSize = docSize,
        };
        return Compute(job, CancellationToken.None);
    }

    /// <summary>
    /// 效果堆疊的輸入 = 基底像素 + 這層的物件（文字）畫上去 —— 文字圖層的外框／陰影／光暈
    /// 全靠堆疊做，所以物件必須在效果之前併進來。畫布內編輯中被藏起來的物件不畫。
    /// </summary>
    public static unsafe uint[] ReadPixelsWithElements(RasterLayer layer, SKRectI rect)
    {
        var pixels = ReadPixels(layer.Surface, rect);
        if (!layer.HasElements || rect.Width <= 0 || rect.Height <= 0) return pixels;

        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, (IntPtr)ptr, rect.Width * 4);
            if (surface == null) return pixels;
            var canvas = surface.Canvas;
            // 物件是 doc 座標；rect 是圖層座標
            canvas.Translate(-rect.Left - layer.Offset.X, -rect.Top - layer.Offset.Y);
            var docRect = new SKRectI(rect.Left + layer.Offset.X, rect.Top + layer.Offset.Y,
                rect.Right + layer.Offset.X, rect.Bottom + layer.Offset.Y);
            foreach (var el in layer.Elements)
            {
                if (el.Id == layer.HiddenElementId) continue;
                if (!el.Bounds.IntersectsWith(docRect)) continue;
                el.Render(canvas);
            }
            canvas.Flush();
        }
        return pixels;
    }

    /// <summary>讀基底像素（圖層座標範圍）成 premul uint 陣列。</summary>
    public static unsafe uint[] ReadPixels(TileSurface surface, SKRectI rect)
    {
        var pixels = new uint[Math.Max(0, rect.Width * rect.Height)];
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = surface.GetTileForRead(idx);
            if (tile == null) continue;
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var src = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var srcRow = src + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                var dstIndex = (y - rect.Top) * rect.Width + (inter.Left - rect.Left);
                new ReadOnlySpan<uint>(srcRow, inter.Width).CopyTo(pixels.AsSpan(dstIndex, inter.Width));
            }
        }
        return pixels;
    }

    /// <summary>把 doc 座標的遮罩讀成圖層座標範圍的 byte 陣列。</summary>
    private static byte[] ReadMask(MaskSurface mask, SKRectI layerRect, SKPointI offset)
    {
        var result = new byte[layerRect.Width * layerRect.Height];
        var docRect = new SKRectI(layerRect.Left + offset.X, layerRect.Top + offset.Y,
            layerRect.Right + offset.X, layerRect.Bottom + offset.Y);
        foreach (var (idx, tile) in mask.Tiles)
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, docRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                Array.Copy(tile.Alpha, (y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left),
                    result, (y - docRect.Top) * layerRect.Width + (inter.Left - docRect.Left), inter.Width);
            }
        }
        return result;
    }
}
