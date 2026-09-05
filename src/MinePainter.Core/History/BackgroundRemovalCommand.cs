using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.History;

public sealed record BackgroundRemovalOptions
{
    /// <summary>本機 ONNX 模型；用 remove.bg 時（<see cref="RemoveBg"/> 有值）可以是 null。</summary>
    public OnnxModelInfo? Model { get; init; }
    /// <summary>
    /// 走 remove.bg 線上服務（同 paint.net 的 Remove Background 插件）。有值時不用本機模型：
    /// 伺服器結果只取 alpha 當遮罩，顏色仍是原圖（原解析度）；伺服器只回預覽尺寸時，
    /// 遮罩放大後用原圖做引導濾波貼回真實邊緣。填實／對比／收縮照常套用。
    /// </summary>
    public RemoveBgOptions? RemoveBg { get; init; }
    public bool UseGpu { get; init; } = true;
    /// <summary>引導濾波精修半徑（全解析度 px；一律精修，見 <see cref="GuidedFilter"/>）。</summary>
    public int RefineRadius { get; init; } = 16;
    /// <summary>
    /// 內部填實：離邊界超過 <see cref="RefineRadius"/> 的內部一律不透明、外部一律透明，
    /// 只在邊緣一圈保留半透明（否則模型的機率圖會讓物件內部變半透明）。
    /// </summary>
    public bool SolidCore { get; init; } = true;
    /// <summary>遮罩對比 0..100（去掉半透明的殘影）。</summary>
    public int Contrast { get; init; }
    /// <summary>邊緣收縮（負）／擴張（正）px。</summary>
    public int Shift { get; init; }
    /// <summary>
    /// 只處理選取範圍（doc 座標；null = 整個圖層）。
    /// 有給時只把選取範圍內的像素送進模型（範圍外對模型是黑），模型的解析度全用在使用者圈出的物件上；
    /// 選取範圍外的像素一律清成透明，選取的軟邊（羽化／抗鋸齒）也乘進遮罩。
    /// </summary>
    public Selections.SelectionMask? Selection { get; init; }
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
            byte[]? coverage = null; // 選取覆蓋度（crop 內、圖層座標）
            var selection = options.Selection is { IsEmpty: false } s ? s : null;
            lock (doc.SyncRoot)
            {
                crop = layer.Surface.ExactContentBounds();
                if (selection != null)
                {
                    // 選取是 doc 座標、圖層像素是圖層座標：把選取外接框搬到圖層座標再交集
                    var sb = selection.Bounds;
                    var selInLayer = new SKRectI(sb.Left - layer.Offset.X, sb.Top - layer.Offset.Y,
                        sb.Right - layer.Offset.X, sb.Bottom - layer.Offset.Y);
                    crop = SKRectI.Intersect(crop, selInLayer);
                }
                if (crop.Width <= 0 || crop.Height <= 0)
                {
                    Rollback();
                    return false;
                }
                pixels = ReadRegion(layer.Surface, crop);
                if (selection != null)
                {
                    coverage = ReadCoverage(selection, crop, layer.Offset);
                    // 範圍外的像素不給模型看（透明 = 黑），讓它只專心在圈出來的東西上
                    for (var i = 0; i < pixels.Length; i++)
                        if (coverage[i] != 255) pixels[i] = Scale(pixels[i], coverage[i]);
                }
            }

            // ---- 3. 推論 + 後處理（鎖外）----
            // 前景機率圖（來源尺寸）：本機模型推論，或 remove.bg 結果的 alpha
            byte[] model;
            var refine = true; // 遮罩是低解析度放大來的才需要用原圖精修邊緣
            if (options.RemoveBg is { } remote)
            {
                var result = RemoveBgClient.Cutout(pixels, crop.Width, crop.Height, remote, ct);
                model = result.Alpha;
                refine = result.Downscaled(crop.Width, crop.Height);
                var charged = RemoveBgClient.LastCreditsCharged;
                BackgroundRemover.LastPlanNote =
                    $"remove.bg 回傳 {result.ServerWidth}×{result.ServerHeight}" +
                    (refine ? $"（{(charged is 0 ? "帳號沒有點數，只給預覽解析度；" : "")}已用原圖精修放大回 {crop.Width}×{crop.Height}）" : "") +
                    (charged is { } c ? $"，扣 {c:0.##} 點" : "");
            }
            else
            {
                var localModel = options.Model ?? throw new InvalidOperationException("沒有指定去背模型");
                model = BackgroundRemover.Infer(localModel, pixels, crop.Width, crop.Height, options.UseGpu, ct);
            }
            ct.ThrowIfCancellationRequested();

