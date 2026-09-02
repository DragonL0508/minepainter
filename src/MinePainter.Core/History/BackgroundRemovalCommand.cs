using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.History;

public sealed record BackgroundRemovalOptions
{
    public required OnnxModelInfo Model { get; init; }
    public bool UseGpu { get; init; }
    /// <summary>用高清原圖做引導濾波精修遮罩邊緣（見 <see cref="GuidedFilter"/>）。</summary>
    public bool RefineEdges { get; init; } = true;
    /// <summary>精修半徑（全解析度 px）。</summary>
    public int RefineRadius { get; init; } = 16;
    /// <summary>遮罩對比 0..100（去掉半透明的殘影）。</summary>
    public int Contrast { get; init; }
    /// <summary>邊緣收縮（負）／擴張（正）px。</summary>
    public int Shift { get; init; }
}

/// <summary>
/// 圖層 → AI 去背：把圖層先平面化（效果堆疊烙印、文字物件柵格化）成純像素，
/// 再用模型算前景遮罩、乘到 alpha 上。整個是一步 undo。
///
/// 模型只吃 1024（u2net 甚至 320）解析度，所以遮罩本身是低解析度放大回來的：
/// 顏色像素一直都是原圖，糊掉的是 alpha 邊緣。「精修邊緣」用原圖當引導做引導濾波，
/// 讓遮罩重新貼回高清像素的邊緣（等同「先留一份高清原圖、去背後再依不透明範圍回原圖取像素」，
/// 但連半透明的髮絲邊也一起處理）。
///
/// 只推論內容外接框（透明邊不送進模型），模型的 1024 解析度全用在物件上。
/// </summary>
public static class BackgroundRemovalCommand
{
    /// <summary>
    /// 執行。長時間工作在呼叫端的背景執行緒上跑；只在讀寫圖層時短暫持鎖。
    /// 回傳 false = 圖層沒有內容、沒有動作。
    /// </summary>
    public static bool Run(EditorSession session, RasterLayer layer, BackgroundRemovalOptions options,
        CancellationToken ct = default)
    {
        var doc = session.Document;
        if (layer.Document != doc) return false;

        // ---- 1. 平面化（鎖內）----
        // 效果快取要先是最新的；RenderLayerNow 會等 worker 正在算的工作
        if (layer.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(doc, layer);

        TileSnapshot before;
        var effectsBefore = layer.Effects;
        Vectors.VectorElement[] elementsBefore;
        SKRectI affected; // 圖層座標
        lock (doc.SyncRoot)
        {
            before = layer.Surface.Snapshot();
            affected = layer.Surface.ContentBounds;

            if (layer.HasActiveEffects && layer.FxCache.Rendered)
            {
                var fxBounds = layer.FxCache.Surface.ContentBounds;
                if (!fxBounds.IsEmpty)
                {
                    LayerEffectCommands.CopyRegion(layer.FxCache.Surface, layer.Surface, fxBounds);
                    affected = Union(affected, fxBounds);
                }
            }
            if (layer.Effects.Count > 0) layer.SetEffects([]);

            elementsBefore = layer.Elements.ToArray();
            if (elementsBefore.Length > 0)
            {
                var rect = LayerCommands.RasterizeElementsLocked(layer, elementsBefore);
                affected = Union(affected, rect);
                foreach (var el in elementsBefore) layer.RemoveElement(el.Id);
            }
        }

        try
        {
            // ---- 2. 讀內容外接框的像素（鎖內，很快）----
            SKRectI crop;
            uint[] pixels;
            lock (doc.SyncRoot)
            {
                crop = layer.Surface.ExactContentBounds();
                if (crop.IsEmpty)
                {
                    Rollback();
                    return false;
                }
                pixels = ReadRegion(layer.Surface, crop);
            }

            // ---- 3. 推論 + 後處理（鎖外）----
            var mask = BackgroundRemover.Infer(options.Model, pixels, crop.Width, crop.Height, options.UseGpu, ct);
            ct.ThrowIfCancellationRequested();
            if (options.RefineEdges)
                mask = GuidedFilter.Refine(mask, pixels, crop.Width, crop.Height, options.RefineRadius, ct: ct);
            BackgroundRemover.ApplyContrast(mask, options.Contrast);
            mask = BackgroundRemover.Shift(mask, crop.Width, crop.Height, options.Shift);
            ct.ThrowIfCancellationRequested();

            // ---- 4. 套 alpha（鎖內）----
            lock (doc.SyncRoot)
            {
                if (layer.Document != doc) { Rollback(); return false; }
                ApplyMask(layer.Surface, crop, mask);
                affected = Union(affected, crop);

                var pixelEntry = TileDeltaEntry.Capture("AI 去背", layer, before, affected);
                var stateEntry = new ActionHistoryEntry("AI 去背", doc.Bounds,
                    undo: d =>
                    {
                        lock (d.SyncRoot)
                        {
                            layer.SetEffects(effectsBefore);
                            foreach (var el in elementsBefore) layer.AddElement(el);
                        }
                        layer.InvalidateAll();
                    },
                    redo: d =>
                    {
                        lock (d.SyncRoot)
                        {
                            layer.SetEffects([]);
                            foreach (var el in elementsBefore) layer.RemoveElement(el.Id);
                        }
                        layer.InvalidateAll();
                    });
                session.History.Push(pixelEntry != null
                    ? new CompositeHistoryEntry("AI 去背", pixelEntry, stateEntry)
                    : stateEntry);
            }
            layer.InvalidateAll();
            return true;
        }
        catch
        {
            Rollback();
            throw;
        }
        finally
        {
            before.Dispose();
        }

        void Rollback()
        {
            lock (doc.SyncRoot)
            {
                foreach (var idx in TileIndex.CoveringRect(affected))
                    layer.Surface.RestoreTile(idx, before.GetTile(idx));
                layer.SetEffects(effectsBefore);
                foreach (var el in elementsBefore)
                    if (layer.Elements.All(e => e.Id != el.Id)) layer.AddElement(el);
            }
            layer.InvalidateAll();
        }
    }

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);

    private static unsafe uint[] ReadRegion(TileSurface surface, SKRectI rect)
    {
        var pixels = new uint[rect.Width * rect.Height];
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
                new ReadOnlySpan<uint>(srcRow, inter.Width)
                    .CopyTo(pixels.AsSpan((y - rect.Top) * rect.Width + (inter.Left - rect.Left), inter.Width));
            }
        }
        return pixels;
    }

    /// <summary>premul 像素四通道乘上 mask/255（rect 為圖層座標）。</summary>
    private static unsafe void ApplyMask(TileSurface surface, SKRectI rect, byte[] mask)
    {
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            if (surface.GetTileForRead(idx) == null) continue;
            var tile = surface.GetTileForWrite(idx);
            var px = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = px + (y - tileRect.Top) * Tile.Size;
                var mrow = (y - rect.Top) * rect.Width;
                for (var x = inter.Left; x < inter.Right; x++)
                {
                    var m = mask[mrow + (x - rect.Left)];
                    if (m == 255) continue;
                    ref var p = ref row[x - tileRect.Left];
                    if (m == 0) { p = 0; continue; }
                    var mul = m + (m >> 7); // 0..256
                    var b = (int)(p & 0xFF) * mul >> 8;
                    var g = (int)((p >> 8) & 0xFF) * mul >> 8;
                    var r = (int)((p >> 16) & 0xFF) * mul >> 8;
                    var a = (int)(p >> 24) * mul >> 8;
                    p = (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
                }
            }
            if (tile.IsBlank()) surface.RemoveTile(idx);
        }
    }
}
