using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>加入雜訊：每像素加上高斯亂數（彩度控制三通道是否各自獨立）。</summary>
public sealed record AddNoiseEffect : IEffect
{
    public int Intensity { get; init; } = 64;        // 0..100
    public int ColorSaturation { get; init; } = 100; // 0..400
    public int Coverage { get; init; } = 100;        // 0..100
    public int Seed { get; init; } = 0;

    public string Name => "加入雜訊";
    public string Category => "雜訊";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("intensity", "強度", 0, 100, o => ((AddNoiseEffect)o).Intensity,
            (o, v) => ((AddNoiseEffect)o) with { Intensity = (int)v }),
        new SliderParam("saturation", "色彩飽和度", 0, 400, o => ((AddNoiseEffect)o).ColorSaturation,
            (o, v) => ((AddNoiseEffect)o) with { ColorSaturation = (int)v }),
        new SliderParam("coverage", "覆蓋率", 0, 100, o => ((AddNoiseEffect)o).Coverage,
            (o, v) => ((AddNoiseEffect)o) with { Coverage = (int)v }),
        new SliderParam("seed", "種子", 0, 255, o => ((AddNoiseEffect)o).Seed,
            (o, v) => ((AddNoiseEffect)o) with { Seed = (int)v }) { IsSeed = true },
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var amp = Intensity * 1.28f;
        var sat = ColorSaturation / 100f;
        var coverage = Coverage / 100f;
        ctx.ForRows(y =>
        {
            var rng = new XorShift((uint)(y * 2246822519u + (uint)Seed * 3266489917u + 7));
            for (var x = 0; x < ctx.Width; x++)
            {
                var p = ctx.SrcAt(x, y);
                if (rng.NextFloat() >= coverage || A(p) == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = p;
                    continue;
                }
                Unpremul(p, out var b, out var g, out var r, out var a);
                var n = rng.NextGaussian() * amp;
                var nb = n + rng.NextGaussian() * amp * sat;
                var ng = n + rng.NextGaussian() * amp * sat;
                var nr = n + rng.NextGaussian() * amp * sat;
                ctx.Dst[y * ctx.Width + x] = Premul(Clamp255(b + nb), Clamp255(g + ng), Clamp255(r + nr), a);
            }
        });
    }
}

/// <summary>每列滑動的四通道直方圖（中位數／百分位用）。</summary>
internal static class LocalHistogram
{
    public delegate uint PixelOp(Span<int> hb, Span<int> hg, Span<int> hr, Span<int> ha, int count, uint center);

    /// <summary>對每個像素以 (2r+1)² 視窗的直方圖呼叫 op（列內滑動，O(r) 每像素）。</summary>
    public static void Run(EffectContext ctx, int radius, PixelOp op)
    {
        var r = Math.Max(1, radius);
        var count = (2 * r + 1) * (2 * r + 1);
        ctx.ForRows(y =>
        {
            Span<int> hb = stackalloc int[256];
            Span<int> hg = stackalloc int[256];
            Span<int> hr = stackalloc int[256];
            Span<int> ha = stackalloc int[256];
            hb.Clear(); hg.Clear(); hr.Clear(); ha.Clear();

            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
                Add(ctx.SrcAt(dx, y + dy), hb, hg, hr, ha, 1);

            for (var x = 0; x < ctx.Width; x++)
            {
                ctx.Dst[y * ctx.Width + x] = op(hb, hg, hr, ha, count, ctx.SrcAt(x, y));
                if (x + 1 >= ctx.Width) break;
                for (var dy = -r; dy <= r; dy++)
                {
                    Add(ctx.SrcAt(x - r, y + dy), hb, hg, hr, ha, -1);
                    Add(ctx.SrcAt(x + r + 1, y + dy), hb, hg, hr, ha, 1);
                }
            }
        });
    }

    private static void Add(uint p, Span<int> hb, Span<int> hg, Span<int> hr, Span<int> ha, int delta)
    {
        hb[B(p)] += delta;
        hg[G(p)] += delta;
        hr[R(p)] += delta;
        ha[A(p)] += delta;
    }

    public static int Percentile(Span<int> hist, int count, int percent)
    {
        var target = Math.Clamp((int)((long)count * percent / 100), 0, Math.Max(0, count - 1));
        var acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc > target) return i;
        }
        return 255;
    }
}

/// <summary>中位數（可調百分位）。</summary>
public sealed record MedianEffect : IEffect
{
    public int Radius { get; init; } = 10;     // 1..200
    public int Percentile { get; init; } = 50; // 0..100

    public string Name => "中位數";
    public string Category => "雜訊";
    public int SourceMargin => Math.Min(Radius, 100) + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 200, o => ((MedianEffect)o).Radius,
            (o, v) => ((MedianEffect)o) with { Radius = (int)v }),
        new SliderParam("percentile", "百分位", 0, 100, o => ((MedianEffect)o).Percentile,
            (o, v) => ((MedianEffect)o) with { Percentile = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var pct = Math.Clamp(Percentile, 0, 100);
        LocalHistogram.Run(ctx, Math.Min(Radius, 100), (hb, hg, hr, ha, count, _) => Pack(
            LocalHistogram.Percentile(hb, count, pct),
            LocalHistogram.Percentile(hg, count, pct),
            LocalHistogram.Percentile(hr, count, pct),
            LocalHistogram.Percentile(ha, count, pct)));
    }
}

/// <summary>降低雜訊：往中位數靠攏，但差異大（邊緣）的地方保留。</summary>
public sealed record ReduceNoiseEffect : IEffect
{
    public int Radius { get; init; } = 6;       // 1..200
    public float Strength { get; init; } = 0.4f; // 0..1

    public string Name => "降低雜訊";
    public string Category => "雜訊";
    public int SourceMargin => Math.Min(Radius, 100) + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 200, o => ((ReduceNoiseEffect)o).Radius,
            (o, v) => ((ReduceNoiseEffect)o) with { Radius = (int)v }),
        new SliderParam("strength", "強度", 0, 1, o => ((ReduceNoiseEffect)o).Strength,
            (o, v) => ((ReduceNoiseEffect)o) with { Strength = (float)v }, "", 2),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var strength = Math.Clamp(Strength, 0f, 1f);
        LocalHistogram.Run(ctx, Math.Min(Radius, 100), (hb, hg, hr, ha, count, center) =>
        {
            var mb = LocalHistogram.Percentile(hb, count, 50);
            var mg = LocalHistogram.Percentile(hg, count, 50);
            var mr = LocalHistogram.Percentile(hr, count, 50);
            var ma = LocalHistogram.Percentile(ha, count, 50);
            var diff = (Math.Abs(mb - B(center)) + Math.Abs(mg - G(center)) + Math.Abs(mr - R(center))) / 3f;
            var w = strength * Math.Clamp(1f - diff / 48f, 0f, 1f);
            return Lerp(center, Pack(mb, mg, mr, ma), w);
        });
    }
}
