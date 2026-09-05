using SkiaSharp;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 斜角和浮雕（Photoshop 的「斜角和浮雕」）：把物件邊緣當成一道斜坡打光 —— 朝光源的坡面亮、背光的坡面暗。
///
/// 作法：用距離場做一張高度圖（內斜角：離邊緣愈深愈高，<see cref="Size"/> 格內從 0 爬到 1；
/// 外斜角：形狀外側從 1 降到 0；浮雕：兩邊接起來；枕狀浮雕：內側反過來凹下去），
/// 柔化就是把高度圖模糊，接著取梯度當法向量，與光源方向（角度＋高度）做內積：正的畫亮部色、負的畫陰影色。
/// <see cref="Depth"/> 是坡面陡度（100% 時 Size 格爬滿一格高，坡面 45°）。
/// 角度用數學慣例（0 = 右、90 = 上，逆時針），與 PS 一致，匯入時不必換算。
/// </summary>
public sealed record BevelEmbossEffect : IEffect
{
    public static readonly string[] StyleNames = ["內斜角", "外斜角", "浮雕", "枕狀浮雕"];

    /// <summary>0 內斜角、1 外斜角、2 浮雕、3 枕狀浮雕。</summary>
    public int Style { get; init; }

    /// <summary>true = 亮部朝光源（凸起）；false = 反過來（凹陷，PS 的方向「下」）。</summary>
    public bool Up { get; init; } = true;

    public int Size { get; init; } = 5;          // 1..50
    public int Depth { get; init; } = 100;       // 1..1000 %
    public int Soften { get; init; } = 0;        // 0..16
    public float Angle { get; init; } = 120f;    // 光源方位（數學慣例）
    public float Altitude { get; init; } = 30f;  // 光源高度 0..90
    public SKColor HighlightColor { get; init; } = SKColors.White;
    public int HighlightOpacity { get; init; } = 75;
    public SKColor ShadowColor { get; init; } = SKColors.Black;
    public int ShadowOpacity { get; init; } = 75;

    /// <summary>方向跟著物件轉（預設）；關掉＝以畫布為準。</summary>
    public bool RelativeToObject { get; init; } = true;

    public string Name => "斜角和浮雕";
    public string Category => "物件";

    private int ClampedSize => Math.Clamp(Size, 1, 50);
    private int ClampedSoften => Math.Clamp(Soften, 0, 16);
    private bool PaintsOutside => Style is 1 or 2 or 3;

    public int SourceMargin => ClampedSize + ClampedSoften * 2 + 2;
    public int OutputMargin => PaintsOutside ? ClampedSize + ClampedSoften + 2 : 0;

