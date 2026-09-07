using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.History;

public sealed record BackgroundRemovalOptions
{
    /// <summary>
    /// remove.bg 線上服務（同 paint.net 的 Remove Background 插件）；null＝本機演算（<see cref="GrabCut"/>）。
    /// 伺服器回全解析度時連它去汙染過的顏色一起用；只回預覽解析度（或本機演算）時只有遮罩、顏色仍是原圖，
    /// 遮罩是低解析度放大來的就用原圖做引導濾波貼回真實邊緣。
    /// </summary>
    public RemoveBgOptions? RemoveBg { get; init; }
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
    /// 硬邊切出（預設關）：遮罩切成二值、清碎片補洞、輪廓磨圓，邊緣只留一像素寬的抗鋸齒，
    /// 邊上被背景汙染的顏色換成內部的顏色 —— 結果沒有半透明的毛邊（見 <see cref="HardEdgeCut"/>）。
    /// 預設關：remove.bg 回全解析度時它的 PNG 就是網站上看到的結果，軟邊、髮絲都是它算好的；
    /// 硬邊會把這些以 0.5 二值化後重畫成 1px 邊，跟網站比起來反而「邊緣不乾淨」（使用者 2026-09-07 回報）。
    /// </summary>
    public bool HardEdge { get; init; }
    /// <summary>
    /// 只處理選取範圍（doc 座標；null = 整個圖層）。
    /// 有給時只把選取範圍內的像素送進模型（範圍外對模型是黑），模型的解析度全用在使用者圈出的物件上；
    /// 選取範圍外的像素一律清成透明，選取的軟邊（羽化／抗鋸齒）也乘進遮罩。
    /// </summary>
    public Selections.SelectionMask? Selection { get; init; }
}

/// <summary>
/// 圖層 → AI 去背：把圖層先平面化（效果堆疊烙印、文字物件柵格化）成純像素，
/// 送 remove.bg 算前景遮罩、乘到 alpha 上。整個是一步 undo。
///
/// remove.bg 回全解析度時，邊緣像素用它的顏色（它已經把混進來的背景色去掉；只拿遮罩乘原圖，
/// 邊上會留一圈背景色的毛邊 —— 使用者 2026-09-07 回報「丟上 remove.bg 就沒有」）。
/// 帳號沒點數時 remove.bg 只回預覽解析度，遮罩是低解析度放大回來的：
/// 顏色像素一直都是原圖，糊掉的是 alpha 邊緣。「精修邊緣」用原圖當引導做引導濾波，
/// 讓遮罩重新貼回高清像素的邊緣（等同「先留一份高清原圖、去背後再依不透明範圍回原圖取像素」，
/// 但連半透明的髮絲邊也一起處理）。
///
/// 只上傳內容外接框（透明邊不送），有選取範圍時只上傳選取的外接框、範圍外清掉。
///
/// 快速模式（圖層帶著比畫布大的原始高清來源）：整套改在來源解析度上做 —— 送原圖的那一塊去算遮罩、
/// 遮罩直接乘到原圖、再把遮罩縮回代理畫布。之前是在代理解析度算遮罩再放大套到原圖，
/// 邊緣就是代理解析度的邊緣，輸出大圖時一放大就糊（使用者 2026-09-06 回報）。
/// </summary>
public static class BackgroundRemovalCommand
{
    /// <summary>來源這一塊超過這個像素數就先縮小再送模型（remove.bg 的上限附近），遮罩回來再放大精修。</summary>
    private const long MaxModelPixels = 40_000_000;

    /// <summary>來源比畫布大到這個倍數以上才值得在來源解析度做（差不多大就沒必要多讀一次原圖）。</summary>
    private const float HiResThreshold = 1.15f;
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
        // 快速模式且有效果要烙：先在輸出解析度算好含效果的一份當來源，平面化不會把高清弄丟
        var flattenedSource = layer.HasActiveEffects ? Documents.OutputRender.RenderLayerAsSource(doc, layer) : null;

