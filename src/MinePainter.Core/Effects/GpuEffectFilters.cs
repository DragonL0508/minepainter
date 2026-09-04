using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 把效果堆疊翻成 Skia 的 <see cref="SKImageFilter"/>，交給 GPU 在畫的時候算。
///
/// **為什麼要有這條路**：現在的效果堆疊是 CPU 的 managed 迴圈（外框的距離變換、陰影的高斯模糊
/// 都是幾百萬像素的逐點運算），算完才上傳成貼圖；GPU 只負責把算好的圖貼上去。一段帶外框＋陰影
/// 的大文字，CPU 這條路一次要上百毫秒 —— 那正是「手勢中畫面跟不上」的根。
/// Skia 的 image filter 是跑在 GPU 上的（blur／dilate／erode／drop shadow），
/// 同一件事交給它，成本是數量級的差別。
///
/// **不是取代，是雙路徑**：GPU 版是「畫面上的近似」，CPU 版仍然是唯一的真相
/// （匯出、烙印、效果快取都走它）。翻不出來的效果（藝術、扭曲、雜訊那些沒有 Skia 對應的）
/// 回傳 null，呼叫端就照舊走 CPU。
///
/// **品質差異（刻意的）**：我們的外框用精確歐氏距離變換，邊緣抗鋸齒與平滑都比 GPU 的
/// 形態學膨脹細緻；GPU 版在互動當下夠用，放開／匯出時看到的仍是精確版。
/// </summary>
public static class GpuEffectFilters
{
    /// <summary>這一串效果能不能整串交給 GPU（任何一個翻不出來就不行 —— 順序不能拆開）。</summary>
    public static bool CanTranslate(IReadOnlyList<LayerEffect> effects)
    {
        var any = false;
        foreach (var entry in effects)
        {
            if (!entry.Enabled) continue;
            if (entry.Mask != null) return false; // 帶遮罩的要逐像素混，Skia 濾鏡表達不了
            if (!CanTranslate(entry.Effect)) return false;
            any = true;
        }
        return any;
    }

    private static bool CanTranslate(IEffect effect) => effect switch
    {
        ObjectShadowEffect => true,
        ObjectGlowEffect => true,
        ObjectOutlineEffect outline => !outline.Gradient && outline.Smooth == 0 && outline.Softness == 0,
        ObjectFillEffect => true,
        _ => false,
    };

    /// <summary>
    /// 整串效果的 GPU 版；翻不出來回傳 null。
    /// 回傳的 filter 由呼叫端負責 Dispose。
    /// </summary>
    public static SKImageFilter? Build(IReadOnlyList<LayerEffect> effects)
    {
        if (!CanTranslate(effects)) return null;

        SKImageFilter? chain = null;
        foreach (var entry in effects)
        {
            if (!entry.Enabled) continue;
            SKImageFilter? next;
            try
            {
                next = Translate(entry, chain);
            }
            catch
            {
                chain?.Dispose();
                return null;
            }
            if (next == null)
            {
                chain?.Dispose();
                return null;
            }
            chain = next;
        }
        return chain;
    }

    /// <summary>一道效果 → 一個 filter（input＝前一道的結果，null＝原圖）。</summary>
    private static SKImageFilter? Translate(LayerEffect entry, SKImageFilter? input) => entry.Effect switch
    {
        ObjectShadowEffect shadow => Shadow(shadow, entry.Color, input),
        ObjectGlowEffect glow => Glow(glow, input),
        ObjectOutlineEffect outline => Outline(outline, entry.Color, input),
        ObjectFillEffect fill => Fill(fill, input),
        _ => null,
    };

    /// <summary>陰影：位移＋模糊＋上色，墊在內容底下（Skia 的 DropShadow 正好是這個語意）。</summary>
    private static SKImageFilter Shadow(ObjectShadowEffect shadow, SKColor primary, SKImageFilter? input)
    {
        var color = shadow.Color == SKColors.Black && primary != SKColors.Black ? primary : shadow.Color;
        var alpha = (byte)Math.Clamp(color.Alpha * Math.Clamp(shadow.Opacity, 0, 100) / 100, 0, 255);
        // Skia 的 sigma ≈ 半徑 / 2（與 SkBlurMask::ConvertRadiusToSigma 一致的近似）
        var sigma = Math.Max(0.01f, shadow.Blur / 2f);
        return SKImageFilter.CreateDropShadow(
            shadow.OffsetX, shadow.OffsetY, sigma, sigma,
            color.WithAlpha(alpha), input);
    }

    /// <summary>光暈：先外擴（dilate）再模糊上色，同樣墊在內容底下。</summary>
    private static SKImageFilter Glow(ObjectGlowEffect glow, SKImageFilter? input)
    {
        var alpha = (byte)Math.Clamp(glow.Color.Alpha * Math.Clamp(glow.Opacity, 0, 100) / 100, 0, 255);
        var sigma = Math.Max(0.01f, glow.Size / 2f);
        SKImageFilter? spread = glow.Spread > 0
            ? SKImageFilter.CreateDilate(glow.Spread, glow.Spread, input)
            : input;
        // DropShadow 的位移設 0 ＝ 純外發光
        return SKImageFilter.CreateDropShadow(0, 0, sigma, sigma, glow.Color.WithAlpha(alpha), spread);
    }

    /// <summary>
    /// 外框：把內容膨脹 Width，塗成外框色，再把原內容疊回去。
    /// （CPU 版走的是精確距離場，邊緣更細緻；這裡是互動當下的近似。）
    /// </summary>
    private static SKImageFilter Outline(ObjectOutlineEffect outline, SKColor primary, SKImageFilter? input)
    {
        var color = outline.Color == SKColors.Black && primary != SKColors.Black ? primary : outline.Color;
        var width = Math.Max(1, outline.Width);

        using var dilate = SKImageFilter.CreateDilate(width, width, input);
        using var paint = new SKPaint { Color = color };
        using var tint = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn);
        var ring = SKImageFilter.CreateColorFilter(tint, dilate);
        // 外框在下、內容在上
        return SKImageFilter.CreateMerge([ring, input ?? SKImageFilter.CreateOffset(0, 0)]);
    }

    /// <summary>塗色：把不透明像素整片換成單一顏色（濃度＝混色比例）。</summary>
    private static SKImageFilter Fill(ObjectFillEffect fill, SKImageFilter? input)
    {
        var amount = Math.Clamp(fill.Opacity, 0, 100) / 100f;
        var a = (byte)Math.Clamp(fill.Color.Alpha * amount, 0, 255);
        using var tint = SKColorFilter.CreateBlendMode(fill.Color.WithAlpha(a), SKBlendMode.SrcATop);
        return SKImageFilter.CreateColorFilter(tint, input);
    }
}
