using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Effects;

/// <summary>邊界處理（極座標反轉等會取樣到範圍外的效果）。</summary>
internal static class EdgeSampling
{
    public static readonly string[] Options = ["夾住", "反射", "環繞", "透明"];

    /// <summary>依邊界模式把目標座標映射到可取樣範圍；回傳 false = 透明。</summary>
    public static bool Map(ref float x, ref float y, int w, int h, int mode)
    {
        switch (mode)
        {
            case 0: // clamp
                x = Math.Clamp(x, 0.5f, w - 0.5f);
                y = Math.Clamp(y, 0.5f, h - 0.5f);
                return true;
            case 1: // reflect
                x = Reflect(x, w);
                y = Reflect(y, h);
                return true;
            case 2: // wrap
                x = ((x % w) + w) % w;
                y = ((y % h) + h) % h;
                return true;
            default:
                return x >= 0 && y >= 0 && x < w && y < h;
        }
    }

    private static float Reflect(float v, int size)
    {
        if (size <= 0) return 0;
        var period = size * 2f;
        v = ((v % period) + period) % period;
        return v >= size ? period - v : v;
    }
}

/// <summary>凸起／凹陷：以中心為準的徑向縮放。</summary>
public sealed record BulgeEffect : IEffect
{
    public int Amount { get; init; } = 45; // -200..100
    public float CenterX { get; init; } = 0f;
    public float CenterY { get; init; } = 0f;

    public string Name => "凸起";
    public string Category => "扭曲";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", -200, 100, o => ((BulgeEffect)o).Amount,
            (o, v) => ((BulgeEffect)o) with { Amount = (int)v }),
        new PointParam("center", "中心", o => (((BulgeEffect)o).CenterX, ((BulgeEffect)o).CenterY),
            (o, v) => ((BulgeEffect)o) with { CenterX = v.X, CenterY = v.Y }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var maxR = Math.Min(ctx.Width, ctx.Height) / 2f;
        var amount = Amount / 100f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                var d = MathF.Sqrt(px * px + py * py);
                if (d >= maxR || d == 0)
                {
                    ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(x, y);
                    continue;
                }
                var r = d / maxR;
                var t = 1f - r * r;
                var scale = 1f - amount * t;
                if (scale < 0.01f) scale = 0.01f;
                ctx.Dst[y * ctx.Width + x] = ctx.SrcBilinearClamp(cx + px * scale, cy + py * scale);
            }
        });
    }
}

/// <summary>結晶化：抖動格點的 Voronoi 分區，每區取種子點的顏色。</summary>
public sealed record CrystalizeEffect : IEffect
{
    public int CellSize { get; init; } = 8; // 2..250
    public int Seed { get; init; } = 0;

    public string Name => "結晶化";
    public bool IsPositionIndependent => false;
    public string Category => "扭曲";
    public int SourceMargin => CellSize * 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("cell", "晶格大小", 2, 250, o => ((CrystalizeEffect)o).CellSize,
            (o, v) => ((CrystalizeEffect)o) with { CellSize = (int)v }),
        new SliderParam("seed", "種子", 0, 255, o => ((CrystalizeEffect)o).Seed,
            (o, v) => ((CrystalizeEffect)o) with { Seed = (int)v }) { IsSeed = true },
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var cell = Math.Max(2, CellSize);
        var seed = (uint)(Seed * 7919 + 1);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var gx = (int)MathF.Floor((float)x / cell);
                var gy = (int)MathF.Floor((float)y / cell);
                var best = float.MaxValue;
                var bx = x;
                var by = y;
                for (var j = -1; j <= 1; j++)
                for (var i = -1; i <= 1; i++)
                {
                    var h = Hash(gx + i, gy + j, seed);
                    var sx = (gx + i) * cell + (h & 0xFFFF) * cell / 65536f;
                    var sy = (gy + j) * cell + ((h >> 16) & 0xFFFF) * cell / 65536f;
                    var dx = sx - x;
                    var dy = sy - y;
                    var d = dx * dx + dy * dy;
                    if (d < best)
                    {
                        best = d;
                        bx = (int)sx;
                        by = (int)sy;
                    }
                }
                ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(bx, by);
            }
        });
    }
}

/// <summary>凹痕：以分形雜訊當位移場折射。</summary>
public sealed record DentsEffect : IEffect
{
    public int Scale { get; init; } = 25;       // 1..200
    public int Refraction { get; init; } = 50;  // 0..200
    public int Roughness { get; init; } = 10;   // 0..100
    public int Tension { get; init; } = 10;     // 0..100
    public int Seed { get; init; } = 0;

