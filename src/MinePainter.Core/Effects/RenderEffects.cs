using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>雲朵：分形雜訊在主色與第二色之間插值，依混合模式疊到原圖上。</summary>
public sealed record CloudsEffect : IEffect
{
    public int Scale { get; init; } = 250;   // 2..2000
    public int Power { get; init; } = 50;    // 0..100
    public int Seed { get; init; } = 0;
    public int Blend { get; init; } = 0;     // 0 一般 1 色彩增值 2 濾色 3 覆疊
    public int Secondary { get; init; } = 0; // 0 白 1 黑 2 透明

    public string Name => "雲朵";
    public string Category => "演算";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("scale", "比例", 2, 2000, o => ((CloudsEffect)o).Scale,
            (o, v) => ((CloudsEffect)o) with { Scale = (int)v }),
        new SliderParam("power", "粗糙度", 0, 100, o => ((CloudsEffect)o).Power,
            (o, v) => ((CloudsEffect)o) with { Power = (int)v }),
        new SliderParam("seed", "種子", 0, 255, o => ((CloudsEffect)o).Seed,
            (o, v) => ((CloudsEffect)o) with { Seed = (int)v }) { IsSeed = true },
        new ChoiceParam("secondary", "第二色", ["白色", "黑色", "透明"], o => ((CloudsEffect)o).Secondary,
            (o, v) => ((CloudsEffect)o) with { Secondary = v }),
        new ChoiceParam("blend", "混合模式", ["一般", "色彩增值", "濾色", "覆疊"], o => ((CloudsEffect)o).Blend,
            (o, v) => ((CloudsEffect)o) with { Blend = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var scale = Math.Max(2, Scale) / 4f;
        var persistence = 0.3f + Power / 200f;
        var seed = (uint)(Seed * 2654435u + 3);
        var c1 = FromColor(ctx.PrimaryColor);
        var c2 = Secondary switch { 1 => FromColor(SKColors.Black), 2 => 0u, _ => FromColor(SKColors.White) };
        var ox = ctx.Region.Left;
        var oy = ctx.Region.Top;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var n = Fbm((x + ox) / scale, (y + oy) / scale, 6, persistence, seed);
                var t = Math.Clamp(n * 0.5f + 0.5f, 0f, 1f);
                var cloud = Lerp(c1, c2, t);
                var s = ctx.SrcAt(x, y);
                ctx.Dst[y * ctx.Width + x] = Blend switch
                {
                    1 => BlendMultiply(cloud, s),
                    2 => BlendScreen(cloud, s),
                    3 => BlendOverlay(cloud, s),
                    _ => Over(cloud, s),
                };
            }
        });
    }

    private static uint BlendMultiply(uint src, uint dst)
    {
        Unpremul(src, out var sb, out var sg, out var sr, out var sa);
        Unpremul(dst, out var db, out var dg, out var dr, out var da);
        var rb = sb * db / 255;
        var rg = sg * dg / 255;
        var rr = sr * dr / 255;
        return Over(Premul(rb, rg, rr, sa * da / 255), Over(Premul(sb, sg, sr, sa * (255 - da) / 255), dst));
    }

    private static uint BlendScreen(uint src, uint dst)
    {
        Unpremul(src, out var sb, out var sg, out var sr, out var sa);
        Unpremul(dst, out var db, out var dg, out var dr, out var da);
        var rb = sb + db - sb * db / 255;
        var rg = sg + dg - sg * dg / 255;
        var rr = sr + dr - sr * dr / 255;
        return Over(Premul(rb, rg, rr, sa * da / 255), Over(Premul(sb, sg, sr, sa * (255 - da) / 255), dst));
    }

    private static uint BlendOverlay(uint src, uint dst)
    {
        Unpremul(src, out var sb, out var sg, out var sr, out var sa);
        Unpremul(dst, out var db, out var dg, out var dr, out var da);
        return Over(Premul(Ov(sb, db), Ov(sg, dg), Ov(sr, dr), sa * da / 255),
            Over(Premul(sb, sg, sr, sa * (255 - da) / 255), dst));

        static int Ov(int s, int d) => d < 128 ? 2 * s * d / 255 : 255 - 2 * (255 - s) * (255 - d) / 255;
    }
}

/// <summary>碎形著色共用。</summary>
internal static class FractalPalette
{
    /// <summary>迭代次數 → 平滑色相環。</summary>
    public static uint Color(float iter, float maxIter, bool invert)
    {
        if (iter >= maxIter) return invert ? FromColor(SKColors.White) : FromColor(SKColors.Black);
        var t = iter / maxIter;
        var hue = (t * 5f) % 1f * 360f;
        var v = invert ? 1f - MathF.Pow(t, 0.5f) : MathF.Pow(t, 0.4f);
        var c = SKColor.FromHsv(hue, 80f, Math.Clamp(v * 100f, 0f, 100f));
        return FromColor(c);
    }
}

