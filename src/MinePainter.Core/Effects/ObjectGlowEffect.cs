using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>物件光暈：內容外圍發光（外擴＋模糊的同色暈），墊在內容底下。</summary>
public sealed record ObjectGlowEffect : IEffect
{
    public int Size { get; init; } = 12;     // 1..100（模糊半徑）
    public int Spread { get; init; } = 2;    // 0..30（先外擴幾 px）
    public int Opacity { get; init; } = 85;  // 0..100
    public SKColor Color { get; init; } = new(0xFF, 0xD3, 0x4A);


    public string Name => "光暈";
    public string Category => "物件";
    public int SourceMargin => Spread + GaussianMargin(Size);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("size", "大小", 1, 50, o => ((ObjectGlowEffect)o).Size,
            (o, v) => ((ObjectGlowEffect)o) with { Size = (int)v }) { Geometric = true },
        new SliderParam("spread", "擴散", 0, 30, o => ((ObjectGlowEffect)o).Spread,
            (o, v) => ((ObjectGlowEffect)o) with { Spread = (int)v }) { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((ObjectGlowEffect)o).Opacity,
            (o, v) => ((ObjectGlowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((ObjectGlowEffect)o).Color,
            (o, v) => ((ObjectGlowEffect)o) with { Color = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var glow = ObjectShadowEffect.ShadowMask(ctx, 0, 0, Spread, Size, Color, Opacity / 100f);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var g = glow[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                // 光暈用 screen 疊在自己上會太亮；直接墊底
                ctx.Dst[y * ctx.Width + x] = Over(ctx.SrcAt(x, y), g);
            }
        });
    }
}
