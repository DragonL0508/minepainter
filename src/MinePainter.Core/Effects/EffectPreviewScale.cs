using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 效果的「降解析度預覽」：在縮小的來源上算、再放大回去。
///
/// 為什麼值得：25% 檢視下的畫面，全解析度算出來的東西有 15/16 最後是被縮掉的。
/// 實測 4K 文件上一個帶 6 個效果的文字層，整串算一次 600 ms —— 那正是抓取／放開時的停頓。
///
/// 什麼時候不能用：效果沒把像素長度的參數標成 <see cref="SliderParam.Geometric"/>
/// （縮了之後外框會變粗）、結果綁在絕對格線上（像素化、雜訊）、或帶遮罩（遮罩是全解析度的）。
///
/// 注意這與 <see cref="IEffect.IsPositionIndependent"/> 是兩回事：那個問的是「能不能只重算髒區」
/// （傾斜要看整塊內容的基準線，所以是 false），這裡問的是「縮小算完再放大，看起來一不一樣」。
/// 那些情況一律照全解析度算。輸出／複製／烙印也一律全解析度重算（見 LayerEffectRenderer 的 exact）。
/// </summary>
public static class EffectPreviewScale
{
    /// <summary>最小容許的預覽比例（再小下去外框之類的東西會縮到看不出形狀）。</summary>
    public const float MinScale = 0.125f;

    /// <summary>
    /// 把檢視比例對齊到 1、1/2、1/4、1/8。不對齊的話使用者每滾一格滾輪就換一個比例、
    /// 整份效果就得重算一次。
    /// </summary>
    public static float Quantize(float viewScale)
    {
        if (!float.IsFinite(viewScale) || viewScale >= 0.75f) return 1f;
        if (viewScale >= 0.375f) return 0.5f;
        if (viewScale >= 0.1875f) return 0.25f;
        return MinScale;
    }

    /// <summary>這一串效果能不能整串在降解析度上算（有一個不行就整串不行）。</summary>
    public static bool CanScale(IReadOnlyList<LayerEffect> effects)
    {
        if (effects.Count == 0) return false;
        foreach (var e in effects)
        {
            if (e.Mask != null) return false; // 遮罩是全解析度的座標
            if (!e.Effect.SupportsPreviewScale) return false;
        }
        return true;
    }

    /// <summary>把像素長度的參數乘上比例；沒有幾何參數的效果原樣回傳。</summary>
    public static IEffect Scale(IEffect effect, float scale)
    {
        if (scale >= 1f) return effect;
        object current = effect;
        foreach (var def in effect.Parameters)
        {
            if (def is not SliderParam { Geometric: true } s) continue;
            var value = s.Get(current) * scale;
            // 夾回滑桿範圍：外框寬度縮成 0 就整條不見了，留最小值總比消失好
            value = Math.Clamp(value, s.Min, s.Max);
            current = s.With(current, value);
        }
        return (IEffect)current;
    }

    /// <summary>把 BGRA premul 的像素縮小到 (w, h)。</summary>
    public static unsafe uint[] Downscale(uint[] src, int srcW, int srcH, int w, int h)
    {
        var dst = new uint[Math.Max(1, w * h)];
        if (srcW <= 0 || srcH <= 0 || w <= 0 || h <= 0) return dst;
        fixed (uint* sp = src)
        fixed (uint* dp = dst)
        {
            var srcInfo = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var dstInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcPixmap = new SKPixmap(srcInfo, (IntPtr)sp, srcW * 4);
            using var dstPixmap = new SKPixmap(dstInfo, (IntPtr)dp, w * 4);
            srcPixmap.ScalePixels(dstPixmap, SKFilterQuality.Medium);
        }
        return dst;
    }

    /// <summary>把算好的低解析度結果放大回原尺寸（雙線性）。</summary>
    public static unsafe uint[] Upscale(uint[] src, int srcW, int srcH, int w, int h)
    {
        var dst = new uint[Math.Max(1, w * h)];
        if (srcW <= 0 || srcH <= 0 || w <= 0 || h <= 0) return dst;
        fixed (uint* sp = src)
        fixed (uint* dp = dst)
        {
            var srcInfo = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var dstInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var srcPixmap = new SKPixmap(srcInfo, (IntPtr)sp, srcW * 4);
            using var dstPixmap = new SKPixmap(dstInfo, (IntPtr)dp, w * 4);
            srcPixmap.ScalePixels(dstPixmap, SKFilterQuality.High);
        }
        return dst;
    }
}