/// <summary>茱莉亞碎形。</summary>
public sealed record JuliaFractalEffect : IEffect
{
    public int Factor { get; init; } = 4;   // 1..10
    public int Quality { get; init; } = 2;  // 1..5
    public float Zoom { get; init; } = 1f;  // 0.1..50
    public float Angle { get; init; } = 0f;

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "茱莉亞碎形";
    public bool IsPositionIndependent => false;
    public string Category => "演算";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("factor", "係數", 1, 10, o => ((JuliaFractalEffect)o).Factor,
            (o, v) => ((JuliaFractalEffect)o) with { Factor = (int)v }),
        new SliderParam("quality", "品質", 1, 5, o => ((JuliaFractalEffect)o).Quality,
            (o, v) => ((JuliaFractalEffect)o) with { Quality = (int)v }),
        new SliderParam("zoom", "縮放", 0.1, 50, o => ((JuliaFractalEffect)o).Zoom,
            (o, v) => ((JuliaFractalEffect)o) with { Zoom = (float)v }, "", 1),
        new AngleParam("angle", "角度", -180, 180, o => ((JuliaFractalEffect)o).Angle,
            (o, v) => ((JuliaFractalEffect)o) with { Angle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((JuliaFractalEffect)o).RelativeToObject,
            (o, v) => ((JuliaFractalEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var maxIter = 64 * Math.Clamp(Quality, 1, 5);
        var cRe = -0.7f + Factor * 0.02f;
        var cIm = 0.27015f + Factor * 0.01f;
        var zoom = Math.Max(0.1f, Zoom);
        var rad = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var half = Math.Max(ctx.Width, ctx.Height) / 2f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = (x - ctx.Width / 2f) / half * 1.6f / zoom;
                var py = (y - ctx.Height / 2f) / half * 1.6f / zoom;
                var zx = px * cos - py * sin;
                var zy = px * sin + py * cos;
                var i = 0;
                while (i < maxIter && zx * zx + zy * zy < 4f)
                {
                    var t = zx * zx - zy * zy + cRe;
                    zy = 2 * zx * zy + cIm;
                    zx = t;
                    i++;
                }
                var smooth = i < maxIter ? i + 1 - MathF.Log(MathF.Log(Math.Max(1.0001f, zx * zx + zy * zy)) / 2f) / MathF.Log(2f) : maxIter;
                ctx.Dst[y * ctx.Width + x] = FractalPalette.Color(Math.Clamp(smooth, 0, maxIter), maxIter, false);
            }
        });
    }
}

/// <summary>曼德博碎形。</summary>
public sealed record MandelbrotFractalEffect : IEffect
{
    public int Factor { get; init; } = 1;   // 1..10
    public int Quality { get; init; } = 2;  // 1..5
    public float Zoom { get; init; } = 10f; // 0.1..100
    public float Angle { get; init; } = 0f;
    public bool Invert { get; init; }

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "曼德博碎形";
    public bool IsPositionIndependent => false;
    public string Category => "演算";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("factor", "係數", 1, 10, o => ((MandelbrotFractalEffect)o).Factor,
            (o, v) => ((MandelbrotFractalEffect)o) with { Factor = (int)v }),
        new SliderParam("quality", "品質", 1, 5, o => ((MandelbrotFractalEffect)o).Quality,
            (o, v) => ((MandelbrotFractalEffect)o) with { Quality = (int)v }),
        new SliderParam("zoom", "縮放", 0.1, 100, o => ((MandelbrotFractalEffect)o).Zoom,
            (o, v) => ((MandelbrotFractalEffect)o) with { Zoom = (float)v }, "", 1),
        new AngleParam("angle", "角度", -180, 180, o => ((MandelbrotFractalEffect)o).Angle,
            (o, v) => ((MandelbrotFractalEffect)o) with { Angle = (float)v }),
        new BoolParam("invert", "反轉顏色", o => ((MandelbrotFractalEffect)o).Invert,
            (o, v) => ((MandelbrotFractalEffect)o) with { Invert = v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((MandelbrotFractalEffect)o).RelativeToObject,
            (o, v) => ((MandelbrotFractalEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var maxIter = 48 * Math.Clamp(Quality, 1, 5) + Factor * 8;
        var zoom = Math.Max(0.1f, Zoom) / 10f;
        var rad = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var half = Math.Max(ctx.Width, ctx.Height) / 2f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = (x - ctx.Width / 2f) / half * 1.8f / zoom - 0.5f;
                var py = (y - ctx.Height / 2f) / half * 1.8f / zoom;
                var cRe = px * cos - py * sin;
                var cIm = px * sin + py * cos;
                float zx = 0, zy = 0;
                var i = 0;
                while (i < maxIter && zx * zx + zy * zy < 4f)
                {
                    var t = zx * zx - zy * zy + cRe;
                    zy = 2 * zx * zy + cIm;
                    zx = t;
                    i++;
                }
                var smooth = i < maxIter ? i + 1 - MathF.Log(MathF.Log(Math.Max(1.0001f, zx * zx + zy * zy)) / 2f) / MathF.Log(2f) : maxIter;
                ctx.Dst[y * ctx.Width + x] = FractalPalette.Color(Math.Clamp(smooth, 0, maxIter), maxIter, Invert);
            }
        });
    }
}
