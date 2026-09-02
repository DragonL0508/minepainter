using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 圖層效果堆疊的快取：堆疊套用後的像素（圖層座標，只涵蓋畫布範圍）＋髒區。
/// 存取一律在 Document.SyncRoot 內。
/// </summary>
public sealed class LayerEffectCache : IDisposable
{
    public TileSurface Surface { get; } = new();

    /// <summary>已至少完整算過一次（合成器才會拿快取而不是基底像素）。</summary>
    public bool Rendered { get; internal set; }

    internal bool DirtyAll = true;
    internal SKRectI Dirty = SKRectI.Empty;      // 圖層座標
    internal SKRectI LastRegion = SKRectI.Empty; // 上次計算時畫布在圖層座標的範圍（位移／畫布尺寸變了要整層重算）

    public bool HasPending => DirtyAll || !Dirty.IsEmpty;

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

    public void Dispose() => Surface.Dispose();
}

/// <summary>
/// 在合成器 worker 上把圖層效果堆疊算進快取：鎖內抓來源像素與堆疊快照、鎖外計算、鎖內寫回。
/// 只重算髒區（外擴兩倍總 margin 吸收邊緣誤差）；有「位置相關」或「整層來源」的效果時整層重算。
/// </summary>
public static class LayerEffectRenderer
{
    private sealed class Job
    {
        public required RasterLayer Layer;
        public required SKRectI Region;   // 畫布在圖層座標的範圍
        public required SKRectI Compute;  // 這次算的範圍（圖層座標）
        public required SKRectI Write;    // 寫回的範圍（圖層座標）
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

            var result = Compute(job, ct);

            lock (doc.SyncRoot)
            {
                WriteBackLocked(doc, job, result);
            }
            any = true;
        }
    }

    /// <summary>同步把某一層算到最新（烙印／匯出前用）。</summary>
    public static void RenderLayerNow(Document doc, RasterLayer layer)
    {
        while (true)
        {
            Job? job;
            lock (doc.SyncRoot)
            {
                job = layer.HasActiveEffects && layer.Document == doc ? TakeJobLocked(doc, layer) : null;
            }
            if (job == null) return;
            var result = Compute(job, CancellationToken.None);
            lock (doc.SyncRoot)
            {
                WriteBackLocked(doc, job, result);
            }
        }
    }

    private static Job? TakeJobLocked(Document doc, RasterLayer? only = null)
    {
        foreach (var node in doc.Descendants())
        {
            if (node is not RasterLayer layer) continue;
            if (only != null && !ReferenceEquals(layer, only)) continue;
            if (!layer.HasActiveEffects)
            {
                // 沒有作用中的效果：快取沒意義，之後再開時整層重算
                layer.FxCache.MarkAllDirty();
                layer.FxCache.Rendered = false;
                continue;
            }
            var cache = layer.FxCache;
            if (!cache.HasPending) continue;

            var region = new SKRectI(-layer.Offset.X, -layer.Offset.Y,
                doc.Width - layer.Offset.X, doc.Height - layer.Offset.Y);
            if (region != cache.LastRegion)
            {
                cache.MarkAllDirty();
                cache.LastRegion = region;
            }

            var effects = layer.Effects.Where(e => e.Enabled).ToList();
            var full = cache.DirtyAll || effects.Any(e => !e.Effect.IsPositionIndependent || e.Effect.SourceMargin == EffectContext.WholeLayer);
            SKRectI write;
            SKRectI compute;
            if (full)
            {
                write = region;
                compute = region;
            }
            else
            {
                write = SKRectI.Intersect(cache.Dirty, region);
                if (write.Width <= 0 || write.Height <= 0)
                {
                    cache.Dirty = SKRectI.Empty;
                    continue;
                }
                var margin = 0;
                foreach (var e in effects) margin += Math.Max(0, e.Effect.SourceMargin);
                compute = write;
                compute.Inflate(margin * 2 + 1, margin * 2 + 1);
                compute = SKRectI.Intersect(compute, region);
            }

            cache.DirtyAll = false;
            cache.Dirty = SKRectI.Empty;

            var masks = new List<byte[]?>(effects.Count);
            foreach (var e in effects)
                masks.Add(e.Mask == null ? null : ReadMask(e.Mask, compute, layer.Offset));

            return new Job
            {
                Layer = layer,
                Region = region,
                Compute = compute,
                Write = write,
                Pixels = ReadPixelsWithElements(layer, compute),
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
        if (layer.Document != doc) return;
        var cache = layer.FxCache;
        var write = job.Write;
        var cw = job.Compute.Width;

        foreach (var idx in TileIndex.CoveringRect(write))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, write);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var tile = cache.Surface.GetTileForWrite(idx);
            var dst = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var srcIndex = (y - job.Compute.Top) * cw + (inter.Left - job.Compute.Left);
                var dstRow = dst + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                result.AsSpan(srcIndex, inter.Width).CopyTo(new Span<uint>(dstRow, inter.Width));
            }
            if (tile.IsBlank()) cache.Surface.RemoveTile(idx);
        }

        cache.Rendered = true;
        var docRect = new SKRectI(
            write.Left + layer.Offset.X, write.Top + layer.Offset.Y,
            write.Right + layer.Offset.X, write.Bottom + layer.Offset.Y);
        layer.InvalidateComposite(docRect);
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
