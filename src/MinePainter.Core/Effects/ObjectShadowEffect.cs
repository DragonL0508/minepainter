using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 物件陰影：alpha 位移＋模糊後上色，墊在內容底下。
/// <see cref="Thickness"/> &gt; 0 時陰影沿位移方向再延伸（把每一步的輪廓疊起來），
/// 看起來像有厚度的立體塊；位移設小、厚度設大就是 Minecraft 標題那種擠出感。
/// </summary>
public sealed record ObjectShadowEffect : IEffect
{
    public int OffsetX { get; init; } = 5;     // -100..100
    public int OffsetY { get; init; } = 5;
    public int Thickness { get; init; } = 0;   // 0..100（沿位移方向擠出的 px）
    public int Blur { get; init; } = 5;        // 0..50
    public int Opacity { get; init; } = 60;    // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>方向跟著物件轉（預設）：文字轉了 45°，陰影也甩到 45° 那一側；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "陰影";
    public string Category => "物件";

    /// <summary>
    /// 位移在單一軸上可能達到的最大值。跟著物件轉時方向會變、長度不變，
    /// 所以餘裕要用向量長度算 —— 用 max(|X|,|Y|) 的話，轉 45° 的陰影會被裁掉一角。
    /// </summary>
    private int OffsetReach => RelativeToObject
        ? (int)MathF.Ceiling(MathF.Sqrt((float)OffsetX * OffsetX + (float)OffsetY * OffsetY))
        : Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY));

    public int SourceMargin => OffsetReach + Thickness + GaussianMargin(Blur);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("ox", "位移 X", -50, 50, o => ((ObjectShadowEffect)o).OffsetX,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetX = (int)v }) { Geometric = true },
        new SliderParam("oy", "位移 Y", -50, 50, o => ((ObjectShadowEffect)o).OffsetY,
            (o, v) => ((ObjectShadowEffect)o) with { OffsetY = (int)v }) { Geometric = true },
        new SliderParam("thickness", "厚度", 0, 50, o => ((ObjectShadowEffect)o).Thickness,
            (o, v) => ((ObjectShadowEffect)o) with { Thickness = (int)v }) { Geometric = true },
        new SliderParam("blur", "模糊", 0, 50, o => ((ObjectShadowEffect)o).Blur,
            (o, v) => ((ObjectShadowEffect)o) with { Blur = (int)v }) { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((ObjectShadowEffect)o).Opacity,
            (o, v) => ((ObjectShadowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((ObjectShadowEffect)o).Color,
            (o, v) => ((ObjectShadowEffect)o) with { Color = v }),
        new BoolParam("relative", "方向跟著物件轉", o => ((ObjectShadowEffect)o).RelativeToObject,
            (o, v) => ((ObjectShadowEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        // 位移方向跟著物件轉：厚度是沿位移方向擠出的，所以擠出方向也一起跟著轉
        var (ox, oy) = ctx.FollowedOffset(OffsetX, OffsetY, RelativeToObject);
        var shadow = ShadowMask(ctx, (int)MathF.Round(ox), (int)MathF.Round(oy), 0, Blur,
            Color, Opacity / 100f, Thickness);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var s = shadow[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                ctx.Dst[y * ctx.Width + x] = Over(ctx.SrcAt(x, y), s);
            }
        });
    }

    /// <summary>
    /// 來源 alpha → 位移、擠出（thickness：沿位移方向每 px 疊一次輪廓）、外擴（spread，方形近似）、模糊、上色（Src 大小）。
    /// </summary>
    internal static uint[] ShadowMask(EffectContext ctx, int offsetX, int offsetY, int spread, int blur, SKColor color, float opacity, int thickness = 0)
    {
        var w = ctx.SrcWidth;
        var h = ctx.SrcHeight;
        var alpha = new byte[w * h];
        foreach (var (ox, oy) in ExtrusionOffsets(offsetX, offsetY, thickness))
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sx = x - ox;
                var sy = y - oy;
                if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
                var a = (byte)A(ctx.Src[sy * w + sx]);
                if (a > alpha[y * w + x]) alpha[y * w + x] = a;
            }
        }

        if (spread > 0)
        {
            // 外擴：分離的最大值濾波（水平 + 垂直）
            var tmp = new byte[w * h];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte m = 0;
                for (var i = -spread; i <= spread; i++)
                {
                    var xx = x + i;
                    if ((uint)xx >= (uint)w) continue;
                    if (alpha[y * w + xx] > m) m = alpha[y * w + xx];
                }
                tmp[y * w + x] = m;
            }
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte m = 0;
                for (var i = -spread; i <= spread; i++)
                {
                    var yy = y + i;
                    if ((uint)yy >= (uint)h) continue;
                    if (tmp[yy * w + x] > m) m = tmp[yy * w + x];
                }
                alpha[y * w + x] = m;
            }
        }

        var result = new uint[w * h];
        for (var i = 0; i < result.Length; i++)
        {
            if (alpha[i] == 0) continue;
            result[i] = FromColor(color, (int)(alpha[i] * opacity * color.Alpha / 255f));
        }
        if (blur > 0) result = GaussianBlur(result, w, h, blur, ctx.Cancellation);
        return result;
    }

    /// <summary>
    /// 擠出用的位移清單：從 (offsetX, offsetY) 起，沿位移方向每 1px 一步、共 thickness 步（去重）。
    /// 位移為零時沿右下 45° 擠出，厚度才不會沒地方長。
    /// </summary>
    internal static IReadOnlyList<(int X, int Y)> ExtrusionOffsets(int offsetX, int offsetY, int thickness)
    {
        var list = new List<(int, int)> { (offsetX, offsetY) };
        if (thickness <= 0) return list;
        float dx = offsetX, dy = offsetY;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) { dx = 1; dy = 1; len = MathF.Sqrt(2); }
        dx /= len; dy /= len;
        var seen = new HashSet<(int, int)> { (offsetX, offsetY) };
        // 步距取 1/√2 才不會在斜向走出缺口（每步至少一軸前進 <1px）
        var step = 0.7f;
        for (var t = step; t <= thickness + 1e-3f; t += step)
        {
            var o = ((int)MathF.Round(offsetX + dx * t), (int)MathF.Round(offsetY + dy * t));
            if (seen.Add(o)) list.Add(o);
        }
        return list;
    }
}