    public string Name => "凹痕";
    public string Category => "扭曲";
    public int SourceMargin => Refraction / 2 + 2;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("scale", "比例", 1, 200, o => ((DentsEffect)o).Scale,
            (o, v) => ((DentsEffect)o) with { Scale = (int)v }),
        new SliderParam("refraction", "折射", 0, 200, o => ((DentsEffect)o).Refraction,
            (o, v) => ((DentsEffect)o) with { Refraction = (int)v }),
        new SliderParam("roughness", "粗糙度", 0, 100, o => ((DentsEffect)o).Roughness,
            (o, v) => ((DentsEffect)o) with { Roughness = (int)v }),
        new SliderParam("tension", "張力", 0, 100, o => ((DentsEffect)o).Tension,
            (o, v) => ((DentsEffect)o) with { Tension = (int)v }),
        new SliderParam("seed", "種子", 0, 255, o => ((DentsEffect)o).Seed,
            (o, v) => ((DentsEffect)o) with { Seed = (int)v }) { IsSeed = true },
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var scale = Math.Max(1, Scale);
        var amp = Refraction / 2f;
        var octaves = 1 + Roughness / 20;      // 1..6
        var persistence = 0.3f + Roughness / 250f;
        var power = 1f + Tension / 25f;        // 1..5
        var seed = (uint)(Seed * 104729 + 17);
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var nx = Fbm(x / (float)scale, y / (float)scale, octaves, persistence, seed);
                var ny = Fbm(x / (float)scale + 37.7f, y / (float)scale + 11.3f, octaves, persistence, seed + 1);
                nx = MathF.Sign(nx) * MathF.Pow(Math.Abs(nx), power);
                ny = MathF.Sign(ny) * MathF.Pow(Math.Abs(ny), power);
                ctx.Dst[y * ctx.Width + x] = ctx.SrcBilinearClamp(x + 0.5f + nx * amp, y + 0.5f + ny * amp);
            }
        });
    }
}

/// <summary>霧面玻璃：隨機散射取樣平均。</summary>
public sealed record FrostedGlassEffect : IEffect
{
    public int Amount { get; init; } = 3;      // 1..200 散射半徑
    public int Smoothness { get; init; } = 5;  // 1..20 取樣數
    public int Seed { get; init; } = 0;

    public string Name => "霧面玻璃";
    public string Category => "扭曲";
    public int SourceMargin => Amount + 1;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", 1, 200, o => ((FrostedGlassEffect)o).Amount,
            (o, v) => ((FrostedGlassEffect)o) with { Amount = (int)v }),
        new SliderParam("smooth", "平滑度", 1, 20, o => ((FrostedGlassEffect)o).Smoothness,
            (o, v) => ((FrostedGlassEffect)o) with { Smoothness = (int)v }),
        new SliderParam("seed", "種子", 0, 255, o => ((FrostedGlassEffect)o).Seed,
            (o, v) => ((FrostedGlassEffect)o) with { Seed = (int)v }) { IsSeed = true },
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var n = Math.Clamp(Smoothness, 1, 20);
        ctx.ForRows(y =>
        {
            var rng = new XorShift((uint)(y * 2654435761u + (uint)Seed * 97u + 1));
            for (var x = 0; x < ctx.Width; x++)
            {
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (var i = 0; i < n; i++)
                {
                    var a = rng.NextFloat() * MathF.Tau;
                    var d = MathF.Sqrt(rng.NextFloat()) * Amount;
                    var p = ctx.SrcAt(x + (int)MathF.Round(MathF.Cos(a) * d), y + (int)MathF.Round(MathF.Sin(a) * d));
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                }
                ctx.Dst[y * ctx.Width + x] = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
            }
        });
    }
}

/// <summary>像素化：每格取平均色。</summary>
public sealed record PixelateEffect : IEffect
{
    public int CellSize { get; init; } = 2; // 1..100

