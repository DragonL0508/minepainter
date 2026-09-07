using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// OKLab 感知均勻色彩空間（Björn Ottosson，2020）：漸層與混色在這裡內插，
/// 中段才不會像 sRGB 那樣發灰變暗（藍→黃在 sRGB 的中點是死灰，在 OKLab 是亮的）。
/// 只做「兩色之間怎麼走」；像素合成（不透明度、圖層混合）仍是 premul sRGB，不在這裡改。
/// </summary>
public static class OkLab
{
    /// <summary>OKLab 座標（L 0..1 明度，a／b 約 −0.4..0.4）。</summary>
    public readonly record struct Lab(float L, float A, float B);

    public static Lab FromColor(SKColor c)
    {
        var r = ToLinear(c.Red / 255f);
        var g = ToLinear(c.Green / 255f);
        var b = ToLinear(c.Blue / 255f);

        var l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
        var m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
        var s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);

        return new Lab(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    public static SKColor ToColor(Lab lab, byte alpha = 255)
    {
        var l = lab.L + 0.3963377774f * lab.A + 0.2158037573f * lab.B;
        var m = lab.L - 0.1055613458f * lab.A - 0.0638541728f * lab.B;
        var s = lab.L - 0.0894841775f * lab.A - 1.2914855480f * lab.B;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;

        var r = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        var g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        var b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        return new SKColor(ToByte(FromLinear(r)), ToByte(FromLinear(g)), ToByte(FromLinear(b)), alpha);
    }

    /// <summary>
    /// 兩色之間 t 處的顏色：色彩走 OKLab、alpha 線性。
    /// 一端完全透明時色彩取另一端（透明像素的 RGB 沒有意義，混進去會拖出一圈暗邊）。
    /// </summary>
    public static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var alpha = ToByte(a.Alpha / 255f + (b.Alpha - a.Alpha) / 255f * t);
        if (a.Alpha == 0 && b.Alpha != 0) return b.WithAlpha(alpha);
        if (b.Alpha == 0 && a.Alpha != 0) return a.WithAlpha(alpha);

        var la = FromColor(a);
        var lb = FromColor(b);
        return ToColor(new Lab(
            la.L + (lb.L - la.L) * t,
            la.A + (lb.A - la.A) * t,
            la.B + (lb.B - la.B) * t), alpha);
    }

    /// <summary>從 start 到 end 等距取 <paramref name="count"/> 色（含兩端），給只吃色陣列的 Skia 著色器用。</summary>
    public static SKColor[] Ramp(SKColor start, SKColor end, int count)
    {
        var colors = new SKColor[Math.Max(2, count)];
        for (var i = 0; i < colors.Length; i++) colors[i] = Lerp(start, end, i / (float)(colors.Length - 1));
        return colors;
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float FromLinear(float c) =>
        c <= 0f ? 0f : c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
