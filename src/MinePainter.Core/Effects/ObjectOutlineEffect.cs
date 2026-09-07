using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>物件外框：在不透明內容外圍描一圈顏色（文字外框就是這個；疊多筆 = 多層外框）。</summary>
public sealed record ObjectOutlineEffect : IEffect
{
    public int Width { get; init; } = 5;     // 1..60（滑桿；內部上限 100）
    public int Softness { get; init; } = 0;  // 0..100
    /// <summary>平滑半徑（px）：先把邊緣小於此尺寸的凹縫／細洞補平再描外框，內側小抖動不會帶動外框。</summary>
    public int Smooth { get; init; } = 0;    // 0..20
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>外框用漸層上色（GradientStops 沿 GradientAngle，以「內容＋外框」的外接框為準）。</summary>
    public bool Gradient { get; init; }
    public float GradientAngle { get; init; } = 90f;

    private readonly GradientStops? _gradientStops;

    /// <summary>漸層節點；沒設定過時預設「外框色 → 白」（跟著 Color 走，改主色時漸層起點也跟著換）。</summary>
    public GradientStops GradientStops
    {
        get => _gradientStops ?? GradientStops.Two(Color, SKColors.White);
        init => _gradientStops = value;
    }

    /// <summary>相容舊欄位：漸層末節點的顏色。</summary>
    public SKColor GradientEnd
    {
        get => GradientStops.Last;
        init => GradientStops = GradientStops.WithEnd(value);
    }


    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;

    public static readonly string[] PositionNames = ["外側", "中央", "內側"];

    /// <summary>
    /// 外框畫在邊緣的哪一側（PS 筆畫的「位置」）：0 外側（預設，往外長）、1 中央（內外各一半）、2 內側（往內長，不會變胖）。
    /// 內側那一半以「到透明的距離」量，只畫在物件本身的像素上，形狀外框不會長大。
    /// </summary>
    public int Position { get; init; }

    public string Name => "外框";
    public string Category => "物件";

    private int ClampedWidth => Math.Min(Width, 100);
    private int ClampedSmooth => Math.Clamp(Smooth, 0, 20);

    /// <summary>往外長的那一部分寬度（外側＝全部、中央＝一半、內側＝0）。</summary>
    private int OuterWidth => Position switch { 1 => (ClampedWidth + 1) / 2, 2 => 0, _ => ClampedWidth };

    /// <summary>往內長的那一部分寬度。</summary>
    private int InnerWidth => Position switch { 1 => ClampedWidth / 2, 2 => ClampedWidth, _ => 0 };

    /// <summary>
    /// 漸層要看整個內容的外接框，所以得整層算；純色只需要外框寬度的來源餘裕。
    /// 平滑（閉運算）膨脹再侵蝕各 r，所以來源餘裕要再加 2r。
    /// </summary>
    public int SourceMargin => Gradient ? EffectContext.WholeLayer : ClampedWidth + ClampedSmooth * 2 + 2;