    public string Name => "像素化";
    public bool IsPositionIndependent => false;
    public string Category => "扭曲";
    public int SourceMargin => 0;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("cell", "格子大小", 1, 100, o => ((PixelateEffect)o).CellSize,
            (o, v) => ((PixelateEffect)o) with { CellSize = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var cell = Math.Max(1, CellSize);
        if (cell == 1)
        {
            ctx.CopySrcToDst();
            return;
        }
        var cellsX = (ctx.Width + cell - 1) / cell;
        var cellsY = (ctx.Height + cell - 1) / cell;
        var options = new ParallelOptions { CancellationToken = ctx.Cancellation };
        Parallel.For(0, cellsY, options, cy =>
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                var x0 = cx * cell;
                var y0 = cy * cell;
                var x1 = Math.Min(ctx.Width, x0 + cell);
                var y1 = Math.Min(ctx.Height, y0 + cell);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                var n = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var p = ctx.SrcAt(x, y);
                    sb += B(p); sg += G(p); sr += R(p); sa += A(p);
                    n++;
                }
                var avg = Pack((int)(sb / n), (int)(sg / n), (int)(sr / n), (int)(sa / n));
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    ctx.Dst[y * ctx.Width + x] = avg;
            }
        });
    }
}

/// <summary>極座標反轉：p' = c + (p − c)·R²·amount / |p − c|²。</summary>
public sealed record PolarInversionEffect : IEffect
{
    public float Amount { get; init; } = 1f;  // 0.1..4
    public float CenterX { get; init; } = 0f;
    public float CenterY { get; init; } = 0f;
    public int Edge { get; init; } = 1;

    public string Name => "極座標反轉";
    public string Category => "扭曲";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", 0.1, 4, o => ((PolarInversionEffect)o).Amount,
            (o, v) => ((PolarInversionEffect)o) with { Amount = (float)v }, "", 2),
        new PointParam("center", "中心", o => (((PolarInversionEffect)o).CenterX, ((PolarInversionEffect)o).CenterY),
            (o, v) => ((PolarInversionEffect)o) with { CenterX = v.X, CenterY = v.Y }),
        new ChoiceParam("edge", "邊界", EdgeSampling.Options, o => ((PolarInversionEffect)o).Edge,
            (o, v) => ((PolarInversionEffect)o) with { Edge = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var maxR = Math.Min(ctx.Width, ctx.Height) / 2f;
        var k = maxR * maxR * Amount;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                var d2 = px * px + py * py;
                if (d2 < 1e-3f)
                {
                    ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(x, y);
                    continue;
                }
                var sx = cx + px * k / d2;
                var sy = cy + py * k / d2;
                ctx.Dst[y * ctx.Width + x] = EdgeSampling.Map(ref sx, ref sy, ctx.Width, ctx.Height, Edge)
                    ? ctx.SrcBilinearClamp(sx, sy)
                    : 0u;
            }
        });
    }
}

/// <summary>拼貼反射：旋轉座標後在每個方格內以 tan 曲率折射。</summary>
public sealed record TileReflectionEffect : IEffect
{
    public float Angle { get; init; } = 30f;    // -180..180
    public int TileSize { get; init; } = 40;    // 2..800
    public int Curvature { get; init; } = 8;    // -100..100

    public string Name => "拼貼反射";
    public string Category => "扭曲";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new AngleParam("angle", "角度", -180, 180, o => ((TileReflectionEffect)o).Angle,
            (o, v) => ((TileReflectionEffect)o) with { Angle = (float)v }),
        new SliderParam("tile", "方格大小", 2, 800, o => ((TileReflectionEffect)o).TileSize,
            (o, v) => ((TileReflectionEffect)o) with { TileSize = (int)v }),
        new SliderParam("curvature", "曲率", -100, 100, o => ((TileReflectionEffect)o).Curvature,
            (o, v) => ((TileReflectionEffect)o) with { Curvature = (int)v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var rad = Angle * MathF.PI / 180f;
        var sin = MathF.Sin(rad);
        var cos = MathF.Cos(rad);
        var scale = MathF.PI / Math.Max(2, TileSize);
        var intensity = Curvature * Curvature / 10f * MathF.Sign(Curvature) / 100f * 8f;
        var (cx, cy) = ctx.Center();
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var i = x + 0.5f - cx;
                var j = y + 0.5f - cy;
                var s = cos * i + sin * j;
                var t = -sin * i + cos * j;
                s += intensity * MathF.Tan(s * scale);
                t += intensity * MathF.Tan(t * scale);
                var u = cos * s - sin * t;
                var v = sin * s + cos * t;
                ctx.Dst[y * ctx.Width + x] = ctx.SrcBilinearClamp(cx + u, cy + v);
            }
        });
    }
}

