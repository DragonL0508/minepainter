using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 聚焦：以一個圓（可設中心、半徑、過渡寬度）為焦點，焦點內保持原樣，往外依距離漸漸套上一種變化 ——
/// 景深（往外越模糊）、對比（往外越強／越弱）、飽和度（往外越淡／越濃）、亮度（往外越暗／越亮）。
/// 用途是把視線引到一個點上；暈影只會變暗，這個能用模糊或去色來凸顯。
/// 半徑與過渡都是「半對角線的百分比」（與暈影同一套尺度），這樣不論圖多大，同一組數字看起來都一樣。
/// </summary>
public sealed record FocusEffect : IEffect
{
    public const int ModeDepth = 0;
    public const int ModeContrast = 1;
    public const int ModeSaturation = 2;
    public const int ModeBrightness = 3;

    public int Mode { get; init; } = ModeDepth;
    public float CenterX { get; init; } = 0f;    // -1..1
    public float CenterY { get; init; } = 0f;
    public int Radius { get; init; } = 25;       // 0..100（半對角線 %）：完全清楚的範圍
    public int Feather { get; init; } = 40;      // 0..100（半對角線 %）：從清楚到全效果的過渡寬度
    public float Curve { get; init; } = 1f;      // 0.2..5：過渡曲線的指數（<1 早發、>1 晚發）
    public bool Elliptical { get; init; }        // 圓形跟著範圍長寬拉成橢圓
    public bool Invert { get; init; }            // 反轉：焦點內套效果、外面保持原樣

    public int BlurRadius { get; init; } = 12;   // 1..100 px（景深模式最外圈的模糊半徑）
    public int Contrast { get; init; } = 60;     // -100..100（對比模式最外圈的變化量）
    public int Saturation { get; init; } = -100; // -100..100（飽和度模式最外圈的變化量）
    public int Brightness { get; init; } = -50;  // -100..100（亮度模式最外圈的變化量）

    /// <summary>景深模式預先算好的模糊層數；像素在相鄰兩層之間內插，層數越多漸變越順但越慢。</summary>
    private const int BlurLevels = 4;

    public string Name => "聚焦";
    public string Category => "相片";

    /// <summary>以範圍中心為準 —— 換了範圍結果就不同，不能只重算髒區。</summary>
    public bool IsPositionIndependent => false;

    public int SourceMargin => Mode == ModeDepth ? GaussianMargin(Math.Clamp(BlurRadius, 1, 100)) : 0;

    private static readonly ParamDef ModeDef =
        new ChoiceParam("mode", "模式", ["景深", "對比", "飽和度", "亮度"], o => ((FocusEffect)o).Mode,
            (o, v) => ((FocusEffect)o) with { Mode = Math.Clamp(v, ModeDepth, ModeBrightness) });

    private static readonly ParamDef[] Common =
    [
        new PointParam("center", "中心", o => (((FocusEffect)o).CenterX, ((FocusEffect)o).CenterY),
            (o, v) => ((FocusEffect)o) with { CenterX = v.X, CenterY = v.Y }),
        new SliderParam("radius", "半徑", 0, 100, o => ((FocusEffect)o).Radius,
            (o, v) => ((FocusEffect)o) with { Radius = (int)v }, "%"),
        new SliderParam("feather", "過渡", 0, 100, o => ((FocusEffect)o).Feather,
            (o, v) => ((FocusEffect)o) with { Feather = (int)v }, "%"),
        new SliderParam("curve", "過渡曲線", 0.2, 5, o => ((FocusEffect)o).Curve,
            (o, v) => ((FocusEffect)o) with { Curve = (float)v }, "", 2),
        new BoolParam("elliptical", "跟著長寬拉成橢圓", o => ((FocusEffect)o).Elliptical,
            (o, v) => ((FocusEffect)o) with { Elliptical = v }),
        new BoolParam("invert", "反轉（焦點內套效果）", o => ((FocusEffect)o).Invert,
            (o, v) => ((FocusEffect)o) with { Invert = v }),
    ];

    // 每個模式各自的「最外圈變化量」；只顯示目前模式的那一個（ChoiceParam 改動會讓 ParamEditor 重建）。
    private static readonly ParamDef[][] ModeParams =
    [
        [
            new SliderParam("blurRadius", "模糊半徑", 1, 100, o => ((FocusEffect)o).BlurRadius,
                (o, v) => ((FocusEffect)o) with { BlurRadius = (int)v }, "px") { Geometric = true },
        ],
        [
            new SliderParam("contrast", "對比變化", -100, 100, o => ((FocusEffect)o).Contrast,
                (o, v) => ((FocusEffect)o) with { Contrast = (int)v }),
        ],
        [
            new SliderParam("saturation", "飽和度變化", -100, 100, o => ((FocusEffect)o).Saturation,
                (o, v) => ((FocusEffect)o) with { Saturation = (int)v }),
        ],
        [
            new SliderParam("brightness", "亮度變化", -100, 100, o => ((FocusEffect)o).Brightness,
                (o, v) => ((FocusEffect)o) with { Brightness = (int)v }) { Track = SliderTrack.Brightness },
        ],
    ];

