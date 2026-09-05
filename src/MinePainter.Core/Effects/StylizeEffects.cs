using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>方向性 3×3 核（浮雕／邊緣偵測／浮雕效果共用）：鄰居權重 = cos(鄰居方位 − 角度)。</summary>
internal static class DirectionalKernel
{
    public static float[] Build(float angleDeg)
    {
        var a = angleDeg * MathF.PI / 180f;
        var k = new float[9];
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var phi = MathF.Atan2(-dy, dx);
            k[(dy + 1) * 3 + (dx + 1)] = MathF.Cos(phi - a) / (dx != 0 && dy != 0 ? 1.4142f : 1f);
        }
        return k;
    }

    /// <summary>對 straight 色三通道各自做卷積（回傳浮點結果）。</summary>
    public static (float B, float G, float R) Convolve(EffectContext ctx, float[] k, int x, int y)
    {
        float sb = 0, sg = 0, sr = 0;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var w = k[(dy + 1) * 3 + (dx + 1)];
            if (w == 0) continue;
            Unpremul(ctx.SrcAt(x + dx, y + dy), out var b, out var g, out var r, out _);
            sb += b * w; sg += g * w; sr += r * w;
        }
        return (sb, sg, sr);
    }
}

/// <summary>邊緣偵測：方向性梯度，中灰為底，各通道分別計算。</summary>
public sealed record EdgeDetectEffect : IEffect
{
    public float Angle { get; init; } = 45f;

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "邊緣偵測";
    public string Category => "風格化";
    public int SourceMargin => 1;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", 0, 360, o => ((EdgeDetectEffect)o).Angle,
            (o, v) => ((EdgeDetectEffect)o) with { Angle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((EdgeDetectEffect)o).RelativeToObject,
            (o, v) => ((EdgeDetectEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var k = DirectionalKernel.Build(ctx.FollowedAngleCcw(Angle, RelativeToObject));
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var (b, g, r) = DirectionalKernel.Convolve(ctx, k, x, y);
                var a = A(ctx.SrcAt(x, y));
                ctx.Dst[y * ctx.Width + x] = Premul(Clamp255(128 + b), Clamp255(128 + g), Clamp255(128 + r), a);
            }
        });
    }
}

/// <summary>浮雕：方向性梯度的灰階。</summary>
public sealed record EmbossEffect : IEffect
{
    public float Angle { get; init; } = 0f;

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "浮雕";
    public string Category => "風格化";
    public int SourceMargin => 1;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", 0, 360, o => ((EmbossEffect)o).Angle,
            (o, v) => ((EmbossEffect)o) with { Angle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((EmbossEffect)o).RelativeToObject,
            (o, v) => ((EmbossEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var k = DirectionalKernel.Build(ctx.FollowedAngleCcw(Angle, RelativeToObject));
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var (b, g, r) = DirectionalKernel.Convolve(ctx, k, x, y);
                var v = Clamp255(128 + (b * 0.114f + g * 0.587f + r * 0.299f));
                var a = A(ctx.SrcAt(x, y));
                ctx.Dst[y * ctx.Width + x] = Premul(v, v, v, a);
            }
        });
    }
}

/// <summary>浮雕效果：方向性梯度疊回原色。</summary>
public sealed record ReliefEffect : IEffect
{
    public float Angle { get; init; } = 45f;

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "浮雕效果";
    public string Category => "風格化";
    public int SourceMargin => 1;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", 0, 360, o => ((ReliefEffect)o).Angle,
            (o, v) => ((ReliefEffect)o) with { Angle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((ReliefEffect)o).RelativeToObject,
            (o, v) => ((ReliefEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var k = DirectionalKernel.Build(ctx.FollowedAngleCcw(Angle, RelativeToObject));
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var (b, g, r) = DirectionalKernel.Convolve(ctx, k, x, y);
                Unpremul(ctx.SrcAt(x, y), out var sb, out var sg, out var sr, out var a);
                ctx.Dst[y * ctx.Width + x] = Premul(Clamp255(sb + b), Clamp255(sg + g), Clamp255(sr + r), a);
            }
        });
    }
}

/// <summary>外框：以局部（最大 − 最小）當線條，白底深線。</summary>
public sealed record OutlineEffect : IEffect
{
    public int Thickness { get; init; } = 3;  // 1..200
    public int Intensity { get; init; } = 50; // 0..100

    public string Name => "外框";
    public string Category => "風格化";
    public int SourceMargin => Math.Min(Thickness, 50) + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("thickness", "粗細", 1, 200, o => ((OutlineEffect)o).Thickness,
            (o, v) => ((OutlineEffect)o) with { Thickness = (int)v }),
        new SliderParam("intensity", "強度", 0, 100, o => ((OutlineEffect)o).Intensity,
            (o, v) => ((OutlineEffect)o) with { Intensity = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var r = Math.Min(Thickness, 50);
        var step = Math.Max(1, (int)Math.Ceiling(r / 8.0));
        var k = Intensity / 50f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                int minB = 255, minG = 255, minR = 255, maxB = 0, maxG = 0, maxR = 0;
                for (var dy = -r; dy <= r; dy += step)
                for (var dx = -r; dx <= r; dx += step)
                {
                    Unpremul(ctx.SrcAt(x + dx, y + dy), out var b, out var g, out var rr, out _);
                    if (b < minB) minB = b; if (b > maxB) maxB = b;
                    if (g < minG) minG = g; if (g > maxG) maxG = g;
                    if (rr < minR) minR = rr; if (rr > maxR) maxR = rr;
                }
                var a = A(ctx.SrcAt(x, y));
                ctx.Dst[y * ctx.Width + x] = Premul(
                    255 - Clamp255((maxB - minB) * k), 255 - Clamp255((maxG - minG) * k), 255 - Clamp255((maxR - minR) * k), a);
            }
        });
    }
}
