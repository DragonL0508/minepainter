using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 顏色透明化：把指定顏色變成透明。兩種模式 ——
///
/// 　「漸進（抽離這個顏色）」（預設，GIMP Color to Alpha 的作法）：每個像素都問
/// 　「要多少不透明度，才能用這個顏色當底把它疊出來」。白→黑漸層指定黑色時，
/// 　黑的地方全透明、中間灰是半透明、白的地方原封不動 —— 漸層會變成「白色的濃淡」，
/// 　而不是只有純黑那一小段被挖掉。
///
/// 　「門檻（只清掉相近色）」：與指定顏色的距離在容許度內＝全透明，容許度到容許度＋柔邊
/// 　之間依距離漸進，之外原樣保留。純色背景去背用這個最乾脆。
/// </summary>
public sealed record ColorToAlphaEffect : IEffect
{
    public SKColor Color { get; init; } = SKColors.White;

    /// <summary>0＝漸進抽離；1＝門檻。</summary>
    public int Mode { get; init; }

    /// <summary>漸進模式的強度（%）：100＝完全抽離，0＝原樣。</summary>
    public int Strength { get; init; } = 100;

    public int Tolerance { get; init; } = 30;  // 0..255（門檻模式）
    public int Softness { get; init; } = 20;   // 0..255（門檻模式）
    public bool Invert { get; init; }          // 門檻模式：反過來，只留這個顏色

    public string Name => "顏色透明化";
    public string Category => "色彩";
    public int SourceMargin => 0;

    private static readonly ParamDef ColorDef =
        new ColorParam("color", "顏色", o => ((ColorToAlphaEffect)o).Color,
            (o, v) => ((ColorToAlphaEffect)o) with { Color = v }) { UsePrimaryByDefault = true };

    private static readonly ParamDef ModeDef =
        new ChoiceParam("mode", "模式", ["漸進（抽離這個顏色）", "門檻（只清掉相近色）"],
            o => ((ColorToAlphaEffect)o).Mode, (o, v) => ((ColorToAlphaEffect)o) with { Mode = v });

    private static readonly ParamDef[] GradualParams =
    [
        ColorDef,
        ModeDef,
        new SliderParam("strength", "強度", 0, 100, o => ((ColorToAlphaEffect)o).Strength,
            (o, v) => ((ColorToAlphaEffect)o) with { Strength = (int)v }, "%"),
    ];

    private static readonly ParamDef[] ThresholdParams =
    [
        ColorDef,
        ModeDef,
        new SliderParam("tolerance", "容許度", 0, 255, o => ((ColorToAlphaEffect)o).Tolerance,
            (o, v) => ((ColorToAlphaEffect)o) with { Tolerance = (int)v }),
        new SliderParam("softness", "柔邊", 0, 255, o => ((ColorToAlphaEffect)o).Softness,
            (o, v) => ((ColorToAlphaEffect)o) with { Softness = (int)v }),
        new BoolParam("invert", "反轉（只保留這個顏色）", o => ((ColorToAlphaEffect)o).Invert,
            (o, v) => ((ColorToAlphaEffect)o) with { Invert = v }),
    ];

    /// <summary>參數隨模式換一組（用不到的滑桿不該還留在對話框裡）。</summary>
    public IReadOnlyList<ParamDef> Parameters => Mode == 1 ? ThresholdParams : GradualParams;

    public void Render(EffectContext ctx)
    {
        if (Mode == 1) RenderThreshold(ctx);
        else RenderGradual(ctx);
    }

    // ---- 漸進：把指定顏色從每個像素裡「抽掉」 ----

    private void RenderGradual(EffectContext ctx)
    {
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var strength = Math.Clamp(Strength, 0, 100) / 100f;
        if (strength <= 0f) { ctx.CopySrcToDst(); return; }

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(p) == 0) { ctx.Dst[i] = p; continue; }

                Unpremul(p, out var b, out var g, out var r, out var a);

                // 每個通道要多少不透明度才蓋得出這個值；取最大的那一個
                var k = MathF.Max(Need(r, cr), MathF.Max(Need(g, cg), Need(b, cb)));
                k = 1f - strength * (1f - k); // 強度：往「原樣」那邊拉回來
                if (k <= 0f) { ctx.Dst[i] = 0; continue; }
                if (k >= 1f) { ctx.Dst[i] = p; continue; }

                // 抽掉底色之後剩下的顏色（疊回指定顏色上要能還原成原本的樣子）
                var nr = Clamp255(cr + (r - cr) / k);
                var ng = Clamp255(cg + (g - cg) / k);
                var nb = Clamp255(cb + (b - cb) / k);
                ctx.Dst[i] = Premul(nb, ng, nr, Clamp255(a * k));
            }
        });
    }

    /// <summary>單一通道需要的不透明度：與目標色差愈大，需要愈不透明才蓋得住。</summary>
    private static float Need(int value, int target)
    {
        if (value > target) return target >= 255 ? 0f : (value - target) / (255f - target);
        if (value < target) return target <= 0 ? 0f : (target - value) / target;
        return 0f;
    }

    // ---- 門檻：只清掉「夠像」的顏色 ----

    private void RenderThreshold(EffectContext ctx)
    {
        var tol = Math.Clamp(Tolerance, 0, 255);
        var soft = Math.Clamp(Softness, 0, 255);
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var invert = Invert;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(p) == 0) { ctx.Dst[i] = p; continue; }

                Unpremul(p, out var b, out var g, out var r, out var a);
                var d = Math.Max(Math.Abs(r - cr), Math.Max(Math.Abs(g - cg), Math.Abs(b - cb)));

                // keep = 這個像素保留多少不透明度
                float keep;
                if (d <= tol) keep = 0f;
                else if (soft <= 0 || d >= tol + soft) keep = 1f;
                else keep = (d - tol) / (float)soft;
                if (invert) keep = 1f - keep;

                var na = Clamp255(a * keep);
                ctx.Dst[i] = na <= 0 ? 0u : na >= a ? p : Premul(b, g, r, na);
            }
        });
    }
}
