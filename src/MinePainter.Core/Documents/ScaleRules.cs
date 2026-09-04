using MinePainter.Core.Effects;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 「整份文件縮放」時，除了像素之外還要跟著縮的東西：
/// 效果堆疊裡的像素長度參數、文字自己的外框／陰影／光暈。
///
/// 少了這一段，放大之後外框還是原本的粗細、陰影還是原本的距離 —— 看起來就不是同一張圖了。
/// 調整影像大小（<see cref="History.ImageCommands"/>）與快速模式的輸出（<see cref="OutputRender"/>）
/// 共用這裡，兩條路才會給出同樣的結果。
/// </summary>
internal static class ScaleRules
{
    /// <summary>把像素長度的效果參數乘上比例。</summary>
    internal static LayerEffect ScaleEffect(LayerEffect entry, float scale)
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
    internal static VectorElement ScaleElement(VectorElement element, SKMatrix matrix, float sx, float sy)
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
