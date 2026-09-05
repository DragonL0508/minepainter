using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 輸出用的算繪：快速模式（見 <see cref="Document.OutputWidth"/>）下，畫布是縮小的代理，
/// 真正要出圖時把整份文件複製一份、放大成專案的解析度再合成。
///
/// 「放大」不是把合成好的圖拉大 —— 那樣只是一張模糊的 1080p。這裡放大的是**文件本身**：
/// 　• 文字、形狀：以新尺寸重新排版／重畫（4K 上就是 4K 的清晰度）
/// 　• 效果堆疊：像素長度的參數（外框寬度、模糊半徑、陰影距離…）與遮罩跟著放大後重算
/// 　• 有「原始高清來源」的像素（放進來的圖、轉快速模式前的像素）：從原圖重畫
/// 　• 筆刷畫上去的像素：只能重新取樣（這是快速模式唯一會失真的東西，UI 要講清楚）
/// 縮放規則全在 <see cref="ScaleRules"/>，與「調整影像大小」共用。
/// </summary>
public static class OutputRender
{
    /// <summary>這份文件輸出時該有的樣子。一般模式＝直接合成。</summary>
    public static SKImage Render(Document doc, IProgress<double>? progress = null,
        ResampleMode resample = ResampleMode.Bicubic)
    {
        if (!doc.IsFastMode)
        {
            var image = Compositor.RenderComposite(doc);
            progress?.Report(1);
            return image;
        }

        // 複製放大占前六成（逐層報），合成占後四成
        var scaling = progress == null ? null : new Progress<double>(v => progress.Report(v * 0.6));
        using var scaled = CloneScaled(doc, doc.OutputWidth, doc.OutputHeight, resample, scaling, clampEffects: false);
        progress?.Report(0.6);
        var result = Compositor.RenderComposite(scaled);
        progress?.Report(1);
        return result;
    }

    /// <summary>
    /// 複製整份文件並縮放到指定尺寸（不動原文件、不進 undo）。
    /// 「以一般模式開啟快速模式的專案」與「開檔時轉成快速模式」用的也是這個。
    /// 複本與原文件完全獨立（原始來源的像素會複製一份），原文件之後釋放也沒關係。
    /// </summary>
    /// <param name="clampEffects">
    /// 效果的像素長度夾在滑桿範圍內。複本要當正式文件用（開檔轉模式）時要夾，之後才調得動；
    /// 只是輸出用的暫時複本不夾，4K 上外框該多粗就多粗。
    /// </param>
    public static Document CloneScaled(Document doc, int width, int height,
        ResampleMode resample = ResampleMode.Bicubic, IProgress<double>? progress = null,
        bool clampEffects = true)
    {
        var clone = new Document(Math.Max(1, width), Math.Max(1, height));
        var sx = clone.Width / (float)doc.Width;
        var sy = clone.Height / (float)doc.Height;

        lock (doc.SyncRoot)
        {
            var total = Math.Max(1, doc.Descendants().Count());
            var done = 0;
            var ctx = new CloneContext(clone, sx, sy, resample, new ScaleRules.Budget(), clampEffects,
                () => progress?.Report(++done / (double)total));
            foreach (var child in doc.Root.Children) clone.Root.Add(CloneNode(ctx, child));
        }
        progress?.Report(1);
        return clone;
    }