            // 精修半徑隨圖片大小放大：模型的一個像素在大圖上是好幾個像素
            var scale = Math.Max(1, (int)MathF.Ceiling(Math.Max(crop.Width, crop.Height) / 1024f));
            var radius = Math.Max(options.RefineRadius, 6 * scale);
            var mask = refine ? GuidedFilter.Refine(model, pixels, crop.Width, crop.Height, radius, ct: ct) : (byte[])model.Clone();
            if (options.SolidCore)
                mask = BackgroundRemover.SolidifyCore(mask, model, crop.Width, crop.Height, radius);
            BackgroundRemover.ApplyContrast(mask, options.Contrast);
            mask = BackgroundRemover.Shift(mask, crop.Width, crop.Height, options.Shift);
            if (coverage != null)
                for (var i = 0; i < mask.Length; i++)
                    if (coverage[i] != 255) mask[i] = (byte)(mask[i] * coverage[i] / 255);
            ct.ThrowIfCancellationRequested();

            // ---- 4. 套 alpha（鎖內）：顏色永遠是原圖的原解析度像素，只乘上遮罩 ----
            lock (doc.SyncRoot)
            {
                if (layer.Document != doc) { Rollback(); return false; }
                ApplyMask(layer.Surface, crop, mask);
                affected = Union(affected, crop);
                if (selection != null)
                {
                    // 選取範圍外（crop 之外的所有內容）一律清掉
                    var content = layer.Surface.ContentBounds;
                    ClearOutside(layer.Surface, crop);
                    affected = Union(affected, content);
                }

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

    /// <summary>讀選取在 rect（圖層座標）內的覆蓋度；選取本身是 doc 座標，差一個圖層位移。</summary>
    private static byte[] ReadCoverage(Selections.SelectionMask selection, SKRectI rect, SKPointI layerOffset)
    {
        var cov = new byte[rect.Width * rect.Height];
        for (var y = 0; y < rect.Height; y++)
        {
            var docY = rect.Top + y + layerOffset.Y;
            for (var x = 0; x < rect.Width; x++)
                cov[y * rect.Width + x] = selection.CoverageAt(rect.Left + x + layerOffset.X, docY);
        }
        return cov;
    }

    /// <summary>把 keep（圖層座標）以外的所有像素清成透明；整個 tile 都在外面就直接移除。</summary>
    private static unsafe void ClearOutside(TileSurface surface, SKRectI keep)
    {
        foreach (var idx in TileIndex.CoveringRect(surface.ContentBounds))
        {
            if (surface.GetTileForRead(idx) == null) continue;
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, keep);
            if (inter.Width <= 0 || inter.Height <= 0)
            {
                surface.RemoveTile(idx);
                continue;
            }
            if (inter == tileRect) continue; // 整塊都在保留範圍內
            var tile = surface.GetTileForWrite(idx);
            var px = (uint*)tile.Pixels;
            for (var y = tileRect.Top; y < tileRect.Bottom; y++)
            {
                var row = px + (y - tileRect.Top) * Tile.Size;
                if (y < inter.Top || y >= inter.Bottom)
                {
                    new Span<uint>(row, Tile.Size).Clear();
                    continue;
                }
                if (inter.Left > tileRect.Left)
                    new Span<uint>(row, inter.Left - tileRect.Left).Clear();
                if (inter.Right < tileRect.Right)
                    new Span<uint>(row + (inter.Right - tileRect.Left), tileRect.Right - inter.Right).Clear();
            }
            if (tile.IsBlank()) surface.RemoveTile(idx);
        }
    }

    /// <summary>premul 像素四通道乘上 m/255。</summary>
    private static uint Scale(uint p, byte m)
    {
        if (m == 255) return p;
        if (m == 0) return 0;
        var mul = m + (m >> 7); // 0..256
        var b = (int)(p & 0xFF) * mul >> 8;
        var g = (int)((p >> 8) & 0xFF) * mul >> 8;
        var r = (int)((p >> 16) & 0xFF) * mul >> 8;
        var a = (int)(p >> 24) * mul >> 8;
        return (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
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
                    p = Scale(p, m);
                }
            }
            if (tile.IsBlank()) surface.RemoveTile(idx);
        }
    }
}