    /// <summary>輸出會延伸到內容外多遠（快取範圍用）：補平的凹縫最遠離原內容 r；內側外框不會長出去。</summary>
    public int OutputMargin => OuterWidth == 0 ? 0 : OuterWidth + ClampedSmooth + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("width", "寬度", 1, 60, o => ((ObjectOutlineEffect)o).Width,
            (o, v) => ((ObjectOutlineEffect)o) with { Width = (int)v }) { Geometric = true },
        new ChoiceParam("position", "位置", PositionNames, o => ((ObjectOutlineEffect)o).Position,
            (o, v) => ((ObjectOutlineEffect)o) with { Position = Math.Clamp(v, 0, 2) }),
        new SliderParam("softness", "柔邊", 0, 100, o => ((ObjectOutlineEffect)o).Softness,
            (o, v) => ((ObjectOutlineEffect)o) with { Softness = (int)v }),
        new SliderParam("smooth", "平滑", 0, 20, o => ((ObjectOutlineEffect)o).Smooth,
            (o, v) => ((ObjectOutlineEffect)o) with { Smooth = (int)v }) { Geometric = true },
        new ColorParam("color", "顏色", o => ((ObjectOutlineEffect)o).Color,
            (o, v) => ((ObjectOutlineEffect)o) with { Color = v }) { UsePrimaryByDefault = true },
        new BoolParam("gradient", "漸層外框", o => ((ObjectOutlineEffect)o).Gradient,
            (o, v) => ((ObjectOutlineEffect)o) with { Gradient = v }),
        new GradientParam("gradientStops", "漸層", o => ((ObjectOutlineEffect)o).GradientStops,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientStops = v })
            { LegacyStartKey = "color", LegacyEndKey = "gradientEnd" },
        new AngleParam("gradientAngle", "漸層角度", 0, 360, o => ((ObjectOutlineEffect)o).GradientAngle,
            (o, v) => ((ObjectOutlineEffect)o) with { GradientAngle = (float)v }),
        new BoolParam("relative", "角度跟著物件轉", o => ((ObjectOutlineEffect)o).RelativeToObject,
            (o, v) => ((ObjectOutlineEffect)o) with { RelativeToObject = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var width = ClampedWidth;
        var outer = OuterWidth;
        var inner = InnerWidth;
        var smooth = ClampedSmooth;
        var pad = width + smooth * 2 + 2;
        var dist = outer > 0 ? DistanceTransform.FromAlphaClosed(ctx, pad, smooth, Math.Min(smooth, outer / 2)) : null;
        var distIn = inner > 0 ? DistanceTransform.ToTransparent(ctx, pad, canvasEdge: false) : null;
        var dw = ctx.Width + pad * 2;
        var soft = Math.Max(0.5f, width * Softness / 100f);
        var color = Color;

        // 漸層：以「內容外接框外擴外框寬度」為漸層框，沿角度由 Color 到 GradientEnd
        GradientRamp? ramp = null;
        if (Gradient)
        {
            var bbox = ContentBox(ctx);
            if (!bbox.IsEmpty)
            {
                bbox.Inflate(width, width);
                ramp = new GradientRamp(bbox, ctx.FollowedAngleCw(GradientAngle, RelativeToObject),
                    radial: false, GradientStops);
            }
        }

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var i = (y + pad) * dw + (x + pad);
                var result = src;
                SKColor? c = null;
                if (dist != null)
                {
                    // 外側：墊在內容底下
                    var d = dist[i];
                    var coverage = soft <= 0.5f
                        ? Math.Clamp(outer - d + 0.5f, 0f, 1f)
                        : Math.Clamp((outer - d + 0.5f) / soft, 0f, 1f);
                    if (coverage > 0f)
                    {
                        c ??= ramp?.At(x, y) ?? color;
                        result = Over(result, FromColor(c.Value, (int)(c.Value.Alpha * coverage)));
                    }
                }
                if (distIn != null && A(src) > 0)
                {
                    // 內側：離透明愈近愈滿；畫在內容上面、只畫在物件自己的像素上（乘上自己的 alpha）
                    var d = distIn[i];
                    var coverage = soft <= 0.5f
                        ? Math.Clamp(inner - d + 0.5f, 0f, 1f)
                        : Math.Clamp((inner - d + 0.5f) / soft, 0f, 1f);
                    if (coverage > 0f)
                    {
                        c ??= ramp?.At(x, y) ?? color;
                        result = Over(FromColor(c.Value, (int)(c.Value.Alpha * coverage * A(src) / 255f)), result);
                    }
                }
                ctx.Dst[y * ctx.Width + x] = result;
            }
        });
    }

    /// <summary>來源內容（alpha > 0）的外接框，目標座標。</summary>
    internal static SKRectI ContentBox(EffectContext ctx)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < ctx.Height; y++)
        for (var x = 0; x < ctx.Width; x++)
        {
            if (A(ctx.SrcAt(x, y)) == 0) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
