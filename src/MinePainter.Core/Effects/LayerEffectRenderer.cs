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
    internal bool LastClipped;                   // 上次的範圍被畫布裁掉過（＝這份快取與畫布位置有關）
    internal SKRectI LastCanvas = SKRectI.Empty; // 上次計算時畫布在圖層座標的範圍（只有「位置相關」效果在乎）
    internal SKPointI LastOffset;                // 上次計算時的圖層 Offset（物件是 doc 座標，Offset 變了物件在圖層座標就動了）
    internal bool HasLastOffset;

    public bool HasPending => DirtyAll || !Dirty.IsEmpty;

    /// <summary>已被取走、還沒寫回的工作數（worker 鎖外計算中）。同步等待者靠它判斷「真的算完了」。</summary>
    internal int InFlight;

    /// <summary>
    /// 每次被標髒就 +1。worker 在鎖外算的時候拿它比對：算到一半又被標髒，
    /// 這份結果寫回去也是舊的（而且馬上會被重算），不如當場放棄、把髒區還回去重來。
    /// </summary>
    private int _generation;

    internal int Generation => Volatile.Read(ref _generation);

    public void MarkDirty(SKRectI layerRect)
    {
        if (layerRect.Width <= 0 || layerRect.Height <= 0) return;
        Interlocked.Increment(ref _generation);
        if (DirtyAll) return;
        Dirty = Dirty.IsEmpty ? layerRect : SKRectI.Union(Dirty, layerRect);
    }

    public void MarkAllDirty()
    {
        Interlocked.Increment(ref _generation);
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
    public static event Action<LayerNode>? LayerRendered;

    /// <summary>
    /// 群組效果的來源像素：把這一組合成起來的樣子讀成 doc 座標的緩衝區。
    /// 由合成器提供（它才有筆劃緩衝／浮動內容那些進行中的預覽）；在 Document.SyncRoot 內呼叫。
    /// </summary>
    public delegate uint[] GroupPixelReader(GroupLayer group, SKRectI docRect);

    private sealed class Job
    {
        public required LayerNode Layer;
        public required SKRectI Region;   // 快取涵蓋的範圍（圖層座標：內容＋效果外擴）
        public required SKRectI Compute;  // 這次算的範圍（圖層座標）
        public required SKRectI Write;    // 寫回的範圍（圖層座標；Compute 以外的部分清成透明）
        public required bool Full;        // 整層重算（寫回前先清空快取）
        public required uint[] Pixels;    // Compute 範圍的基底像素
        public required List<LayerEffect> Effects;
        public required List<byte[]?> Masks; // 每個效果在 Compute 範圍內的遮罩（null = 整層）
        public required SKSizeI DocSize;
        public float ContentRotation;        // 這層唯一的文字物件的角度（見 EffectContext.ContentRotation）
        public int Generation;               // 取這份工作時的髒區版本（見 LayerEffectCache.Generation）

        /// <summary>只有「合成器排進來的」工作可以中途放棄；拖曳預覽那種一次性的算完就是要用。</summary>
        public bool AbandonWhenStale;

        /// <summary>取走之後又被標髒了：算完也是舊的，白算。</summary>
        public bool IsStale => AbandonWhenStale && Layer.FxCache.Generation != Generation;
    }

    /// <summary>算完所有待處理的圖層；回傳是否有任何圖層被更新（呼叫端據此決定要不要再跑一輪）。</summary>
    public static bool RenderPending(Document doc, CancellationToken ct = default,
        GroupPixelReader? groupReader = null)
    {
        var any = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Job? job;
            lock (doc.SyncRoot)
            {
                job = TakeJobLocked(doc, groupReader: groupReader);
            }
            if (job == null) return any;

            uint[]? result;
            try
            {
                result = Compute(job, ct);
            }
            catch
            {
                lock (doc.SyncRoot) AbandonLocked(doc, job); // 取消／炸掉：把工作還回去，等待者才不會卡死
                throw;
            }

            if (result == null)
            {
                // 算到一半又被標髒：放棄這份，下一輪用新的髒區重算（省下寫回與一次多餘的重算）
                lock (doc.SyncRoot) AbandonLocked(doc, job);
                continue;
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
    public static void RenderLayerNow(Document doc, LayerNode layer, GroupPixelReader? groupReader = null)
    {
        while (true)
        {
            Job? job;
            lock (doc.SyncRoot)
            {
                if (!layer.HasActiveEffects || layer.Document != doc) return;
                job = TakeJobLocked(doc, layer, groupReader);
                if (job == null)
                {
                    if (layer.FxCache.InFlight <= 0) return;
                    Monitor.Wait(doc.SyncRoot, 20); // worker 寫回時 PulseAll；逾時再檢查一次
                    continue;
                }
            }
            uint[]? result;
            try
            {
                result = Compute(job, CancellationToken.None);
            }
            catch
            {
                lock (doc.SyncRoot) AbandonLocked(doc, job);
                throw;
            }
            if (result == null)
            {
                lock (doc.SyncRoot) AbandonLocked(doc, job); // 被蓋過了，下一圈重算
                continue;
            }
            lock (doc.SyncRoot)
            {
                WriteBackLocked(doc, job, result);
            }
        }
    }

    /// <summary>
    /// 同步把整份文件的效果堆疊算到最新（匯出／離線合成用）。
    /// 與 <see cref="RenderPending"/> 的差別：合成器 worker 已取走、正在鎖外算的工作也會等它寫回，
    /// 否則 RenderComposite 會拿到「效果尚未套用」的基底像素（偶發）。
    /// </summary>
    public static void RenderAllNow(Document doc, GroupPixelReader? groupReader = null)
    {
        List<LayerNode> layers;
        // 由內而外：群組效果的來源是「這一組合成起來的樣子」，子層要先算完
        lock (doc.SyncRoot) layers = EffectOrder(doc).Where(l => l.HasActiveEffects).ToList();
        foreach (var layer in layers) RenderLayerNow(doc, layer, groupReader);
    }

    /// <summary>
    /// 效果的計算順序：後序（子層先於它所在的群組）。
    /// 群組效果吃的是子層算完之後的樣子，順序反了會先用舊的算一次再重算。
    /// </summary>
    private static IEnumerable<LayerNode> EffectOrder(Document doc) => EffectOrderOf(doc.Root);

    private static IEnumerable<LayerNode> EffectOrderOf(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            if (child is GroupLayer g)
            {
                foreach (var nested in EffectOrderOf(g)) yield return nested;
            }
            yield return child;
        }
    }

    /// <summary>作用中效果的總 margin（有限值相加；整層來源的效果不計）。</summary>
    /// <summary>
    /// 一個效果在快取上要留的餘裕：來源餘裕（讀得到足夠的鄰域）與輸出餘裕（結果長到內容外）取大。
    /// 整層來源的效果（漸層外框）來源餘裕不算數，靠輸出餘裕撐住快取範圍。
    /// </summary>
    private static int EffectMargin(IEffect effect)
    {
        var src = effect.SourceMargin;
        return Math.Max(src == EffectContext.WholeLayer ? 0 : Math.Max(0, src), effect.OutputMargin);
    }

    public static int TotalMargin(LayerNode layer)
    {
        var margin = 0;
        foreach (var e in layer.Effects)
        {
            if (!e.Enabled) continue;
            margin += EffectMargin(e.Effect);
        }
        return margin;
    }

    /// <summary>
    /// 圖層內容在圖層座標的範圍（基底像素 ∪ 物件，不含畫布內編輯中被藏起來的物件）。
    /// </summary>
    public static SKRectI ContentRegion(LayerNode node)
    {
        // 群組：效果吃的是整組合成起來的樣子，範圍就是這一組的內容（doc 座標＝快取座標）
        if (node is not RasterLayer layer) return node.ContentBounds;

        var bounds = layer.Surface.ContentBounds;
        foreach (var el in layer.Elements)
        {
            if (layer.ElementsHidden || el.Id == layer.HiddenElementId) continue;
            var b = el.Bounds;
            if (b.IsEmpty) continue;
            var lb = new SKRectI(b.Left - layer.Offset.X, b.Top - layer.Offset.Y,
                b.Right - layer.Offset.X, b.Bottom - layer.Offset.Y);
            bounds = bounds.IsEmpty ? lb : SKRectI.Union(bounds, lb);
        }
        return bounds;
    }

    private static Job? TakeJobLocked(Document doc, LayerNode? only = null, GroupPixelReader? groupReader = null)
    {
        foreach (var layer in EffectOrder(doc))
        {
            if (!layer.CanHaveEffects) continue;
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
                if (e.Effect.SourceMargin == EffectContext.WholeLayer) wholeLayer = true;
                margin += EffectMargin(e.Effect);
                if (!e.Effect.IsPositionIndependent) canvasDependent = true;
            }

            // 物件是 doc 座標：Offset 變了而物件沒跟著動，物件在圖層座標裡就搬家了
            // （像素跟物件一起動時，物件的 ReplaceElement 已經標髒）。只標物件範圍，不整層重算。
            if (layer is RasterLayer { HasElements: true } withElements &&
                cache.HasLastOffset && cache.LastOffset != layer.EffectOffset)
            {
                foreach (var el in withElements.Elements)
                {
                    var b = el.Bounds;
                    if (b.IsEmpty) continue;
                    cache.MarkDirty(new SKRectI(b.Left - cache.LastOffset.X, b.Top - cache.LastOffset.Y,
                        b.Right - cache.LastOffset.X, b.Bottom - cache.LastOffset.Y));
                    cache.MarkDirty(new SKRectI(b.Left - withElements.Offset.X, b.Top - withElements.Offset.Y,
                        b.Right - withElements.Offset.X, b.Bottom - withElements.Offset.Y));
                }
            }
            cache.LastOffset = layer.EffectOffset;
            cache.HasLastOffset = true;

            var canvasInLayer = new SKRectI(-layer.EffectOffset.X, -layer.EffectOffset.Y,
                doc.Width - layer.EffectOffset.X, doc.Height - layer.EffectOffset.Y);

            // 只算「看得到的那塊」：畫布外的部分算了也永遠不會被合成到（合成器只走畫布內的
            // tile），可是成本照付 —— 一個大部分在畫布外的大物件，效果堆疊每次都在算不存在的
            // 畫面（實測：完全在畫布外的 3200×370 文字，外框＋陰影仍要 113 ms）。
            // 往外留 margin：畫布外的內容，它的外框／陰影還是可能伸進畫布裡。
            // 上次算的範圍被裁過（或效果本身看畫布）＝這份快取與「畫布落在圖層的哪裡」有關：
            // 圖層一平移，看得到的那一塊就換人，得重算（沒裁到的話快取與畫布無關，平移不必重算）。
            if ((canvasDependent || cache.LastClipped) && canvasInLayer != cache.LastCanvas) cache.MarkAllDirty();
            cache.LastCanvas = canvasInLayer;

            if (!cache.HasPending) continue;

            // 先對齊 tile 再加 margin：內容範圍是「tile 粒度的內容框再外擴 margin」，
            // 視窗要蓋得住同一個算法，完全在畫布內的圖層才不會被誤判成被裁到。
            var visibleWindow = SnapOutToTiles(canvasInLayer);
            if (margin > 0) visibleWindow.Inflate(margin, margin);

            var content = ContentRegion(layer);
            if (!content.IsEmpty && margin > 0) content.Inflate(margin, margin);

            // 只有「比畫布大很多」才裁。裁過的快取蓋不到整個物件，拖曳快照就得現算一次
            // （或先顯示沒有效果的樣子再換上），使用者看到的就是拖一下閃一下。
            // 稍微超出畫布的物件整份算完便宜得多，也讓拖曳／旋轉的快照永遠是完整的。
            var canvasArea = (long)Math.Max(1, canvasInLayer.Width) * Math.Max(1, canvasInLayer.Height);
            var contentArea = (long)Math.Max(0, content.Width) * Math.Max(0, content.Height);
            var worthClipping = contentArea > canvasArea * 4;

            var region = content.IsEmpty || !worthClipping
                ? content
                : SKRectI.Intersect(content, visibleWindow);
            if (region.Width <= 0 || region.Height <= 0) region = SKRectI.Empty;
            cache.LastClipped = !content.IsEmpty && region != content;
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
                masks.Add(e.Mask == null || compute.IsEmpty ? null : ReadMask(e.Mask, compute, layer.EffectOffset));

            return new Job
            {
                Layer = layer,
                Region = region,
                Compute = compute,
                Write = write,
                Full = full,
                Pixels = compute.IsEmpty ? [] : ReadSourceLocked(layer, compute, groupReader),
                Effects = effects,
                Masks = masks,
                DocSize = new SKSizeI(doc.Width, doc.Height),
                ContentRotation = ContentRotationOf(layer),
                Generation = cache.Generation,
                AbandonWhenStale = true,
            };
        }
        return null;
    }

    /// <summary>
    /// 把可見視窗往外對齊到 tile 邊界。點陣圖層的內容範圍本身就是 tile 粒度
    /// （<see cref="Tiles.TileSurface.ContentBounds"/>，256 的倍數），視窗不對齊的話
    /// 連完全在畫布內的小圖層都會被判成「被裁到」，白白失去「平移不必重算」這個性質。
    /// </summary>
    private static SKRectI SnapOutToTiles(SKRectI rect)
    {
        static int Floor(int v) => (int)Math.Floor(v / (double)Tile.Size) * Tile.Size;
        static int Ceil(int v) => (int)Math.Ceiling(v / (double)Tile.Size) * Tile.Size;
        return new SKRectI(Floor(rect.Left), Floor(rect.Top), Ceil(rect.Right), Ceil(rect.Bottom));
    }

    /// <summary>
    /// 這層內容自己的角度：只有「這層剛好就是一個文字物件」時才說得準（文字圖層的常態）。
    /// 其他情況（多個物件、純像素、群組）回 0 ＝不轉。
    /// </summary>
    private static float ContentRotationOf(LayerNode layer) =>
        layer is RasterLayer { Elements.Count: 1 } single && single.Elements[0] is Vectors.TextElement text
            ? text.Rotation
            : 0f;

    /// <summary>
    /// 效果堆疊的來源像素：點陣圖層＝基底像素＋這層的物件；群組＝整組合成起來的樣子。
    /// 在 Document.SyncRoot 內呼叫。
    /// </summary>
    private static uint[] ReadSourceLocked(LayerNode layer, SKRectI rect, GroupPixelReader? groupReader)
    {
        if (layer is RasterLayer raster) return ReadPixelsWithElements(raster, rect);
        if (layer is not GroupLayer group) return new uint[Math.Max(0, rect.Width * rect.Height)];
        // 沒給讀取器（烙印、預覽、測試直接呼叫）就用離線版：不含進行中的筆劃／浮動內容
        return (groupReader ?? Compositing.Compositor.StaticGroupSourceLocked)(group, rect);
    }

    /// <summary>回傳 null＝這份工作在計算途中就被新的髒區蓋過了（放棄，髒區由呼叫端還回去）。</summary>
    private static uint[]? Compute(Job job, CancellationToken ct)
    {
        var w = job.Compute.Width;
        var h = job.Compute.Height;
        var current = job.Pixels;
        if (job.Compute.IsEmpty) return current;
        for (var i = 0; i < job.Effects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // 一道效果算完就看一次：後面還有好幾道的話，早點放棄省下的是整整幾十毫秒
            if (job.IsStale) return null;
            var entry = job.Effects[i];
            var ctx = new EffectContext(job.Compute, job.Compute, current, job.DocSize)
            {
                PrimaryColor = entry.Color,
                Cancellation = ct,
                ContentRotation = job.ContentRotation,
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
        var off = layer.EffectOffset;
        var docRect = new SKRectI(
            write.Left + off.X, write.Top + off.Y,
            write.Right + off.X, write.Bottom + off.Y);
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
            ContentRotation = element is Vectors.TextElement rotated ? rotated.Rotation : 0f,
        };
        return Compute(job, CancellationToken.None) ?? pixels;
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
                if (layer.ElementsHidden || el.Id == layer.HiddenElementId) continue;
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
