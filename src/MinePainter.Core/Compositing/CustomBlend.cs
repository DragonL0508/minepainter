using MinePainter.Core.Layers;
using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Compositing;

/// <summary>
/// Skia 沒有的混合模式（Photoshop 專有：線性加深、線性光源、強烈光源、小光源、實色疊印混合、
/// 顏色變暗／變亮、減去、分割），自己逐像素算。公式照 Photoshop／W3C 的分離式混合：
/// 兩邊都先反預乘成直通色，算出混合色 B(Cb, Cs)，再依 W3C 的規則與 alpha 合成：
/// Co = (1 − αb)·Cs + αb·B，然後 Cs' = Co 做一般的 src-over。
///
/// 效能：只在圖層真的用到這些模式時才走這條，且一次只算一格 tile（256×256）；
/// GPU 路徑遇到這些模式整份退回 CPU 合成器（<see cref="IsCustom"/>）。
/// </summary>
public static class CustomBlend
{
    /// <summary>這個模式 Skia 畫不出來、要自己算。</summary>
    public static bool IsCustom(BlendMode mode) => mode is BlendMode.LinearBurn or BlendMode.LinearLight or BlendMode.VividLight
        or BlendMode.PinLight or BlendMode.HardMix or BlendMode.DarkerColor or BlendMode.LighterColor
        or BlendMode.Subtract or BlendMode.Divide;

    /// <summary>
    /// 把 <paramref name="src"/>（premul）以自訂模式疊到 <paramref name="surface"/> 的 (x, y)，整體不透明度 <paramref name="opacity"/>。
    /// 讀回目標區域的像素、逐點混合、再以 Src 寫回。畫布外的部分裁掉。
    /// </summary>
    public static void DrawImage(SKSurface surface, SKImage src, int x, int y, float opacity, BlendMode mode)
    {
        var canvas = surface.Canvas;
        canvas.Flush();
        // 呼叫端的 canvas 可能帶著平移矩陣：這裡全部以裝置像素座標算，畫回去時也用裝置座標
        var bounds = canvas.DeviceClipBounds;
        var target = SKRectI.Intersect(new SKRectI(x, y, x + src.Width, y + src.Height), bounds);
        if (target.Width <= 0 || target.Height <= 0) return;

        var info = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var backdrop = new uint[target.Width * target.Height];
        var source = new uint[target.Width * target.Height];
        unsafe
        {
            fixed (uint* bp = backdrop)
            fixed (uint* sp = source)
            {
                using var snapshot = surface.Snapshot();
                if (!snapshot.ReadPixels(info, (IntPtr)bp, target.Width * 4, target.Left, target.Top)) return;
                if (!src.ReadPixels(info, (IntPtr)sp, target.Width * 4, target.Left - x, target.Top - y)) return;
            }
        }

        var alphaScale = (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255);
        for (var i = 0; i < backdrop.Length; i++)
        {
            var s = source[i];
            if (s == 0) continue;
            if (alphaScale < 255) s = LayerPixelSource.ScalePremul(s, (byte)alphaScale);
            backdrop[i] = Blend(s, backdrop[i], mode);
        }

        unsafe
        {
            fixed (uint* bp = backdrop)
            {
                using var result = SKImage.FromPixelCopy(info, (IntPtr)bp, target.Width * 4);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                canvas.Save();
                canvas.ResetMatrix();
                canvas.DrawImage(result, target.Left, target.Top, paint);
                canvas.Restore();
            }
        }
        canvas.Flush();
    }

    /// <summary>單一像素：<paramref name="src"/> 以 <paramref name="mode"/> 疊在 <paramref name="dst"/> 上（兩者皆 premul BGRA）。</summary>
    public static uint Blend(uint src, uint dst, BlendMode mode)
    {
        var sa = A(src);
        if (sa == 0) return dst;
        var ba = A(dst);
        if (ba == 0) return src;

        Unpremul(src, out var sb, out var sg, out var sr, out _);
        Unpremul(dst, out var bb, out var bg, out var br, out _);
        var cs = (sr / 255f, sg / 255f, sb / 255f);
        var cb = (br / 255f, bg / 255f, bb / 255f);

        (float R, float G, float B) mixed;
        if (mode is BlendMode.DarkerColor or BlendMode.LighterColor)
        {
            var ls = Luma(cs);
            var lb = Luma(cb);
            mixed = mode == BlendMode.DarkerColor ? (ls < lb ? cs : cb) : (ls > lb ? cs : cb);
        }
        else
        {
            mixed = (Channel(cb.Item1, cs.Item1, mode), Channel(cb.Item2, cs.Item2, mode), Channel(cb.Item3, cs.Item3, mode));
        }

        // W3C：Co = (1 − αb)·Cs + αb·B(Cb, Cs)，再以 αs 做 src-over
        var ab = ba / 255f;
        var r = (1 - ab) * cs.Item1 + ab * mixed.R;
        var g = (1 - ab) * cs.Item2 + ab * mixed.G;
        var b = (1 - ab) * cs.Item3 + ab * mixed.B;
        var over = Premul(Clamp255(b * 255f), Clamp255(g * 255f), Clamp255(r * 255f), sa);
        return Over(over, dst);
    }

    /// <summary>分離式公式（每個通道獨立），輸入輸出都是 0..1 的直通值。</summary>
    public static float Channel(float b, float s, BlendMode mode) => mode switch
    {
        BlendMode.LinearBurn => Math.Clamp(b + s - 1f, 0f, 1f),
        BlendMode.LinearLight => Math.Clamp(b + 2f * s - 1f, 0f, 1f),
        BlendMode.VividLight => VividLight(b, s),
        BlendMode.PinLight => s < 0.5f ? MathF.Min(b, 2f * s) : MathF.Max(b, 2f * s - 1f),
        BlendMode.HardMix => VividLight(b, s) < 0.5f ? 0f : 1f,
        BlendMode.Subtract => Math.Clamp(b - s, 0f, 1f),
        BlendMode.Divide => s <= 0f ? 1f : Math.Clamp(b / s, 0f, 1f),
        _ => s,
    };

    private static float VividLight(float b, float s)
    {
        if (s < 0.5f)
        {
            // 加深顏色（用 2s）
            var d = 2f * s;
            if (b >= 1f) return 1f;
            if (d <= 0f) return 0f;
            return Math.Clamp(1f - (1f - b) / d, 0f, 1f);
        }
        // 加亮顏色（用 2s − 1）
        var dodge = 2f * s - 1f;
        if (b <= 0f) return 0f;
        if (dodge >= 1f) return 1f;
        return Math.Clamp(b / (1f - dodge), 0f, 1f);
    }

    private static float Luma((float R, float G, float B) c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
}