        TileSnapshot before;
        var effectsBefore = layer.Effects;
        Vectors.VectorElement[] elementsBefore;
        SKRectI affected; // 圖層座標
        LayerPixelSource? sourceOriginal; // 去背前的原始高清來源（undo 要接回去）
        lock (doc.SyncRoot)
        {
            before = layer.Surface.Snapshot();
            affected = layer.Surface.ContentBounds;
            sourceOriginal = layer.ValidPixelSource;
            if (sourceOriginal != null && (layer.HasActiveEffects || layer.HasElements))
            {
                layer.TakePixelSource(); // 平面化會寫像素，先拿下來留給 undo
                if (flattenedSource != null) layer.SetPixelSource(flattenedSource); // Revision 在平面化後對齊
            }

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
            if (layer.PixelSource is { } flattened) flattened.Revision = layer.Surface.Revision;
        }

        try
        {
            // ---- 2. 讀內容外接框的像素（鎖內，很快）----
            SKRectI crop;
            uint[] pixels;
            byte[]? coverage = null; // 選取覆蓋度（crop 內、圖層座標）
            var selection = options.Selection is { IsEmpty: false } s ? s : null;
            LayerPixelSource? sourceBefore;
            lock (doc.SyncRoot)
            {
                // 快速模式／變形留下的原始高清來源：平面化沒動到像素時它還有效，
                // 去背要連它一起做（遮罩套到原圖上），輸出大圖時才不會拿代理解析度放大
                sourceBefore = layer.ValidPixelSource;
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
                    // 範圍外的像素不送上去（透明），讓伺服器只專心在圈出來的東西上
                    for (var i = 0; i < pixels.Length; i++)
                        if (coverage[i] != 255) pixels[i] = Scale(pixels[i], coverage[i]);
                }
            }

            // ---- 3. 推論 + 後處理（鎖外）----
            byte[] mask;
            uint[]? serverPixels;
            LayerPixelSource? sourceAfter;
            var hiResRegion = sourceBefore != null && sourceBefore.SourcePixelsPerLayerPixel >= HiResThreshold
                ? sourceBefore.SourceRegionFor(crop)
                : SKRectI.Empty;
            if (sourceBefore != null && !hiResRegion.IsEmpty)
            {
                // 快速模式：在原圖那一塊上算遮罩（選取覆蓋度也放大到來源座標），遮罩直接乘到原圖
                var ratio = sourceBefore.SourcePixelsPerLayerPixel;
                var sourcePixels = sourceBefore.ReadPixels(hiResRegion);
                var sourceCoverage = coverage == null ? null : sourceBefore.ResampleMaskToSource(coverage, crop, hiResRegion);
                if (sourceCoverage != null)
                    for (var i = 0; i < sourcePixels.Length; i++)
                        if (sourceCoverage[i] != 255) sourcePixels[i] = Scale(sourcePixels[i], sourceCoverage[i]);

                var (sourceMask, serverSource) = ComputeMask(sourcePixels, hiResRegion.Width, hiResRegion.Height, sourceCoverage, options,
                    shift: (int)MathF.Round(options.Shift * ratio), ct);
                var sourceBase = serverSource != null ? WithServerColors(sourcePixels, serverSource) : sourcePixels;
                if (options.HardEdge)
                {
                    // 硬邊：在來源解析度切、去汙染，像素直接換進原圖
                    var (hardMask, hardPixels) = HardEdgeCut.Apply(sourceMask, sourceBase, hiResRegion.Width, hiResRegion.Height,
                        decontaminate: serverSource == null);
                    sourceMask = hardMask;
                    sourceAfter = sourceBefore.WithRegionPixels(hiResRegion, hardPixels, ct);
                }
                else if (serverSource != null)
                {
                    sourceAfter = sourceBefore.WithRegionPixels(hiResRegion, ServerWithAlpha(serverSource, sourceMask), ct);
                }
                else
                {
                    sourceAfter = sourceBefore.MaskedInSourceSpace(hiResRegion, sourceMask, ct);
                }
                // 代理畫布用的是同一份遮罩縮回來的（取樣平均），兩邊看到的是同一個輪廓
                mask = sourceBefore.ResampleMaskToLayer(sourceMask, hiResRegion, crop);
                // 代理畫布的顏色維持原圖（伺服器的顏色已經進了高清來源，輸出時用的是那份）
                serverPixels = null;
            }
            else
            {
                (mask, serverPixels) = ComputeMask(pixels, crop.Width, crop.Height, coverage, options, options.Shift, ct);
                // 原始高清來源也套同一份遮罩（依來源矩陣反查、雙線性取樣），成為新的來源
                sourceAfter = sourceBefore?.Masked(crop, mask, ct: ct);
            }
            // 圖層（代理）像素：伺服器有回顏色就用它的（邊緣已去汙染）；硬邊模式連輪廓一起重切；否則只乘遮罩
            uint[]? layerPixels = null;
            var basePixels = serverPixels != null ? WithServerColors(pixels, serverPixels) : pixels;
            if (options.HardEdge)
            {
                var (hardMask, hardPixels) = HardEdgeCut.Apply(mask, basePixels, crop.Width, crop.Height,
                    decontaminate: serverPixels == null);
                mask = hardMask;
                layerPixels = hardPixels;
            }
            else if (serverPixels != null)
            {
                layerPixels = ServerWithAlpha(serverPixels, mask);
            }
            ct.ThrowIfCancellationRequested();

