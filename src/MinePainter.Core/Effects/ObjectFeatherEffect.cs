using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 羽化物件：真的把物件最外圍 <c>削去</c> px 的像素吃掉（任何 alpha &gt; 0 的像素都算最外圍，
/// 去背殘留的毛邊、背景色的邊就是這樣清掉的），再往內 <c>柔邊</c> px 用 smoothstep 回到原本的濃度；
/// 離邊緣比削去＋柔邊遠的內部一格都不動，顏色一律不動，物件外不會長出任何東西。
///
/// 演進（都是使用者 2026-09-07 的回報）：以邊緣為中心往外鋪 → 「外圍變不透明、變糊」；
/// 距離場整段 smoothstep → 「只是由外往內變透明」；照 BoltBait 模糊 alpha → 邊緣停在一半、
/// 內部一大段半透明，「後期效果不好加」。他要的是：最外層像素確實被吃掉、內部不透明度不要被拉低，
/// 所以拆成兩個參數：削去（真的移除）＋柔邊（短短一段過渡，預設 2px）。
/// 距離場用二值覆蓋率（alpha &gt; 0 = 內容）：半透明的毛邊也算最外層，整片半透明的物件內部不會被誤當邊。
/// </summary>
public sealed record ObjectFeatherEffect : IEffect
{
    /// <summary>削去（px）：從最外圍往內這麼多像素整個移除。</summary>
    public int Radius { get; init; } = 2;
    /// <summary>柔邊（px）：削去之後再往內這麼多像素的過渡（0 = 只留一格抗鋸齒）。</summary>
    public int Softness { get; init; } = 2;
    /// <summary>強度 0..100：0 = 完全不動，100 = 整段都照羽化的結果走。</summary>
    public int Strength { get; init; } = 100;
    /// <summary>畫布邊界也視為物件邊（貼齊畫布邊的物件是否也羽化）。</summary>
    public bool FeatherCanvasEdge { get; init; }


    public string Name => "羽化";
    public string Category => "物件";

    /// <summary>距離場要看到削去＋柔邊之外一點才算得準；輸出不會長出去。</summary>
    public int SourceMargin => Math.Clamp(Radius, 1, 100) + Math.Clamp(Softness, 0, 50) + 2;
    public int OutputMargin => 0;

    private static readonly ParamDef[] Params =
    [
        // 鍵 "radius" 沿用（舊檔的寬度就是現在的削去），"soft" 是新鍵，舊檔沒有就用預設
        new SliderParam("radius", "削去", 1, 50, o => ((ObjectFeatherEffect)o).Radius,
            (o, v) => ((ObjectFeatherEffect)o) with { Radius = (int)v }, "px") { Geometric = true },
        new SliderParam("soft", "柔邊", 0, 20, o => ((ObjectFeatherEffect)o).Softness,
            (o, v) => ((ObjectFeatherEffect)o) with { Softness = (int)v }, "px") { Geometric = true },
        new SliderParam("strength", "強度", 0, 100, o => ((ObjectFeatherEffect)o).Strength,
            (o, v) => ((ObjectFeatherEffect)o) with { Strength = (int)v }, "%"),
        new BoolParam("canvasEdge", "畫布邊緣也羽化", o => ((ObjectFeatherEffect)o).FeatherCanvasEdge,
            (o, v) => ((ObjectFeatherEffect)o) with { FeatherCanvasEdge = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var erode = (float)Math.Clamp(Radius, 1, 100);
        var soft = (float)Math.Clamp(Softness, 0, 50);
        var pad = SourceMargin;
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var strength = Math.Clamp(Strength, 0, 100) / 100f;

        var padded = DistanceTransform.PaddedSource(ctx, pad, FeatherCanvasEdge);
        var coverage = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i++) coverage[i] = A(padded[i]) > 0 ? (byte)255 : (byte)0;
        var sd = DistanceTransform.SignedFromCoverage(coverage, w, h);   // 最外圍那一格的中心 ≈ 0.5
        var ramp = Math.Max(soft, 1f);                                   // 柔邊 0 也留一格抗鋸齒

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var di = (y + pad) * w + (x + pad);
                var oi = y * ctx.Width + x;
                var src = padded[di];
                if (A(src) == 0) { ctx.Dst[oi] = 0; continue; }
                var d = sd[di];
                if (d >= erode + ramp) { ctx.Dst[oi] = src; continue; }   // 內部：一格都不動

                var t = Math.Clamp((d - erode) / ramp, 0f, 1f);
                var s = t * t * (3f - 2f * t);
                var keep = 1f - strength * (1f - s);
                if (keep >= 0.999f) { ctx.Dst[oi] = src; continue; }
                var m = (byte)Math.Clamp(MathF.Round(keep * 255f), 0f, 255f);
                ctx.Dst[oi] = m == 0 ? 0 : LayerPixelSource.ScalePremul(src, m);
            }
        });
    }

}
