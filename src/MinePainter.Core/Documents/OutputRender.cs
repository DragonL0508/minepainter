using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 輸出用的算繪：快速模式（見 <see cref="Document.OutputWidth"/>）下，畫布是 1080p 級的代理，
/// 真正要出圖時把整份文件複製一份、放大成專案的解析度再合成。
///
/// 「放大」不是把合成好的圖拉大 —— 那樣只是一張模糊的 1080p。這裡放大的是**文件本身**：
/// 　• 文字、形狀：以新尺寸重新排版／重畫（4K 上就是 4K 的清晰度）
/// 　• 效果堆疊：像素長度的參數（外框寬度、模糊半徑、陰影距離…）跟著放大後重算
/// 　• 筆刷畫上去的像素：只能重新取樣（這是快速模式唯一會失真的東西，UI 要講清楚）
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

        progress?.Report(0.05);
        using var scaled = CloneScaled(doc, doc.OutputWidth, doc.OutputHeight, resample);
        progress?.Report(0.5);
        var result = Compositor.RenderComposite(scaled);
        progress?.Report(1);
        return result;
    }

    /// <summary>
    /// 複製整份文件並縮放到指定尺寸（不動原文件、不進 undo）。
    /// 「以一般模式開啟快速模式的專案」用的也是這個。
    /// </summary>
    public static Document CloneScaled(Document doc, int width, int height,
        ResampleMode resample = ResampleMode.Bicubic)
    {
        var clone = new Document(Math.Max(1, width), Math.Max(1, height));
        var sx = clone.Width / (float)doc.Width;
        var sy = clone.Height / (float)doc.Height;

        lock (doc.SyncRoot)
        {
            foreach (var child in doc.Root.Children) clone.Root.Add(CloneNode(clone, child, sx, sy, resample));
        }
        return clone;
    }

    private static LayerNode CloneNode(Document clone, LayerNode node, float sx, float sy, ResampleMode resample)
    {
        var k = (Math.Abs(sx) + Math.Abs(sy)) / 2f;
        switch (node)
        {
            case GroupLayer group:
            {
                var copy = new GroupLayer { Name = group.Name };
                CopyCommon(group, copy, k);
                foreach (var child in group.Children) copy.Add(CloneNode(clone, child, sx, sy, resample));
                return copy;
            }
            case AdjustmentLayer adjustment:
            {
                var copy = new AdjustmentLayer(adjustment.Adjustment) { Name = adjustment.Name };
                CopyCommon(adjustment, copy, k);
                return copy;
            }
            case RasterLayer raster:
            {
                var copy = new RasterLayer { Name = raster.Name };
                CopyCommon(raster, copy, k);

                // 像素：能從「原始高清來源」重畫就重畫（放進來的大圖不會被放大兩次），
                // 不行才把代理解析度的像素重新取樣
                if (!TryRedrawFromSource(clone, raster, copy, sx, sy))
                {
                    // 縮小（例如把一般專案轉成快速模式）：把原本的高清像素留給複本，
                    // 之後輸出時就能從它重畫，而不是拿縮過的再放大
                    var keep = ScaleRules.CaptureSource(raster, sx, sy, 0);
                    copy.ReplaceSurface(ImageCommands.ScaleSurface(raster, sx, sy, resample));
                    if (keep != null)
                    {
                        keep.Revision = copy.Surface.Revision;
                        copy.SetPixelSource(keep);
                    }
                }
                copy.Offset = SKPointI.Empty;

                // 物件：以新尺寸重新算（文字重新排版、形狀重畫）
                var matrix = SKMatrix.CreateScale(sx, sy);
                foreach (var element in raster.Elements)
                    copy.AddElement(ScaleRules.ScaleElement(element, matrix, sx, sy));

                return copy;
            }
            default:
                throw new NotSupportedException($"未知的圖層類型：{node.GetType().Name}");
        }
    }

    /// <summary>
    /// 這層的像素若還留著「原始高清來源」（<see cref="RasterLayer.ValidPixelSource"/>），
    /// 輸出時直接拿原圖以最終尺寸重畫一次，而不是把代理解析度的那份放大。
    ///
    /// 差別很實際：在 1080p 代理上放一張 4K 照片、縮小擺好，輸出 4K 時這條路是「原圖 → 4K」
    /// 一次重取樣；走放大那條則是「原圖 → 1080p → 4K」，第二次放大只會糊。
    /// 來源在圖層被畫過之後會自動失效（Revision 對不上），那時就只能走放大。
    /// </summary>
    private static bool TryRedrawFromSource(Document clone, RasterLayer source, RasterLayer target,
        float sx, float sy)
    {
        if (source.ValidPixelSource is not { } pixels) return false;
        var image = pixels.Pixels;
        if (image == null) return false;

        // 原始 → 目前呈現（doc 座標）→ 圖層後來的平移 → 輸出比例
        var delta = new SKPointI(source.Offset.X - pixels.BaseOffset.X, source.Offset.Y - pixels.BaseOffset.Y);
        var matrix = SKMatrix.Concat(
            SKMatrix.CreateScale(sx, sy),
            SKMatrix.Concat(SKMatrix.CreateTranslation(delta.X, delta.Y), pixels.Matrix));

        var bounds = new SKRect(pixels.Bounds.Left, pixels.Bounds.Top, pixels.Bounds.Right, pixels.Bounds.Bottom);
        var dest = SKRectI.Round(matrix.MapRect(bounds));
        if (dest.Width <= 0 || dest.Height <= 0) return false;

        // 畫布外的內容留一圈就好（效果會吃到邊界外的東西），整份留著在放大之後可能非常大
        var limit = new SKRectI(-OutsideMargin, -OutsideMargin,
            clone.Width + OutsideMargin, clone.Height + OutsideMargin);
        dest = SKRectI.Intersect(dest, limit);
        if (dest.Width <= 0 || dest.Height <= 0) return false;

        var info = new SKImageInfo(dest.Width, dest.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return false;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-dest.Left, -dest.Top);
        canvas.Concat(ref matrix);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.DrawImage(image, pixels.Bounds.Left, pixels.Bounds.Top, paint);
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        using var pixmap = bitmap.PeekPixels();
        var result = new TileSurface();
        result.CopyFrom(pixmap, new SKPointI(dest.Left, dest.Top));
        target.ReplaceSurface(result);
        return true;
    }

    /// <summary>從原始來源重畫時，畫布外要多留多少（效果可能吃到畫布外的內容）。</summary>
    private const int OutsideMargin = 256;

    private static void CopyCommon(LayerNode source, LayerNode target, float scale)
    {
        target.Name = source.Name;
        target.IsVisible = source.IsVisible;
        target.Opacity = source.Opacity;
        target.BlendMode = source.BlendMode;
        if (source.HasEffects) target.SetEffects([.. source.Effects.Select(fx => ScaleRules.ScaleEffect(fx, scale))]);
    }

}
