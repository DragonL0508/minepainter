using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 物件塗色（PS 的「顏色覆蓋」）：把物件的不透明像素整片換成單一顏色，形狀與邊緣的
/// 抗鋸齒完全保留。跟「漸層」是同一類的上色手段，只是單色 —— 想換個顏色試配色時，
/// 比去改原始像素快得多，而且是非破壞性的。
/// </summary>
public sealed record ObjectFillEffect : IEffect
{
    public SKColor Color { get; init; } = new(0xE0, 0x4B, 0x4B);

    /// <summary>0..100：塗上去的濃度（不是整層透明度，是這片顏色蓋過原色的程度）。</summary>
    public int Opacity { get; init; } = 100;


    public string Name => "塗色";
    public string Category => "物件";

    /// <summary>逐像素、不看鄰居；輸出不會長到內容外。</summary>
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new ColorParam("color", "顏色", o => ((ObjectFillEffect)o).Color,
            (o, v) => ((ObjectFillEffect)o) with { Color = v }),
        new SliderParam("opacity", "濃度", 0, 100, o => ((ObjectFillEffect)o).Opacity,
            (o, v) => ((ObjectFillEffect)o) with { Opacity = (int)v }, "%"),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var amount = Math.Clamp(Opacity, 0, 100) * 255 / 100;
        if (amount <= 0)
        {
            ctx.CopySrcToDst();
            return;
        }
        var fr = Color.Red;
        var fg = Color.Green;
        var fb = Color.Blue;
        var fa = Color.Alpha;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var a = A(src);
                if (a == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }
                // 塗上去的顏色也保有自己的 alpha；再乘上「濃度」與原像素的 alpha，
                // 邊緣的半透明像素才不會被塗成硬邊
                var cover = fa * amount / 255;
                Unpremul(src, out var sb, out var sg, out var sr, out _);
                var r = sr + (fr - sr) * cover / 255;
                var g = sg + (fg - sg) * cover / 255;
                var b = sb + (fb - sb) * cover / 255;
                ctx.Dst[y * ctx.Width + x] = Premul((byte)b, (byte)g, (byte)r, a);
            }
        });
    }
}
