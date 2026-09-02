using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>墨水素描：5×5 拉普拉斯取邊當墨線，乘上（去色 ↔ 原色）混合的底色。</summary>
public sealed record InkSketchEffect : IEffect
{
    public int InkOutline { get; init; } = 50;  // 0..99
    public int Coloring { get; init; } = 50;    // 0..100

    public string Name => "墨水素描";
    public string Category => "藝術";
    public int SourceMargin => 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("ink", "墨線", 0, 99, o => ((InkSketchEffect)o).InkOutline,
            (o, v) => ((InkSketchEffect)o) with { InkOutline = (int)v }),
        new SliderParam("coloring", "著色", 0, 100, o => ((InkSketchEffect)o).Coloring,
            (o, v) => ((InkSketchEffect)o) with { Coloring = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var strength = (InkOutline + 1) / 25f;   // 0.04..4
        var coloring = Coloring / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                if (a == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = 0;
                    continue;
                }

                // 5×5：中心 24、其餘 -1（拉普拉斯）
                var center = Intensity(p);
                var sum = 0;
                for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    sum += Intensity(ctx.SrcAt(x + dx, y + dy));
                }
                var edge = Math.Abs(center * 24 - sum) / 24f;
                var ink = 255 - Clamp255(edge * strength);

                var gray = Intensity(b, g, r);
                var ob = gray + (b - gray) * coloring;
                var og = gray + (g - gray) * coloring;
                var or = gray + (r - gray) * coloring;
                ctx.Dst[y * ctx.Width + x] = Premul(
                    (int)(ob * ink / 255f), (int)(og * ink / 255f), (int)(or * ink / 255f), a);
            }
        });
    }
}

/// <summary>油畫：視窗內依亮度分桶（粗糙度 = 桶數），取最多數桶的平均色。</summary>
public sealed record OilPaintingEffect : IEffect
{
    public int BrushSize { get; init; } = 3;    // 1..8
    public int Coarseness { get; init; } = 50;  // 3..255

    public string Name => "油畫";
    public string Category => "藝術";
    public int SourceMargin => BrushSize;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("brush", "筆刷大小", 1, 8, o => ((OilPaintingEffect)o).BrushSize,
            (o, v) => ((OilPaintingEffect)o) with { BrushSize = (int)v }),
        new SliderParam("coarseness", "粗糙度", 3, 255, o => ((OilPaintingEffect)o).Coarseness,
            (o, v) => ((OilPaintingEffect)o) with { Coarseness = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var radius = Math.Clamp(BrushSize, 1, 8);
        var levels = Math.Clamp(Coarseness, 3, 255);
        ctx.ForRows(y =>
        {
            Span<int> count = stackalloc int[256];
            Span<int> sumB = stackalloc int[256];
            Span<int> sumG = stackalloc int[256];
            Span<int> sumR = stackalloc int[256];
            Span<int> sumA = stackalloc int[256];
            for (var x = 0; x < ctx.Width; x++)
            {
                count.Clear(); sumB.Clear(); sumG.Clear(); sumR.Clear(); sumA.Clear();
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var p = ctx.SrcAt(x + dx, y + dy);
                    Unpremul(p, out var b, out var g, out var r, out var a);
                    var bucket = Intensity(b, g, r) * (levels - 1) / 255;
                    count[bucket]++;
                    sumB[bucket] += b; sumG[bucket] += g; sumR[bucket] += r; sumA[bucket] += a;
                }
                var best = 0;
                for (var i = 1; i < levels; i++)
                    if (count[i] > count[best]) best = i;
                var n = Math.Max(1, count[best]);
                ctx.Dst[y * ctx.Width + x] = Premul(sumB[best] / n, sumG[best] / n, sumR[best] / n, sumA[best] / n);
            }
        });
    }
}

/// <summary>鉛筆素描：去色後與「反相模糊」做顏色加亮（color dodge），得到鉛筆線稿。</summary>
public sealed record PencilSketchEffect : IEffect
{
    public int PencilTipSize { get; init; } = 2; // 1..20
    public int Range { get; init; } = 0;         // 0..20

    public string Name => "鉛筆素描";
    public string Category => "藝術";
    public int SourceMargin => GaussianMargin(PencilTipSize);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("tip", "筆尖大小", 1, 20, o => ((PencilSketchEffect)o).PencilTipSize,
            (o, v) => ((PencilSketchEffect)o) with { PencilTipSize = (int)v }),
        new SliderParam("range", "色彩範圍", 0, 20, o => ((PencilSketchEffect)o).Range,
            (o, v) => ((PencilSketchEffect)o) with { Range = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        // 反相灰階（保 alpha）→ 高斯模糊
        var inv = new uint[ctx.Src.Length];
        for (var i = 0; i < inv.Length; i++)
        {
            var p = ctx.Src[i];
            Unpremul(p, out var b, out var g, out var r, out var a);
            var gray = 255 - Intensity(b, g, r);
            inv[i] = Premul(gray, gray, gray, a);
        }
        var blurred = GaussianBlur(inv, ctx.SrcWidth, ctx.SrcHeight, PencilTipSize, ctx.Cancellation);
        var k = 1f + Range * 0.15f;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                Unpremul(p, out var b, out var g, out var r, out var a);
                var gray = Intensity(b, g, r);
                var q = blurred[(y + ctx.SrcOffsetY) * ctx.SrcWidth + (x + ctx.SrcOffsetX)];
                Unpremul(q, out var bb, out _, out _, out _);
                // color dodge：gray / (1 - blurredInverted)
                var dodge = bb >= 255 ? 255 : Math.Min(255, gray * 255 / (255 - bb));
                var v = 255 - Clamp255((255 - dodge) * k);
                ctx.Dst[y * ctx.Width + x] = Premul(v, v, v, a);
            }
        });
    }
}
