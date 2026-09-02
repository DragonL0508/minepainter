using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>圓盤（disc）平均：逐帶列前綴和，每像素 O(r)。</summary>
internal static class DiscBlur
{
    public static uint[] Run(uint[] src, int w, int h, int r, CancellationToken ct)
    {
        if (r <= 0) return (uint[])src.Clone();
        var dst = new uint[w * h];
        var half = new int[2 * r + 1];
        for (var dy = -r; dy <= r; dy++)
            half[dy + r] = (int)Math.Floor(Math.Sqrt((double)r * r - dy * dy));

        const int bandH = 64;
        var stride = w + 1;
        for (var y0 = 0; y0 < h; y0 += bandH)
        {
            ct.ThrowIfCancellationRequested();
            var y1 = Math.Min(h, y0 + bandH);
            var rowsFrom = y0 - r;
            var rowsTo = y1 + r;
            var rows = rowsTo - rowsFrom;
            var pb = new int[rows * stride];
            var pg = new int[rows * stride];
            var pr = new int[rows * stride];
            var pa = new int[rows * stride];

            Parallel.For(0, rows, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var sy = Math.Clamp(rowsFrom + i, 0, h - 1);
                var srcRow = sy * w;
                var o = i * stride;
                int sb = 0, sg = 0, sr = 0, sa = 0;
                pb[o] = pg[o] = pr[o] = pa[o] = 0;
                for (var x = 0; x < w; x++)
                {
                    var p = src[srcRow + x];
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                    pb[o + x + 1] = sb; pg[o + x + 1] = sg; pr[o + x + 1] = sr; pa[o + x + 1] = sa;
                }
            });

            Parallel.For(y0, y1, new ParallelOptions { CancellationToken = ct }, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    var n = 0;
                    for (var dy = -r; dy <= r; dy++)
                    {
                        var hw = half[dy + r];
                        var x1 = Math.Max(0, x - hw);
                        var x2 = Math.Min(w - 1, x + hw);
                        var o = (y + dy - rowsFrom) * stride;
                        sb += pb[o + x2 + 1] - pb[o + x1];
                        sg += pg[o + x2 + 1] - pg[o + x1];
                        sr += pr[o + x2 + 1] - pr[o + x1];
                        sa += pa[o + x2 + 1] - pa[o + x1];
                        n += x2 - x1 + 1;
                    }
                    dst[y * w + x] = Pack((int)((sb + n / 2) / n), (int)((sg + n / 2) / n),
                        (int)((sr + n / 2) / n), (int)((sa + n / 2) / n));
                }
            });
        }
        return dst;
    }
}

/// <summary>高斯模糊（三次盒狀近似）。</summary>
public sealed record GaussianBlurEffect : IEffect
{
    public int Radius { get; init; } = 2; // 0..200

    public string Name => "高斯模糊";
    public string Category => "模糊";
    public int SourceMargin => GaussianMargin(Radius);

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 0, 200, o => ((GaussianBlurEffect)o).Radius,
            (o, v) => ((GaussianBlurEffect)o) with { Radius = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var blurred = GaussianBlur(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, Radius, ctx.Cancellation);
        ctx.CropToDst(blurred);
    }
}

/// <summary>失焦：圓盤平均（比高斯更「平」的模糊）。</summary>
public sealed record UnfocusEffect : IEffect
{
    public int Radius { get; init; } = 4; // 1..200

    public string Name => "失焦";
    public string Category => "模糊";
    public int SourceMargin => Radius + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 200, o => ((UnfocusEffect)o).Radius,
            (o, v) => ((UnfocusEffect)o) with { Radius = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var blurred = DiscBlur.Run(ctx.Src, ctx.SrcWidth, ctx.SrcHeight, Radius, ctx.Cancellation);
        ctx.CropToDst(blurred);
    }
}

/// <summary>散景：亮部先以 gamma 抬高再做圓盤平均，高光會膨成光斑。</summary>
public sealed record BokehEffect : IEffect
{
    public int Radius { get; init; } = 8;   // 1..200
    public float Gamma { get; init; } = 3f; // 1..10（高光強度）

