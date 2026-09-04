using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
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
            foreach (var child in doc.Root.Children) clone.Root.Add(CloneNode(child, sx, sy, resample));
        }
        return clone;
    }

    private static LayerNode CloneNode(LayerNode node, float sx, float sy, ResampleMode resample)
    {
        var k = (Math.Abs(sx) + Math.Abs(sy)) / 2f;
        switch (node)
        {
            case GroupLayer group:
            {
                var copy = new GroupLayer { Name = group.Name };
                CopyCommon(group, copy, k);
                foreach (var child in group.Children) copy.Add(CloneNode(child, sx, sy, resample));
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

                // 像素：整層縮放（含畫布外的部分；Offset 併進表面）
                copy.ReplaceSurface(ImageCommands.ScaleSurface(raster, sx, sy, resample));
                copy.Offset = SKPointI.Empty;

                // 物件：以新尺寸重新算（文字重新排版、形狀重畫）
                var matrix = SKMatrix.CreateScale(sx, sy);
                foreach (var element in raster.Elements)
                    copy.AddElement(ScaleElement(element, matrix, sx, sy));

                return copy;
            }
            default:
                throw new NotSupportedException($"未知的圖層類型：{node.GetType().Name}");
        }
    }

    private static void CopyCommon(LayerNode source, LayerNode target, float scale)
    {
        target.Name = source.Name;
        target.IsVisible = source.IsVisible;
        target.Opacity = source.Opacity;
        target.BlendMode = source.BlendMode;
        if (source.HasEffects) target.SetEffects([.. source.Effects.Select(fx => ScaleEffect(fx, scale))]);
    }

    /// <summary>把像素長度的效果參數乘上比例。</summary>
    private static LayerEffect ScaleEffect(LayerEffect entry, float scale)
    {
        if (Math.Abs(scale - 1f) < 0.001f) return entry;
        object current = entry.Effect;
        foreach (var def in entry.Effect.Parameters)
        {
            if (def is not SliderParam { Geometric: true } slider) continue;
            var value = Math.Clamp(slider.Get(current) * scale, slider.Min, slider.Max);
            current = slider.With(current, value);
        }
        // 遮罩是 doc 座標的整層遮罩，縮放後對不上 —— 這種效果維持原遮罩（近似）
        return entry with { Effect = (IEffect)current };
    }

    /// <summary>物件（文字／形狀）縮放：外觀上的像素長度也要跟著縮。</summary>
    private static VectorElement ScaleElement(VectorElement element, SKMatrix matrix, float sx, float sy)
    {
        var scaled = element.TransformedBy(matrix, sx, sy, 0f);
        if (scaled is not TextElement text) return scaled;

        var k = (Math.Abs(sx) + Math.Abs(sy)) / 2f;
        return text with
        {
            Stroke = ScaleStroke(text.Stroke, k),
            Shadow = text.Shadow is { } shadow
                ? shadow with
                {
                    Distance = shadow.Distance * k,
                    Blur = shadow.Blur * k,
                    Spread = shadow.Spread * k,
                }
                : null,
            Glow = text.Glow is { } glow
                ? glow with { Size = glow.Size * k, Spread = glow.Spread * k }
                : null,
        };
    }

    private static TextStroke? ScaleStroke(TextStroke? stroke, float k)
    {
        if (stroke == null) return null;
        var layers = stroke.Layers().Select(s => s with { Width = s.Width * k }).ToList();
        return TextStroke.FromLayers(layers);
    }
}