    /// <summary>
    /// 這一層在輸出解析度下的樣子（原始高清來源重畫＋效果堆疊放大後算好），包成新的原始高清來源。
    /// 快速模式下「烙印效果」與「去背前先平面化」靠這個保留高清：代理上的像素照樣烙，
    /// 但來源換成輸出解析度算出來的那份，之後輸出仍是從它重畫。
    /// 不是快速模式、這層沒有有效來源、或結果太大（<see cref="ScaleRules.MaxSourcePixels"/>）時回 null。
    /// 回傳的 Revision 未對齊，呼叫端要設。可能很慢（4K 上跑一遍效果），別在鎖內呼叫。
    /// </summary>
    internal static LayerPixelSource? RenderLayerAsSource(Document doc, RasterLayer layer)
    {
        if (!doc.IsFastMode || layer.ValidPixelSource == null) return null;
        var sx = doc.OutputWidth / (float)doc.Width;
        var sy = doc.OutputHeight / (float)doc.Height;

        using var temp = new Document(doc.OutputWidth, doc.OutputHeight);
        RasterLayer copy;
        lock (doc.SyncRoot)
        {
            var ctx = new CloneContext(temp, sx, sy, ResampleMode.Bicubic, new ScaleRules.Budget(), false, () => { });
            copy = (RasterLayer)CloneNode(ctx, layer);
            temp.Root.Add(copy);
        }

        if (copy.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(temp, copy);
        var surface = copy.HasActiveEffects && copy.FxCache.Rendered ? copy.FxCache.Surface : copy.Surface;
        var region = surface.ExactContentBounds();
        if (region.Width <= 0 || region.Height <= 0) return null;
        if ((long)region.Width * region.Height > ScaleRules.MaxSourcePixels) return null;

        using var bitmap = ImageCommands.ReadRegion(surface, region);
        var image = SKImage.FromBitmap(bitmap);
        if (image == null) return null;

        // 複本的 Offset 是 0：表面座標＝輸出 doc 座標；除回比例就是代理 doc 座標
        return new LayerPixelSource(image, region, SKMatrix.CreateScale(1 / sx, 1 / sy), layer.Offset,
            SKRect.Create(region.Left / sx, region.Top / sy, region.Width / sx, region.Height / sy),
            0f, new SKSize(region.Width, region.Height), 0);
    }

    private sealed record CloneContext(Document Target, float Sx, float Sy, ResampleMode Resample,
        ScaleRules.Budget Budget, bool ClampEffects, Action Step);

    private static LayerNode CloneNode(CloneContext ctx, LayerNode node)
    {
        var (clone, sx, sy, resample, budget, clamp, step) = ctx;
        switch (node)
        {
            case GroupLayer group:
            {
                var copy = new GroupLayer { Name = group.Name };
                CopyCommon(group, copy, sx, sy, clamp);
                step();
                foreach (var child in group.Children) copy.Add(CloneNode(ctx, child));
                return copy;
            }
            case AdjustmentLayer adjustment:
            {
                // 調整不共用實例：複本有時會變成正式文件（開檔轉模式），原文件隨後釋放
                var own = AdjustmentRegistry.Load(adjustment.Adjustment.TypeId, adjustment.Adjustment.SaveParams());
                var copy = new AdjustmentLayer(own) { Name = adjustment.Name };
                CopyCommon(adjustment, copy, sx, sy, clamp);
                step();
                return copy;
            }
            case RasterLayer raster:
            {
                var copy = new RasterLayer { Name = raster.Name };
                CopyCommon(raster, copy, sx, sy, clamp);

                // 像素：有原始高清來源就從原圖重畫（放進來的大圖不會被放大兩次），沒有才重新取樣；
                // 縮小時把原本的高清像素留給複本，之後輸出時就能從它重畫
                var (surface, source) = ScaleRules.ScaleLayerPixels(raster, sx, sy, resample, clone.Bounds,
                    budget, shareSource: false);
                copy.ReplaceSurface(surface);
                if (source != null)
                {
                    source.Revision = copy.Surface.Revision;
                    copy.SetPixelSource(source);
                }
                copy.Offset = SKPointI.Empty;

                // 物件：以新尺寸重新算（文字重新排版、形狀重畫）
                var matrix = SKMatrix.CreateScale(sx, sy);
                foreach (var element in raster.Elements)
                    copy.AddElement(ScaleRules.ScaleElement(element, matrix, sx, sy));

                step();
                return copy;
            }
            default:
                throw new NotSupportedException($"未知的圖層類型：{node.GetType().Name}");
        }
    }

    private static void CopyCommon(LayerNode source, LayerNode target, float sx, float sy, bool clampEffects)
    {
        target.Name = source.Name;
        target.IsVisible = source.IsVisible;
        target.Opacity = source.Opacity;
        target.BlendMode = source.BlendMode;
        if (source.HasEffects)
            target.SetEffects([.. source.Effects.Select(fx => ScaleRules.ScaleEffect(fx, sx, sy, clampEffects))]);
    }
}
