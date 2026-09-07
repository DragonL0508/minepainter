using SkiaSharp;
using MinePainter.Core.Layers;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>
/// 物件內光暈：光從物件的邊緣往內亮，形狀不變（只染色，不動 alpha）——
/// 外光暈是把光畫在物件外面，內光暈是畫在自己身上，所以剪影一模一樣。
/// 距離＝到最近透明像素的距離：邊緣 <c>擴散</c> px 內是滿的，再往內 <c>大小</c> px 淡出。
/// 「指定角度」模式：只有朝向光源那一側的邊緣會亮——距離改成「沿光源方向走到透明為止」的長度，
/// 背光側走不到透明（或走太遠）就不亮，像打了一盞側光。
/// </summary>
public sealed record InnerGlowEffect : IEffect
{
    public int Size { get; init; } = 12;      // 1..100（往內淡出幾 px）
    public int Spread { get; init; } = 0;     // 0..30（貼著邊緣全滿的厚度）
    public int Opacity { get; init; } = 85;   // 0..100
    public SKColor Color { get; init; } = new(0xFF, 0xD3, 0x4A);

    /// <summary>true = 指定角度（只亮朝向光源的邊）；false = 均勻（四周一樣亮）。</summary>
    public bool Directional { get; init; }

    /// <summary>光源方向（度，數學慣例：0 = 右、90 = 上；與角度轉盤一致）。</summary>
    public float Angle { get; init; } = 90f;

    /// <summary>畫布邊界也算物件邊（貼齊畫布邊的物件那一側要不要也發光）。</summary>
    public bool GlowCanvasEdge { get; init; }

    /// <summary>角度跟著物件轉（預設）：文字轉了 45°，這個方向也跟著轉；關掉＝以畫布為準。
    /// 與傾斜、漸層的同名選項是同一件事（見 <see cref="EffectContext.ContentRotation"/>）。</summary>
    public bool RelativeToObject { get; init; } = true;


    public string Name => "內光暈";
    public string Category => "物件";
    public int SourceMargin => Pad;

    private int Pad => Math.Min(Size, 100) + Math.Min(Spread, 30) + 2;

    private static readonly ParamDef ModeParam =
        new ChoiceParam("mode", "模式", ["均勻", "指定角度"], o => ((InnerGlowEffect)o).Directional ? 1 : 0,
            (o, v) => ((InnerGlowEffect)o) with { Directional = v == 1 });
    private static readonly ParamDef AngleDef =
        new AngleParam("angle", "光源角度", 0, 360, o => ((InnerGlowEffect)o).Angle,
            (o, v) => ((InnerGlowEffect)o) with { Angle = (float)v });
    private static readonly ParamDef RelativeDef =
        new BoolParam("relative", "角度跟著物件轉", o => ((InnerGlowEffect)o).RelativeToObject,
            (o, v) => ((InnerGlowEffect)o) with { RelativeToObject = v });
    private static readonly ParamDef[] Common =
    [
        new SliderParam("size", "大小", 1, 50, o => ((InnerGlowEffect)o).Size,
            (o, v) => ((InnerGlowEffect)o) with { Size = (int)v }, "px") { Geometric = true },
        new SliderParam("spread", "擴散", 0, 30, o => ((InnerGlowEffect)o).Spread,
            (o, v) => ((InnerGlowEffect)o) with { Spread = (int)v }, "px") { Geometric = true },
        new SliderParam("opacity", "不透明度", 0, 100, o => ((InnerGlowEffect)o).Opacity,
            (o, v) => ((InnerGlowEffect)o) with { Opacity = (int)v }, "%"),
        new ColorParam("color", "顏色", o => ((InnerGlowEffect)o).Color,
            (o, v) => ((InnerGlowEffect)o) with { Color = v }),
        new BoolParam("canvasEdge", "畫布邊緣也發光", o => ((InnerGlowEffect)o).GlowCanvasEdge,
            (o, v) => ((InnerGlowEffect)o) with { GlowCanvasEdge = v }),
    ];
    private static readonly ParamDef[] UniformParams = [ModeParam, .. Common];
    private static readonly ParamDef[] DirectionalParams = [ModeParam, AngleDef, RelativeDef, .. Common];