/// <summary>扭轉：離中心越近轉得越多。</summary>
public sealed record TwistEffect : IEffect
{
    public int Amount { get; init; } = 45;     // -200..200（度）
    public float Size { get; init; } = 1f;     // 0.1..2（半徑倍率）
    public float CenterX { get; init; } = 0f;
    public float CenterY { get; init; } = 0f;

    public string Name => "扭轉";
    public string Category => "扭曲";
    public int SourceMargin => EffectContext.WholeLayer;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("amount", "數量", -200, 200, o => ((TwistEffect)o).Amount,
            (o, v) => ((TwistEffect)o) with { Amount = (int)v }),
        new SliderParam("size", "大小", 0.1, 2, o => ((TwistEffect)o).Size,
            (o, v) => ((TwistEffect)o) with { Size = (float)v }, "", 2),
        new PointParam("center", "中心", o => (((TwistEffect)o).CenterX, ((TwistEffect)o).CenterY),
            (o, v) => ((TwistEffect)o) with { CenterX = v.X, CenterY = v.Y }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var (cx, cy) = ctx.Center(CenterX, CenterY);
        var maxR = Math.Min(ctx.Width, ctx.Height) / 2f * Math.Max(0.1f, Size);
        var amount = Amount * MathF.PI / 180f * 2f;
        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                var px = x + 0.5f - cx;
                var py = y + 0.5f - cy;
                var d = MathF.Sqrt(px * px + py * py);
                if (d >= maxR)
                {
                    ctx.Dst[y * ctx.Width + x] = ctx.SrcAt(x, y);
                    continue;
                }
                var t = 1f - d / maxR;
                var theta = amount * t * t;
                var cos = MathF.Cos(theta);
                var sin = MathF.Sin(theta);
                ctx.Dst[y * ctx.Width + x] = ctx.SrcBilinearClamp(cx + px * cos - py * sin, cy + px * sin + py * cos);
            }
        });
    }
}

/// <summary>
/// 傾斜（斜體）：把內容依角度切變。水平傾斜＝每往上一列就往右推一點（正值＝像斜體往右倒）。
/// 基準線（不動的那條線）可選中心／上緣／下緣 —— 文字要像斜體就用「下緣」（基線不動）。
/// </summary>
public sealed record SkewEffect : IEffect
{
    public float Horizontal { get; init; } = 15f;  // -80..80（度）
    public float Vertical { get; init; }           // -80..80（度）
    public int Pivot { get; init; }                // 0=中心 1=上緣 2=下緣

    public string Name => "傾斜";
    public string Category => "扭曲";
    public int SourceMargin => EffectContext.WholeLayer;

    /// <summary>切變量以「範圍內的基準線」為準，換了範圍結果就不同 —— 不能只重算髒區。</summary>
    public bool IsPositionIndependent => false;

    private static readonly ParamDef[] Params =
    [
        new SliderParam("horizontal", "水平傾斜", -80, 80, o => ((SkewEffect)o).Horizontal,
            (o, v) => ((SkewEffect)o) with { Horizontal = (float)v }, "°", 1),
        new SliderParam("vertical", "垂直傾斜", -80, 80, o => ((SkewEffect)o).Vertical,
            (o, v) => ((SkewEffect)o) with { Vertical = (float)v }, "°", 1),
        new ChoiceParam("pivot", "基準", ["中心", "上緣", "下緣"],
            o => ((SkewEffect)o).Pivot, (o, v) => ((SkewEffect)o) with { Pivot = v }),
    ];
    public IReadOnlyList<ParamDef> Parameters => Params;

    public void Render(EffectContext ctx)
    {
        var shx = MathF.Tan(Math.Clamp(Horizontal, -80f, 80f) * MathF.PI / 180f);
        var shy = MathF.Tan(Math.Clamp(Vertical, -80f, 80f) * MathF.PI / 180f);
        if (shx == 0f && shy == 0f) { ctx.CopySrcToDst(); return; }

        var pivotY = Pivot switch { 1 => 0f, 2 => ctx.Height, _ => ctx.Height / 2f };
        var pivotX = ctx.Width / 2f;

        ctx.ForRows(y =>
        {
            for (var x = 0; x < ctx.Width; x++)
            {
                // 反向映射：目標像素往回找來源（正的水平角度＝上方往右移，所以來源要往左找）
                var px = x + 0.5f;
                var py = y + 0.5f;
                var sx = px + shx * (py - pivotY);
                var sy = py - shy * (px - pivotX);
                ctx.Dst[y * ctx.Width + x] = ctx.SrcBilinear(sx, sy);
            }
        });
    }
}