    public string Name => "散景";
    public string Category => "模糊";
    public int SourceMargin => Radius + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 200, o => ((BokehEffect)o).Radius,
            (o, v) => ((BokehEffect)o) with { Radius = (int)v }),
        new SliderParam("gamma", "高光", 1, 10, o => ((BokehEffect)o).Gamma,
            (o, v) => ((BokehEffect)o) with { Gamma = (float)v }, "", 1),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var gamma = Math.Clamp(Gamma, 1f, 10f);
        var lutIn = new byte[256];
        var lutOut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            lutIn[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, gamma));
            lutOut[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, 1 / gamma));
        }

        var boosted = new uint[ctx.Src.Length];
        for (var i = 0; i < boosted.Length; i++)
        {
            var p = ctx.Src[i];
            Unpremul(p, out var b, out var g, out var r, out var a);
            boosted[i] = Premul(lutIn[b], lutIn[g], lutIn[r], a);
        }
        var blurred = DiscBlur.Run(boosted, ctx.SrcWidth, ctx.SrcHeight, Radius, ctx.Cancellation);
        for (var i = 0; i < blurred.Length; i++)
        {
            Unpremul(blurred[i], out var b, out var g, out var r, out var a);
            blurred[i] = Premul(lutOut[b], lutOut[g], lutOut[r], a);
        }
        ctx.CropToDst(blurred);
    }
}

/// <summary>動態模糊：沿指定方向的線段取樣平均。</summary>
public sealed record MotionBlurEffect : IEffect
{
    public float Angle { get; init; } = 25f;   // 0..360
    public int Distance { get; init; } = 10;   // 1..200
    public bool Centered { get; init; } = true;

    public string Name => "動態模糊";
    public string Category => "模糊";
    public int SourceMargin => Distance + 1;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", 0, 360, o => ((MotionBlurEffect)o).Angle,
            (o, v) => ((MotionBlurEffect)o) with { Angle = (float)v }),
        new SliderParam("distance", "距離", 1, 200, o => ((MotionBlurEffect)o).Distance,
            (o, v) => ((MotionBlurEffect)o) with { Distance = (int)v }),
        new BoolParam("centered", "置中", o => ((MotionBlurEffect)o).Centered,
            (o, v) => ((MotionBlurEffect)o) with { Centered = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var rad = Angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad);
        var dy = -MathF.Sin(rad);
        var n = Math.Max(2, Distance);
        var start = Centered ? -(Distance - 1) / 2f : 0f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = start + i * (Distance - 1f) / (n - 1);
                    var p = ctx.SrcBilinearClamp(x + 0.5f + dx * t, y + 0.5f + dy * t);
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                }
                ctx.Dst[y * ctx.Width + x] = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
            }
        });
    }
}

/// <summary>放射狀模糊：繞中心沿圓弧取樣。</summary>
public sealed record RadialBlurEffect : IEffect
{
    public float Angle { get; init; } = 2f;    // 0..360
    public float CenterX { get; init; } = 0f;  // -1..1
    public float CenterY { get; init; } = 0f;
    public int Quality { get; init; } = 2;     // 1..5

    public string Name => "放射狀模糊";
    public string Category => "模糊";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", 0, 360, o => ((RadialBlurEffect)o).Angle,
            (o, v) => ((RadialBlurEffect)o) with { Angle = (float)v }),
        new PointParam("center", "中心", o => (((RadialBlurEffect)o).CenterX, ((RadialBlurEffect)o).CenterY),
            (o, v) => ((RadialBlurEffect)o) with { CenterX = v.X, CenterY = v.Y }),
        new SliderParam("quality", "品質", 1, 5, o => ((RadialBlurEffect)o).Quality,
            (o, v) => ((RadialBlurEffect)o) with { Quality = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var half = Angle * MathF.PI / 360f;
        var quality = Math.Clamp(Quality, 1, 5);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                var d = MathF.Sqrt(px * px + py * py);
                var arc = d * half * 2;
                var n = Math.Clamp((int)(arc * quality * 0.5f) + 1, 1, 64 * quality);
                if (n <= 1 || half <= 0)
                {
                    ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(x, y);
                    continue;
                }
                var baseAngle = MathF.Atan2(py, px);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (var i = 0; i < n; i++)
                {
                    var a = baseAngle - half + (2 * half) * i / (n - 1);
                    var p = ctx.SrcBilinearClamp(cx + MathF.Cos(a) * d, cy + MathF.Sin(a) * d);
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                }
                ctx.Dst[y * ctx.Width + x] = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
            }
        });
    }
}

/// <summary>縮放模糊：沿著往中心的射線取樣。</summary>
public sealed record ZoomBlurEffect : IEffect
{
    public int Amount { get; init; } = 10;    // 0..100
    public float CenterX { get; init; } = 0f;
    public float CenterY { get; init; } = 0f;