    // 角度轉盤只在「指定角度」時出現（ChoiceParam 改動會讓 ParamEditor 重建）
    public IReadOnlyList<ParamDef> Parameters => Directional ? DirectionalParams : UniformParams;

    public void Render(EffectContext ctx)
    {
        var size = Math.Min(Size, 100);
        var spread = Math.Min(Spread, 30);
        var opacity = Math.Clamp(Opacity, 0, 100) / 100f;
        if (opacity <= 0f) { ctx.CopySrcToDst(); return; }

        var pad = Pad;
        var dist = Directional ? null : DistanceTransform.ToTransparent(ctx, pad, GlowCanvasEdge);
        var dw = ctx.Width + pad * 2;
        int cr = Color.Red, cg = Color.Green, cb = Color.Blue;
        var reach = spread + size;

        // 朝光源走的單位向量（螢幕座標 y 朝下，所以 sin 取負：90° = 往上找邊）
        var rad = ctx.FollowedAngleCcw(Angle, RelativeToObject) * MathF.PI / 180f;
        var ux = MathF.Cos(rad);
        var uy = -MathF.Sin(rad);

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var src = ctx.SrcAt(x, y);
                var i = y * ctx.Width + x;
                if (A(src) == 0) { ctx.Dst[i] = src; continue; } // 物件外不畫

                var d = dist != null
                    ? dist[(y + pad) * dw + (x + pad)]
                    : DirectionalDistance(ctx, x, y, ux, uy, reach);
                if (d >= reach) { ctx.Dst[i] = src; continue; }

                // 邊緣 spread px 內全滿，再往內 size px 用 smoothstep 淡出
                var t = Math.Clamp((d - spread) / size, 0f, 1f);
                var k = 1f - t * t * (3f - 2f * t);
                var f = opacity * k;
                if (f <= 0f) { ctx.Dst[i] = src; continue; }

                // 只把顏色往光暈色推，alpha 原封不動 —— 剪影不會變胖也不會變糊
                Unpremul(src, out var b, out var g, out var r, out var a);
                ctx.Dst[i] = Premul(
                    Clamp255(b + (cb - b) * f),
                    Clamp255(g + (cg - g) * f),
                    Clamp255(r + (cr - r) * f),
                    a);
            }
        });
    }

    /// <summary>
    /// 從 (x, y) 沿 (ux, uy) 每次走 1px，走到第一個透明像素為止的距離；走了 reach 還沒碰到就回 reach。
    /// 用前後兩點的 alpha 線性內插把碰邊的位置補到次像素，斜向的邊才不會一階一階。
    /// 畫布外：GlowCanvasEdge 時算透明（貼齊畫布邊那側也會亮），否則視為走不到邊。
    /// </summary>
    private float DirectionalDistance(EffectContext ctx, int x, int y, float ux, float uy, int reach)
    {
        var prevA = 255;
        for (var k = 1; k <= reach; k++)
        {
            var sx = (int)MathF.Floor(x + 0.5f + ux * k);
            var sy = (int)MathF.Floor(y + 0.5f + uy * k);
            var dx = ctx.Region.Left + sx;
            var dy = ctx.Region.Top + sy;
            int a;
            if (dx < 0 || dy < 0 || dx >= ctx.DocSize.Width || dy >= ctx.DocSize.Height)
            {
                if (!GlowCanvasEdge) return reach;
                a = 0;
            }
            else a = A(ctx.SrcOrTransparent(sx, sy));

            if (a < 128)
            {
                // prevA ≥ 128 > a：邊界落在 k-1 與 k 之間
                var frac = prevA == a ? 0f : (prevA - 128f) / (prevA - a);
                return Math.Max(0f, k - 1 + frac);
            }
            prevA = a;
        }
        return reach;
    }
}
