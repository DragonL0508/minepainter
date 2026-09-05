using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 內陰影（Photoshop 的「內陰影」）：物件內側、靠光源那一邊的暗影 —— 像物件被壓進紙裡。
/// 作法：把自己的形狀往陰影方向位移，「原形狀有、位移後沒有」的那一圈就是陰影，
/// 先填塞（把位移後的形狀侵蝕幾格，陰影變厚）、再模糊，最後只留在物件內部（乘上自己的 alpha）。
/// 角度用數學慣例（0 = 右、90 = 上，逆時針）指「光從哪來」，陰影落在對面 —— 與 PS 一致，匯入時不必換算。
/// </summary>
public sealed record InnerShadowEffect : IEffect
{
    /// <summary>光源角度（度，數學慣例）。</summary>
    public float Angle { get; init; } = 120f;
    public int Distance { get; init; } = 5;    // 0..50
    public int Choke { get; init; } = 0;       // 0..100（%：先填塞 Size 的幾成）
    public int Size { get; init; } = 5;        // 0..50（模糊）
    public int Opacity { get; init; } = 75;    // 0..100
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>方向跟著物件轉（預設）：文字轉了 45°，內陰影也跟著轉；關掉＝以畫布為準。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "內陰影";
    public string Category => "物件";

    /// <summary>位移＋模糊都會用到鄰近的來源；輸出不會長到內容外（乘回自己的 alpha）。</summary>
    public int SourceMargin => Math.Clamp(Distance, 0, 50) + ChokePixels + GaussianMargin(Math.Clamp(Size, 0, 50));
    public int OutputMargin => 0;

    private int ChokePixels => (int)MathF.Round(Math.Clamp(Size, 0, 50) * Math.Clamp(Choke, 0, 100) / 100f);

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "光源角度", 0, 360, o => ((InnerShadowEffect)o).Angle,
            (o, v) => ((InnerShadowEffect)o) with { Angle = (float)v }),
        new SliderParam("distance", "距離", 0, 50, o => ((InnerShadowEffect)o).Distance,
            (o, v) => ((InnerShadowEffect)o) with { Distance = (int)v }) { Geometric = true },
        new SliderParam("choke", "填塞", 0, 100, o => ((InnerShadowEffect)o).Choke,
            (o, v) => ((InnerShadowEffect)o) with { Choke = (int)v }, "%"),
        new SliderParam("size", "大小", 0, 50, o => ((InnerShadowEffect)o).Size,
            (o, v) => ((InnerShadowEffect)o) with { Size = (int)v }) { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((InnerShadowEffect)o).Opacity,
            (o, v) => ((InnerShadowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((InnerShadowEffect)o).Color,
            (o, v) => ((InnerShadowEffect)o) with { Color = v }),
        new BoolParam("relative", "方向跟著物件轉", o => ((InnerShadowEffect)o).RelativeToObject,
            (o, v) => ((InnerShadowEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var w = ctx.SrcWidth;
        var h = ctx.SrcHeight;
        var distance = Math.Clamp(Distance, 0, 50);
        var size = Math.Clamp(Size, 0, 50);
        var choke = ChokePixels;
        var blur = Math.Max(0, size - choke);

        // 光從 angle 來 → 陰影往對面：數學座標的 (−cos, sin) 換到螢幕（y 往下）是 (−cos, +sin)
        var lightCcw = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var ox = (int)MathF.Round(-MathF.Cos(lightCcw) * distance);
        var oy = (int)MathF.Round(MathF.Sin(lightCcw) * distance);

        // 位移後的形狀（畫布外／來源外視為透明）
        var shifted = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sx = x - ox;
            var sy = y - oy;
            shifted[y * w + x] = (uint)sx < (uint)w && (uint)sy < (uint)h ? (byte)A(ctx.Src[sy * w + sx]) : (byte)0;
        }
        if (choke > 0) shifted = Erode(shifted, w, h, choke);

        // 陰影 = 原形狀 ∧ ¬位移形狀，上色後模糊
        var shadow = new uint[w * h];
        var alphaScale = Opacity / 100f * Color.Alpha / 255f;
        for (var i = 0; i < shadow.Length; i++)
        {
            var a = A(ctx.Src[i]);
            if (a == 0) continue;
            var inside = 255 - shifted[i];
            if (inside == 0) continue;
            shadow[i] = FromColor(Color, (int)(inside * alphaScale));
        }
        if (blur > 0) shadow = GaussianBlur(shadow, w, h, blur, ctx.Cancellation);

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
                var s = shadow[(y + ctx.SrcOffsetY) * w + (x + ctx.SrcOffsetX)];
                // 只留在物件內部：陰影乘上自己的 alpha，邊緣的抗鋸齒像素才不會被塗滿
                if (a < 255) s = Lerp256(0, s, a);
                ctx.Dst[y * ctx.Width + x] = Over(s, src);   // 陰影疊在內容上面
            }
        });
    }

    /// <summary>分離的最小值濾波（水平 + 垂直），把形狀往內縮 r 格。</summary>
    internal static byte[] Erode(byte[] alpha, int w, int h, int r)
    {
        var tmp = new byte[w * h];
        var dst = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            byte m = 255;
            for (var i = -r; i <= r; i++)
            {
                var xx = x + i;
                var v = (uint)xx < (uint)w ? alpha[y * w + xx] : (byte)0;
                if (v < m) m = v;
            }
            tmp[y * w + x] = m;
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            byte m = 255;
            for (var i = -r; i <= r; i++)
            {
                var yy = y + i;
                var v = (uint)yy < (uint)h ? tmp[yy * w + x] : (byte)0;
                if (v < m) m = v;
            }
            dst[y * w + x] = m;
        }
        return dst;
    }
}