    private static readonly ParamDef[] Params =
    [
        new ChoiceParam("style", "樣式", StyleNames, o => ((BevelEmbossEffect)o).Style,
            (o, v) => ((BevelEmbossEffect)o) with { Style = Math.Clamp(v, 0, 3) }),
        new BoolParam("up", "凸起（關掉＝凹陷）", o => ((BevelEmbossEffect)o).Up,
            (o, v) => ((BevelEmbossEffect)o) with { Up = v }),
        new SliderParam("size", "大小", 1, 50, o => ((BevelEmbossEffect)o).Size,
            (o, v) => ((BevelEmbossEffect)o) with { Size = (int)v }) { Geometric = true },
        new SliderParam("depth", "深度", 1, 1000, o => ((BevelEmbossEffect)o).Depth,
            (o, v) => ((BevelEmbossEffect)o) with { Depth = (int)v }, "%"),
        new SliderParam("soften", "柔化", 0, 16, o => ((BevelEmbossEffect)o).Soften,
            (o, v) => ((BevelEmbossEffect)o) with { Soften = (int)v }) { Geometric = true },
        new AngleParam("angle", "光源角度", 0, 360, o => ((BevelEmbossEffect)o).Angle,
            (o, v) => ((BevelEmbossEffect)o) with { Angle = (float)v }),
        new SliderParam("altitude", "光源高度", 0, 90, o => ((BevelEmbossEffect)o).Altitude,
            (o, v) => ((BevelEmbossEffect)o) with { Altitude = (float)v }, "°"),
        new ColorParam("highlightColor", "亮部顏色", o => ((BevelEmbossEffect)o).HighlightColor,
            (o, v) => ((BevelEmbossEffect)o) with { HighlightColor = v }),
        new SliderParam("highlightOpacity", "亮部不透明度", 0, 100, o => ((BevelEmbossEffect)o).HighlightOpacity,
            (o, v) => ((BevelEmbossEffect)o) with { HighlightOpacity = (int)v }, "%"),
        new ColorParam("shadowColor", "陰影顏色", o => ((BevelEmbossEffect)o).ShadowColor,
            (o, v) => ((BevelEmbossEffect)o) with { ShadowColor = v }),
        new SliderParam("shadowOpacity", "陰影不透明度", 0, 100, o => ((BevelEmbossEffect)o).ShadowOpacity,
            (o, v) => ((BevelEmbossEffect)o) with { ShadowOpacity = (int)v }, "%"),
        new BoolParam("relative", "方向跟著物件轉", o => ((BevelEmbossEffect)o).RelativeToObject,
            (o, v) => ((BevelEmbossEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var size = ClampedSize;
        var soften = ClampedSoften;
        var pad = size + soften * 2 + 2;
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;

        var height = HeightField(ctx, pad, size);
        if (soften > 0) height = BoxBlur(BoxBlur(height, w, h, soften), w, h, soften);

        // 光源向量（螢幕座標 y 往下）：方位角逆時針、高度角抬起
        var az = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var alt = Math.Clamp(Altitude, 0f, 90f) * MathF.PI / 180f;
        var lx = MathF.Cos(az) * MathF.Cos(alt);
        var ly = -MathF.Sin(az) * MathF.Cos(alt);
        var lz = MathF.Sin(alt);
        var slope = size * Math.Clamp(Depth, 1, 1000) / 100f;   // 深度 100%：Size 格爬滿一格高
        var sign = Up ? 1f : -1f;
        var flat = lz;   // 平面（法向量 (0,0,1)）受光量：坡面比它亮才算亮部、比它暗才算陰影
        var gain = 1f / Math.Max(0.05f, MathF.Cos(alt));   // 光源低角度時對比更強，與 PS 的感覺一致

        var hlAlpha = HighlightOpacity / 100f * HighlightColor.Alpha / 255f;
        var shAlpha = ShadowOpacity / 100f * ShadowColor.Alpha / 255f;
        var paintsOutside = PaintsOutside;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var a = A(src);
                if (a == 0 && !paintsOutside)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }

                var px = x + pad;
                var py = y + pad;
                var dhx = (height[py * w + Math.Min(px + 1, w - 1)] - height[py * w + Math.Max(px - 1, 0)]) * 0.5f * slope * sign;
                var dhy = (height[Math.Min(py + 1, h - 1) * w + px] - height[Math.Max(py - 1, 0) * w + px]) * 0.5f * slope * sign;
                if (dhx == 0f && dhy == 0f)
                {
                    ctx.Dst[y * ctx.Width + x] = src;
                    continue;
                }
                var inv = 1f / MathF.Sqrt(dhx * dhx + dhy * dhy + 1f);
                var shade = ((-dhx * lx) + (-dhy * ly) + lz) * inv - flat;
                shade = Math.Clamp(shade * gain, -1f, 1f);

                uint paint;
                if (shade > 0f) paint = FromColor(HighlightColor, (int)(255 * shade * hlAlpha));
                else paint = FromColor(ShadowColor, (int)(255 * -shade * shAlpha));

                // 內側的打光只留在物件內（乘上自己的 alpha）；外側（外斜角／浮雕）畫在透明處
                if (a < 255 && !paintsOutside) paint = Lerp256(0, paint, a);
                ctx.Dst[y * ctx.Width + x] = Over(paint, src);   // 打光疊在內容上面（外側的畫在透明處，一樣成立）
            }
        });
    }

    /// <summary>高度圖（含 pad 的來源範圍）：依樣式由內外距離場拼出 0..1 的坡。</summary>
    private float[] HeightField(EffectContext ctx, int pad, int size)
    {
        var w = ctx.Width + pad * 2;
        var h = ctx.Height + pad * 2;
        var field = new float[w * h];
        var inside = Style is 0 or 2 or 3 ? DistanceTransform.ToTransparent(ctx, pad, canvasEdge: false) : null;
        var outside = Style is 1 or 2 or 3 ? DistanceTransform.FromAlpha(ctx, pad) : null;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var a = A(ctx.SrcOrTransparent(x - pad, y - pad));
            float v;
            switch (Style)
            {
                case 0:   // 內斜角：形狀內從邊緣往裡爬到 1；外面平的 0
                    v = a == 0 ? 0f : Ramp(inside![i], size);
                    break;
                case 1:   // 外斜角：形狀內是平台 1；外面從邊緣往外降到 0
                    v = a > 0 ? 1f : 1f - Ramp(outside![i], size);
                    break;
                case 2:   // 浮雕：外面 0 → 邊緣 0.5 → 裡面 1，一條連續的坡
                    v = a > 0 ? 0.5f + 0.5f * Ramp(inside![i], size) : 0.5f - 0.5f * Ramp(outside![i], size);
                    break;
                default:  // 枕狀浮雕：邊緣是稜線（1），往外往內都降下去
                    v = a > 0 ? 1f - Ramp(inside![i], size) : 1f - Ramp(outside![i], size);
                    break;
            }
            field[i] = v;
        }
        return field;
    }

    private static float Ramp(float distance, int size) => Math.Clamp((distance + 0.5f) / size, 0f, 1f);

    /// <summary>可分離的方框模糊（邊界取最近值）。</summary>
    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        var tmp = new float[w * h];
        var dst = new float[w * h];
        var inv = 1f / (2 * r + 1);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                sum += src[row + Math.Clamp(x + r + 1, 0, w - 1)] - src[row + Math.Clamp(x - r, 0, w - 1)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            for (var k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                sum += tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x] - tmp[Math.Clamp(y - r, 0, h - 1) * w + x];
            }
        }
        return dst;
    }
}
