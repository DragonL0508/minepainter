using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>光暈：模糊後調亮／對比，再以濾色疊回。</summary>
public sealed record GlowEffect : IEffect
{
    public int Radius { get; init; } = 6;      // 1..20
    public int Brightness { get; init; } = 10; // -100..100
    public int Contrast { get; init; } = 10;   // -100..100

    public string Name => "光暈";
    public string Category => "相片";
    public int SourceMargin => GaussianMargin(Radius);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 20, o => ((GlowEffect)o).Radius,
            (o, v) => ((GlowEffect)o) with { Radius = (int)v }),
        new SliderParam("brightness", "亮度", -100, 100, o => ((GlowEffect)o).Brightness,
            (o, v) => ((GlowEffect)o) with { Brightness = (int)v }),
        new SliderParam("contrast", "對比", -100, 100, o => ((GlowEffect)o).Contrast,
            (o, v) => ((GlowEffect)o) with { Contrast = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var blurred = GaussianBlur(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, Radius, ctx.Cancellation);
        var lut = BrightnessContrastLut(Brightness, Contrast);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = ctx.SrcAt(x, y);
                var q = blurred[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                Unpremul(q, out var b, out var g, out var r, out var a);
                var glow = Premul(lut[b], lut[g], lut[r], a);
                // screen（premul）：s + d − s·d
                ctx.Dst[y * ctx.Width + x] = Pack(
                    B(s) + B(glow) - B(s) * B(glow) / 255,
                    G(s) + G(glow) - G(s) * G(glow) / 255,
                    R(s) + R(glow) - R(s) * R(glow) / 255,
                    A(s));
            }
        });
    }

    internal static byte[] BrightnessContrastLut(int brightness, int contrast)
    {
        var bri = brightness * 2.55f;
        var c = contrast * 2.55f;
        var k = (259f * (c + 255f)) / (255f * (259f - c));
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
            lut[i] = (byte)Clamp255((i + bri - 128f) * k + 128f);
        return lut;
    }
}

/// <summary>紅眼移除：紅色明顯高於其他兩通道的像素，把紅拉回。</summary>
public sealed record RedEyeRemovalEffect : IEffect
{
    public int Tolerance { get; init; } = 70;  // 0..100
    public int Saturation { get; init; } = 90; // 0..100

    public string Name => "紅眼移除";
    public string Category => "相片";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("tolerance", "容許度", 0, 100, o => ((RedEyeRemovalEffect)o).Tolerance,
            (o, v) => ((RedEyeRemovalEffect)o) with { Tolerance = (int)v }),
        new SliderParam("saturation", "飽和度", 0, 100, o => ((RedEyeRemovalEffect)o).Saturation,
            (o, v) => ((RedEyeRemovalEffect)o) with { Saturation = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var threshold = (100 - Tolerance) * 1.5f;
        var strength = Saturation / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                var other = Math.Max(g, b);
                var excess = r - other;
                if (excess <= threshold)
                {
                    ctx.Dst[y * ctx.Width + x] = p;
                    continue;
                }
                var target = (g + b) / 2;
                var nr = (int)(r + (target - r) * strength);
                ctx.Dst[y * ctx.Width + x] = Premul(b, g, nr, a);
            }
        });
    }
}

/// <summary>銳利化：反遮罩（原圖 + (原圖 − 模糊) × 量）。</summary>
public sealed record SharpenEffect : IEffect
{
    public int Amount { get; init; } = 2; // 1..20

    public string Name => "銳利化";
    public string Category => "相片";
    public int SourceMargin => 3;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", 1, 20, o => ((SharpenEffect)o).Amount,
            (o, v) => ((SharpenEffect)o) with { Amount = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var blurred = BoxBlur(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, 1, ctx.Cancellation);
        var k = Amount * 0.25f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = ctx.SrcAt(x, y);
                var q = blurred[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                Unpremul(s, out var sb, out var sg, out var sr, out var sa);
                Unpremul(q, out var qb, out var qg, out var qr, out _);
                ctx.Dst[y * ctx.Width + x] = Premul(
                    Clamp255(sb + (sb - qb) * k), Clamp255(sg + (sg - qg) * k), Clamp255(sr + (sr - qr) * k), sa);
            }
        });
    }
}

/// <summary>柔化人像：柔焦 + 打光 + 暖色。</summary>
public sealed record SoftenPortraitEffect : IEffect
{
    public int Softness { get; init; } = 5;  // 0..10
    public int Lighting { get; init; } = 0;  // -20..20
    public int Warmth { get; init; } = 10;   // 0..20

    public string Name => "柔化人像";
    public string Category => "相片";
    public int SourceMargin => GaussianMargin(Softness);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("softness", "柔化", 0, 10, o => ((SoftenPortraitEffect)o).Softness,
            (o, v) => ((SoftenPortraitEffect)o) with { Softness = (int)v }),
        new SliderParam("lighting", "打光", -20, 20, o => ((SoftenPortraitEffect)o).Lighting,
            (o, v) => ((SoftenPortraitEffect)o) with { Lighting = (int)v }),
        new SliderParam("warmth", "暖度", 0, 20, o => ((SoftenPortraitEffect)o).Warmth,
            (o, v) => ((SoftenPortraitEffect)o) with { Warmth = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var blurred = Softness > 0
            ? GaussianBlur(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, Softness, ctx.Cancellation)
            : ctx.Src;
        var mix = Softness / 10f * 0.75f;
        var light = Lighting * 3;
        var warmR = Warmth * 2;
        var warmB = -Warmth;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = ctx.SrcAt(x, y);
                var q = blurred[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                var m = Lerp(s, q, mix);
                Unpremul(m, out var b, out var g, out var r, out var a);
                ctx.Dst[y * ctx.Width + x] = Premul(
                    Clamp255(b + light + warmB), Clamp255(g + light + warmR / 3), Clamp255(r + light + warmR), a);
            }
        });
    }
}

/// <summary>暈影：四角依距離變暗。</summary>
public sealed record VignetteEffect : IEffect
{
    public float Radius { get; init; } = 0.5f;  // 0.1..4（半對角線倍率）
    public float Amount { get; init; } = 1f;    // 0..1
    public float CenterX { get; init; } = 0f;
    public float CenterY { get; init; } = 0f;

    public string Name => "暈影";
    public bool IsPositionIndependent => false;
    public string Category => "相片";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 0.1, 4, o => ((VignetteEffect)o).Radius,
            (o, v) => ((VignetteEffect)o) with { Radius = (float)v }, "", 2),
        new SliderParam("amount", "強度", 0, 1, o => ((VignetteEffect)o).Amount,
            (o, v) => ((VignetteEffect)o) with { Amount = (float)v }, "", 2),
        new PointParam("center", "中心", o => (((VignetteEffect)o).CenterX, ((VignetteEffect)o).CenterY),
            (o, v) => ((VignetteEffect)o) with { CenterX = v.X, CenterY = v.Y }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var halfDiag = MathF.Sqrt(ctx.Width * ctx.Width + ctx.Height * ctx.Height) / 2f;
        var r = Math.Max(0.05f, Radius) * halfDiag;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                var n = MathF.Sqrt(px * px + py * py) / r;
                var t = Math.Clamp(n, 0f, 1f);
                var s = t * t * (3f - 2f * t);
                var f = 1f - Amount * s * s;
                var p = ctx.SrcAt(x, y);
                ctx.Dst[y * ctx.Width + x] = Pack((int)(B(p) * f), (int)(G(p) * f), (int)(R(p) * f), A(p));
            }
        });
    }
}