    private static readonly ParamDef[][] ParamsByMode =
        ModeParams.Select(m => (ParamDef[])[ModeDef, .. m, .. Common]).ToArray();

    public IReadOnlyList<ParamDef> Parameters => ParamsByMode[Math.Clamp(Mode, ModeDepth, ModeBrightness)];

    public void Render(EffectContext ctx)
    {
        var weight = BuildWeights(ctx);
        switch (Math.Clamp(Mode, ModeDepth, ModeBrightness))
        {
            case ModeDepth: RenderDepth(ctx, weight); break;
            case ModeContrast: RenderContrast(ctx, weight); break;
            case ModeSaturation: RenderSaturation(ctx, weight); break;
            default: RenderBrightness(ctx, weight); break;
        }
    }

    /// <summary>每個目標像素套效果的比例（0＝焦點內原樣、1＝最外圈全效果）。</summary>
    internal float[] BuildWeights(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var halfDiag = MathF.Sqrt(ctx.Width * ctx.Width + ctx.Height * ctx.Height) / 2f;
        var r = Math.Clamp(Radius, 0, 100) / 100f;
        var f = Math.Max(Math.Clamp(Feather, 0, 100) / 100f, 0.001f);
        var curve = Math.Clamp(Curve, 0.2f, 5f);
        // 橢圓：把 y 方向的距離依長寬比縮放，等於在「拉成正方形」的空間裡量圓形距離
        var yScale = Elliptical && ctx.Height > 0 ? (float)ctx.Width / ctx.Height : 1f;
        var invert = Invert;

        var w = new float[ctx.Width * ctx.Height];
        ctx.ForRows(y =>
        {
            var py = (y + 0.5f - cy) * yScale;
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var n = MathF.Sqrt(px * px + py * py) / halfDiag;
                var t = Math.Clamp((n - r) / f, 0f, 1f);
                t = t * t * (3f - 2f * t);
                if (curve != 1f) t = MathF.Pow(t, curve);
                w[y * ctx.Width + x] = invert ? 1f - t : t;
            }
        });
        return w;
    }

    private void RenderDepth(EffectContext ctx, float[] weight)
    {
        var maxRadius = Math.Clamp(BlurRadius, 1, 100);
        // 第 0 層是原圖，第 i 層模糊半徑 = maxRadius × i / BlurLevels
        var levels = new uint[BlurLevels + 1][];
        levels[0] = ctx.Src;
        for (var i = 1; i <= BlurLevels; i++)
            levels[i] = GaussianBlur(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, maxRadius * (float)i / BlurLevels, ctx.Cancellation);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var t = weight[y * ctx.Width + x] * BlurLevels;
                var i0 = Math.Min((int)t, BlurLevels - 1);
                var frac = t - i0;
                var si = (y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX);
                ctx.Dst[y * ctx.Width + x] = frac <= 0f
                    ? levels[i0][si]
                    : Lerp(levels[i0][si], levels[i0 + 1][si], frac);
            }
        });
    }

    private void RenderContrast(EffectContext ctx, float[] weight)
    {
        // 正值往外越「硬」（最多 ×2.5），負值往外越「平」（最少 ×0）
        var c = Math.Clamp(Contrast, -100, 100) / 100f;
        var gain = c >= 0 ? c * 1.5f : c;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var k = 1f + gain * weight[y * ctx.Width + x];
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                ctx.Dst[y * ctx.Width + x] = Premul(
                    Clamp255((b - 128f) * k + 128f), Clamp255((g - 128f) * k + 128f), Clamp255((r - 128f) * k + 128f), a);
            }
        });
    }

    private void RenderSaturation(EffectContext ctx, float[] weight)
    {
        var s = Math.Clamp(Saturation, -100, 100) / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var k = 1f + s * weight[y * ctx.Width + x];
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                var lum = Intensity(b, g, r);
                ctx.Dst[y * ctx.Width + x] = Premul(
                    Clamp255(lum + (b - lum) * k), Clamp255(lum + (g - lum) * k), Clamp255(lum + (r - lum) * k), a);
            }
        });
    }

    private void RenderBrightness(EffectContext ctx, float[] weight)
    {
        // 負值往黑收（乘法，黑不會變灰）、正值往白拉（補到 255 的比例，白不會爆）
        var bri = Math.Clamp(Brightness, -100, 100) / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var v = bri * weight[y * ctx.Width + x];
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                ctx.Dst[y * ctx.Width + x] = v < 0
                    ? Premul(Clamp255(b * (1f + v)), Clamp255(g * (1f + v)), Clamp255(r * (1f + v)), a)
                    : Premul(Clamp255(b + (255 - b) * v), Clamp255(g + (255 - g) * v), Clamp255(r + (255 - r) * v), a);
            }
        });
    }
}