            // ---- 4. 套 alpha（鎖內）：顏色永遠是原圖的原解析度像素，只乘上遮罩 ----
            lock (doc.SyncRoot)
            {
                if (layer.Document != doc) { sourceAfter?.Dispose(); Rollback(); return false; }
                // 目前掛著的來源（原始的、或平面化後算好的）拿下來：ApplyMask 換了像素版本後它會被當失效釋放
                if (sourceBefore != null) layer.TakePixelSource();
                if (layerPixels != null) WriteRegion(layer.Surface, crop, layerPixels);
                else ApplyMask(layer.Surface, crop, mask);
                affected = Union(affected, crop);
                if (selection != null)
                {
                    // 選取範圍外（crop 之外的所有內容）一律清掉
                    var content = layer.Surface.ContentBounds;
                    ClearOutside(layer.Surface, crop);
                    affected = Union(affected, content);
                }

                if (sourceAfter != null)
                {
                    sourceAfter.Revision = layer.Surface.Revision;
                    layer.SetPixelSource(sourceAfter);
                }

                IHistoryEntry? pixelEntry = TileDeltaEntry.Capture("AI 去背", layer, before, affected);
                if (pixelEntry != null && (sourceOriginal != null || sourceAfter != null))
                    pixelEntry = new PixelSourceSwapEntry(pixelEntry, layer, sourceOriginal, sourceAfter);
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
                // 像素放回去了：去背前的原始來源接回去（平面化算出的那份就不要了）
                var restore = sourceOriginal ?? layer.PixelSource;
                if (restore != null)
                {
                    if (!ReferenceEquals(layer.PixelSource, restore)) layer.TakePixelSource();
                    restore.Revision = layer.Surface.Revision;
                    layer.SetPixelSource(restore);
                }
                layer.SetEffects(effectsBefore);
                foreach (var el in elementsBefore)
                    if (layer.Elements.All(e => e.Id != el.Id)) layer.AddElement(el);
            }
            layer.InvalidateAll();
        }
    }

    /// <summary>
    /// 一塊像素 → 前景遮罩：模型（remove.bg 或本機 GrabCut）給機率圖，低解析度放大來的用原圖引導濾波貼回邊緣，
    /// 再做內部填實、對比、收縮／擴張、乘上選取覆蓋度。太大的圖先縮到 <see cref="MaxModelPixels"/> 以內送模型。
    /// 回傳的 ServerPixels 是 remove.bg 回的整張結果（與 pixels 同尺寸、顏色已去汙染），只有伺服器回全解析度、
    /// 而且沒有先縮小送出時才有。
    /// </summary>
    private static (byte[] Mask, uint[]? ServerPixels) ComputeMask(uint[] pixels, int width, int height, byte[]? coverage,
        BackgroundRemovalOptions options, int shift, CancellationToken ct)
    {
        var area = (long)width * height;
        var sendScale = area > MaxModelPixels ? MathF.Sqrt(MaxModelPixels / (float)area) : 1f;
        var (modelPixels, modelW, modelH) = sendScale < 1f ? Downscale(pixels, width, height, sendScale) : (pixels, width, height);

        byte[] model;
        bool refine;
        uint[]? serverPixels = null;
        if (options.RemoveBg is { } remote)
        {
            var result = RemoveBgClient.Cutout(modelPixels, modelW, modelH, remote, ct);
            model = result.Alpha;
            refine = result.Downscaled(modelW, modelH);
            if (ReferenceEquals(modelPixels, pixels)) serverPixels = result.Pixels;
        }
        else
        {
            var modelCoverage = ReferenceEquals(modelPixels, pixels) ? coverage : null;
            model = GrabCut.Run(modelPixels, modelW, modelH, LocalTrimap(modelPixels, modelW, modelH, modelCoverage), ct: ct);
            refine = Math.Max(modelW, modelH) > GrabCut.MaxSide;
        }
        ct.ThrowIfCancellationRequested();

        if (modelW != width || modelH != height)
        {
            model = LayerPixelSource.ResampleMask(model, new SKRectI(0, 0, modelW, modelH),
                SKMatrix.CreateScale(width / (float)modelW, height / (float)modelH), new SKRectI(0, 0, width, height));
            refine = true;
        }

        // 精修半徑隨圖片大小放大：模型的一個像素在大圖上是好幾個像素
        var scale = Math.Max(1, (int)MathF.Ceiling(Math.Max(width, height) / 1024f));
        var radius = Math.Max(options.RefineRadius, 6 * scale);
        var mask = refine ? GuidedFilter.Refine(model, pixels, width, height, radius, ct: ct) : (byte[])model.Clone();
        // 內部填實是給「機率圖」用的（本機演算、或預覽解析度放大後經引導濾波漏進內部紋理的）；
        // remove.bg 回全解析度時內部本來就是實的，填實只會把它的軟邊以 0.5 二值化再推向 0／1，白白丟掉髮絲
        if (options.SolidCore && serverPixels == null)
            mask = BackgroundRemover.SolidifyCore(mask, model, width, height, radius);
        BackgroundRemover.ApplyContrast(mask, options.Contrast);
        mask = BackgroundRemover.Shift(mask, width, height, shift);
        if (coverage != null)
            for (var i = 0; i < mask.Length; i++)
                if (coverage[i] != 255) mask[i] = (byte)(mask[i] * coverage[i] / 255);
        return (mask, serverPixels);
    }

    /// <summary>
    /// 伺服器的結果配上最後的遮罩：遮罩沒被動過（對比 0、收縮 0、沒有選取）時就是伺服器回的 PNG 原樣，
    /// 跟 remove.bg 網站上看到的一模一樣。之前是「原圖 alpha × 遮罩」再套伺服器顏色，原圖本身半透明時 alpha 會乘兩次。
    /// </summary>
    internal static uint[] ServerWithAlpha(uint[] server, byte[] mask)
    {
        var output = new uint[server.Length];
        for (var i = 0; i < output.Length; i++)
        {
            var s = server[i];
            var sa = s >> 24;
            var m = mask[i];
            if (sa == 0 || m == 0) continue;
            if (m == sa) { output[i] = s; continue; }
            var r = Math.Min(255u, ((s >> 16) & 0xFF) * 255 / sa);
            var g = Math.Min(255u, ((s >> 8) & 0xFF) * 255 / sa);
            var b = Math.Min(255u, (s & 0xFF) * 255 / sa);
            output[i] = ((uint)m << 24) | ((r * m / 255) << 16) | ((g * m / 255) << 8) | (b * m / 255);
        }
        return output;
    }

    /// <summary>
    /// 原圖的 alpha + 伺服器的顏色：伺服器有留下的像素（alpha &gt; 0）換成它去汙染過的顏色，
    /// 其餘（它判成背景、但我們的遮罩可能因擴張還留著的）維持原圖。alpha 一律是原圖的，之後照常乘遮罩。
    /// </summary>
    internal static uint[] WithServerColors(uint[] original, uint[] server)
    {
        var output = new uint[original.Length];
        for (var i = 0; i < output.Length; i++)
        {
            var o = original[i];
            var s = server[i];
            var sa = s >> 24;
            var oa = o >> 24;
            if (sa == 0 || oa == 0)
            {
                output[i] = o;
                continue;
            }
            // 伺服器像素去預乘取顏色，再以原圖的 alpha 重新預乘
            var r = Math.Min(255u, ((s >> 16) & 0xFF) * 255 / sa);
            var g = Math.Min(255u, ((s >> 8) & 0xFF) * 255 / sa);
            var b = Math.Min(255u, (s & 0xFF) * 255 / sa);
            output[i] = (oa << 24) | ((r * oa / 255) << 16) | ((g * oa / 255) << 8) | (b * oa / 255);
        }
        return output;
    }

    /// <summary>premul 像素逐一乘上遮罩，回傳新陣列。</summary>
    private static unsafe (uint[] Pixels, int Width, int Height) Downscale(uint[] pixels, int width, int height, float factor)
    {
        var w = Math.Max(1, (int)MathF.Round(width * factor));
        var h = Math.Max(1, (int)MathF.Round(height * factor));
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* p = pixels)
        {
            using var source = new SKBitmap();
            if (!source.InstallPixels(info, (IntPtr)p, width * 4)) throw new InvalidOperationException("建立縮圖來源失敗");
            using var small = source.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High)
                ?? throw new InvalidOperationException("縮小送模型的圖失敗");
            var result = new uint[w * h];
            fixed (uint* dst = result)
                Buffer.MemoryCopy((void*)small.GetPixels(), dst, (long)w * h * 4, (long)w * h * 4);
            return (result, w, h);
        }
    }

    /// <summary>
    /// 演算去背的初始 trimap：整塊當「可能前景」，只有最外圈一條帶子（邊長 2%，至少 2px）當確定背景
    /// —— GrabCut 需要背景樣本；有選取時範圍外的像素已是透明，也算背景。
    /// </summary>
    private static byte[] LocalTrimap(uint[] pixels, int w, int h, byte[]? coverage)
    {
        var band = Math.Max(2, Math.Min(w, h) * 2 / 100);
        var trimap = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var edge = x < band || y < band || x >= w - band || y >= h - band;
            var transparent = pixels[i] >> 24 == 0 || (coverage != null && coverage[i] < 128);
            trimap[i] = edge || transparent ? GrabCut.Background : GrabCut.ProbableForeground;
        }
        return trimap;
    }

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);

    /// <summary>讀 rect（圖層座標）的 premul BGRA 像素；沒有 tile 的地方是 0。</summary>
    internal static unsafe uint[] ReadRegion(TileSurface surface, SKRectI rect)
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
    internal static byte[] ReadCoverage(Selections.SelectionMask selection, SKRectI rect, SKPointI layerOffset)
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
    private static uint Scale(uint p, byte m) => LayerPixelSource.ScalePremul(p, m);

    /// <summary>把 rect（圖層座標）的像素整塊換成 <paramref name="pixels"/>（premul；0 就是透明）。</summary>
    internal static unsafe void WriteRegion(TileSurface surface, SKRectI rect, uint[] pixels)
    {
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var tile = surface.GetTileForWrite(idx);
            var dst = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = dst + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                pixels.AsSpan((y - rect.Top) * rect.Width + (inter.Left - rect.Left), inter.Width)
                    .CopyTo(new Span<uint>(row, inter.Width));
            }
            if (tile.IsBlank()) surface.RemoveTile(idx);
        }
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