    public string Name => "縮放模糊";
    public string Category => "模糊";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", 0, 100, o => ((ZoomBlurEffect)o).Amount,
            (o, v) => ((ZoomBlurEffect)o) with { Amount = (int)v }),
        new PointParam("center", "中心", o => (((ZoomBlurEffect)o).CenterX, ((ZoomBlurEffect)o).CenterY),
            (o, v) => ((ZoomBlurEffect)o) with { CenterX = v.X, CenterY = v.Y }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var amount = Amount / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                var d = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                var n = Math.Clamp((int)(d * amount * 0.5f) + 1, 1, 64);
                if (n <= 1)
                {
                    ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(x, y);
                    continue;
                }
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = amount * i / (n - 1);
                    var p = ctx.SrcBilinearClamp(px + (cx - px) * t, py + (cy - py) * t);
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                }
                ctx.Dst[y * ctx.Width + x] = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
            }
        });
    }
}

/// <summary>表面模糊：只平均色差在門檻內的鄰居（雙邊濾波），邊緣保留。</summary>
public sealed record SurfaceBlurEffect : IEffect
{
    public int Radius { get; init; } = 6;      // 1..100
    public int Threshold { get; init; } = 15;  // 1..100

    public string Name => "表面模糊";
    public string Category => "模糊";
    public int SourceMargin => Radius + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("radius", "半徑", 1, 100, o => ((SurfaceBlurEffect)o).Radius,
            (o, v) => ((SurfaceBlurEffect)o) with { Radius = (int)v }),
        new SliderParam("threshold", "門檻", 1, 100, o => ((SurfaceBlurEffect)o).Threshold,
            (o, v) => ((SurfaceBlurEffect)o) with { Threshold = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var r = Math.Clamp(Radius, 1, 100);
        var step = Math.Max(1, (int)Math.Ceiling(r / 12.0)); // 大半徑時抽樣，保持可互動
        var thr = Threshold * 2.55f;
        var r2 = r * r;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var c = ctx.SrcAt(x, y);
                Unpremul(c, out var cb, out var cg, out var cr, out var ca);
                float sb = 0, sg = 0, sr = 0, sa = 0, wsum = 0;
                for (var dy = -r; dy <= r; dy += step)
                for (var dx = -r; dx <= r; dx += step)
                {
                    if (dx * dx + dy * dy > r2) continue;
                    var p = ctx.SrcAt(x + dx, y + dy);
                    Unpremul(p, out var b, out var g, out var rr, out var a);
                    var diff = (Math.Abs(b - cb) + Math.Abs(g - cg) + Math.Abs(rr - cr)) / 3f;
                    var w = 1f - diff / thr;
                    if (w <= 0) continue;
                    sb += b * w; sg += g * w; sr += rr * w; sa += a * w; wsum += w;
                }
                ctx.Dst[y * ctx.Width + x] = wsum <= 0
                    ? c
                    : Premul((int)(sb / wsum + 0.5f), (int)(sg / wsum + 0.5f), (int)(sr / wsum + 0.5f), (int)(sa / wsum + 0.5f));
            }
        });
    }
}

/// <summary>碎片：把影像複製 N 份沿圓周錯開後平均。</summary>
public sealed record FragmentEffect : IEffect
{
    public int Fragments { get; init; } = 4;  // 2..50
    public int Distance { get; init; } = 8;   // 0..100
    public float Rotation { get; init; } = 0f;

    public string Name => "碎片";
    public string Category => "模糊";
    public int SourceMargin => Distance + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("fragments", "碎片數", 2, 50, o => ((FragmentEffect)o).Fragments,
            (o, v) => ((FragmentEffect)o) with { Fragments = (int)v }),
        new SliderParam("distance", "距離", 0, 100, o => ((FragmentEffect)o).Distance,
            (o, v) => ((FragmentEffect)o) with { Distance = (int)v }),
        new AngleParam("rotation", "旋轉", 0, 360, o => ((FragmentEffect)o).Rotation,
            (o, v) => ((FragmentEffect)o) with { Rotation = (float)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var n = Math.Clamp(Fragments, 2, 50);
        var offsets = new (float X, float Y)[n];
        for (var i = 0; i < n; i++)
        {
            var a = Rotation * MathF.PI / 180f + MathF.Tau * i / n;
            offsets[i] = (MathF.Cos(a) * Distance, MathF.Sin(a) * Distance);
        }
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                long sb = 0, sg = 0, sr = 0, sa = 0;
                foreach (var (ox, oy) in offsets)
                {
                    var p = ctx.SrcBilinearClamp(x + 0.5f + ox, y + 0.5f + oy);
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                }
                ctx.Dst[y * ctx.Width + x] = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
            }
        });
    }
}
