using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// 曲線：亮度模式（一條曲線套 RGB 三通道）或 RGB 模式（三條各自的曲線）。
/// 控制點為 0..1 正規化座標，預設以單調三次樣條（Fritsch–Carlson）插值成 256 項 LUT，
/// 亦可選用自然三次樣條以保留 Photoshop 曲線的插值方式。
/// </summary>
public sealed record CurvesAdjustment : IAdjustment
{
    public const int ModeLuminosity = 0;
    public const int ModeRgb = 1;

    public static readonly IReadOnlyList<(float X, float Y)> Identity = [(0f, 0f), (1f, 1f)];

    public int Mode { get; init; } = ModeLuminosity;

    /// <summary>自然三次樣條（Photoshop 曲線）；預設保留既有的單調插值。</summary>
    public bool UseNaturalSpline { get; init; }

    /// <summary>亮度模式 1 條、RGB 模式 3 條（R, G, B）。</summary>
    public IReadOnlyList<IReadOnlyList<(float X, float Y)>> Curves { get; init; } = [Identity];

    public IReadOnlyList<(float X, float Y)> MasterCurve { get; init; } = Identity;

    public string DisplayName => "曲線";
    public string TypeId => "curves";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new ChoiceParam("mode", "模式", ["亮度", "RGB"],
            a => ((CurvesAdjustment)a).Mode,
            (a, v) => ((CurvesAdjustment)a).WithMode(v)),
        new BoolParam("naturalSpline", "自然三次樣條",
            a => ((CurvesAdjustment)a).UseNaturalSpline,
            (a, v) => ((CurvesAdjustment)a) with { UseNaturalSpline = v }),
        new CurvesParam("curves", "曲線", ["亮度"],
            a => ((CurvesAdjustment)a).Curves,
            (a, v) => ((CurvesAdjustment)a) with { Curves = v }),
        new CurvesParam("masterCurve", "RGB 總曲線", ["RGB"],
            a => [((CurvesAdjustment)a).MasterCurve],
            (a, v) => ((CurvesAdjustment)a) with { MasterCurve = v[0] }),
    ];

    public string[] ChannelNames => Mode == ModeRgb ? ["紅", "綠", "藍"] : ["亮度"];

    public CurvesAdjustment WithMode(int mode)
    {
        mode = mode == ModeRgb ? ModeRgb : ModeLuminosity;
        if (mode == Mode) return this;
        var count = mode == ModeRgb ? 3 : 1;
        var curves = new List<IReadOnlyList<(float, float)>>();
        for (var i = 0; i < count; i++)
            curves.Add(i < Curves.Count ? Curves[i] : Curves[0]);
        return this with { Mode = mode, Curves = curves };
    }

    public Dictionary<string, float> SaveParams()
    {
        var p = new Dictionary<string, float> { ["mode"] = Mode, ["channels"] = Curves.Count };
        p["naturalSpline"] = UseNaturalSpline ? 1 : 0;
        p["masterCount"] = MasterCurve.Count;
        for (var i = 0; i < MasterCurve.Count; i++)
        {
            p[$"masterX{i}"] = MasterCurve[i].X;
            p[$"masterY{i}"] = MasterCurve[i].Y;
        }
        for (var c = 0; c < Curves.Count; c++)
        {
            var pts = Curves[c];
            p[$"c{c}n"] = pts.Count;
            for (var i = 0; i < pts.Count; i++)
            {
                p[$"c{c}x{i}"] = pts[i].X;
                p[$"c{c}y{i}"] = pts[i].Y;
            }
        }
        return p;
    }

    public static CurvesAdjustment Load(IReadOnlyDictionary<string, float> p)
    {
        var mode = (int)p.GetValueOrDefault("mode", ModeLuminosity);
        var channels = Math.Clamp((int)p.GetValueOrDefault("channels", 1), 1, 3);
        var curves = new List<IReadOnlyList<(float, float)>>();
        for (var c = 0; c < channels; c++)
        {
            var n = (int)p.GetValueOrDefault($"c{c}n", 0);
            if (n < 2)
            {
                curves.Add(Identity);
                continue;
            }
            var pts = new List<(float, float)>(n);
            for (var i = 0; i < n; i++)
                pts.Add((p.GetValueOrDefault($"c{c}x{i}"), p.GetValueOrDefault($"c{c}y{i}")));
            curves.Add(pts);
        }
        var masterCount = Math.Clamp((int)p.GetValueOrDefault("masterCount", 0), 0, 19);
        var master = Enumerable.Range(0, masterCount)
            .Select(i => (p.GetValueOrDefault($"masterX{i}"), p.GetValueOrDefault($"masterY{i}"))).ToList();
        return new CurvesAdjustment
        {
            Mode = mode, Curves = curves, MasterCurve = master.Count >= 2 ? master : Identity,
            UseNaturalSpline = p.GetValueOrDefault("naturalSpline", 0) != 0,
        };
    }

    /// <summary>控制點 → 256 項 LUT（預設單調三次樣條；可選自然樣條；端點外延平）。</summary>
    public static byte[] BuildTable(IReadOnlyList<(float X, float Y)> points, bool useNaturalSpline = false)
    {
        if (useNaturalSpline) return BuildNaturalTable(points);
        var pts = points.OrderBy(p => p.X).ToList();
        if (pts.Count == 0) pts = [(0f, 0f), (1f, 1f)];
        if (pts.Count == 1) pts.Add((pts[0].X >= 1f ? 0f : 1f, pts[0].Y));

        var n = pts.Count;
        var xs = new float[n];
        var ys = new float[n];
        for (var i = 0; i < n; i++)
        {
            xs[i] = Math.Clamp(pts[i].X, 0f, 1f);
            ys[i] = Math.Clamp(pts[i].Y, 0f, 1f);
        }

        // 斜率（Fritsch–Carlson）
        var d = new float[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var dx = Math.Max(xs[i + 1] - xs[i], 1e-4f);
            d[i] = (ys[i + 1] - ys[i]) / dx;
        }
        var m = new float[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++)
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var tau = 3f / MathF.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }

        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255f;
            float y;
            if (x <= xs[0]) y = ys[0];
            else if (x >= xs[n - 1]) y = ys[n - 1];
            else
            {
                var k = 0;
                while (k < n - 2 && x > xs[k + 1]) k++;
                var h = Math.Max(xs[k + 1] - xs[k], 1e-4f);
                var t = (x - xs[k]) / h;
                var t2 = t * t;
                var t3 = t2 * t;
                var h00 = 2 * t3 - 3 * t2 + 1;
                var h10 = t3 - 2 * t2 + t;
                var h01 = -2 * t3 + 3 * t2;
                var h11 = t3 - t2;
                y = h00 * ys[k] + h10 * h * m[k] + h01 * ys[k + 1] + h11 * h * m[k + 1];
            }
            table[i] = (byte)Math.Clamp(MathF.Round(y * 255f), 0, 255);
        }
        return table;
    }

    private static byte[] BuildNaturalTable(IReadOnlyList<(float X, float Y)> points)
    {
        // 同一輸入值以最後的控制點為準；先正規化再去重，避免零長區間。
        var pts = points.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y))
            .Select(p => (X: (double)Math.Clamp(p.X, 0f, 1f), Y: (double)Math.Clamp(p.Y, 0f, 1f)))
            .GroupBy(p => p.X).Select(g => g.Last()).OrderBy(p => p.X).ToArray();
        if (pts.Length == 0) return BuildTable(Identity);
        if (pts.Length == 1) return Enumerable.Repeat((byte)Math.Round(pts[0].Y * 255), 256).ToArray();

        var n = pts.Length;
        // 解三對角系統取得各節點二階導數；自然邊界 s[0] = s[n-1] = 0。
        var diagonal = new double[n];
        var rhs = new double[n];
        var second = new double[n];
        for (var i = 1; i < n - 1; i++)
        {
            var left = pts[i].X - pts[i - 1].X;
            var right = pts[i + 1].X - pts[i].X;
            diagonal[i] = 2 * (left + right);
            rhs[i] = 6 * ((pts[i + 1].Y - pts[i].Y) / right - (pts[i].Y - pts[i - 1].Y) / left);
            if (i > 1)
            {
                var factor = left / diagonal[i - 1];
                diagonal[i] -= factor * left;
                rhs[i] -= factor * rhs[i - 1];
            }
        }
        for (var i = n - 2; i > 0; i--)
            second[i] = (rhs[i] - (pts[i + 1].X - pts[i].X) * second[i + 1]) / diagonal[i];

        var table = new byte[256];
        var k = 0;
        for (var i = 0; i < table.Length; i++)
        {
            var x = i / 255.0;
            double y;
            if (x <= pts[0].X) y = pts[0].Y;
            else if (x >= pts[^1].X) y = pts[^1].Y;
            else
            {
                while (k < n - 2 && x > pts[k + 1].X) k++;
                var h = pts[k + 1].X - pts[k].X;
                var a = (pts[k + 1].X - x) / h;
                var b = (x - pts[k].X) / h;
                y = a * pts[k].Y + b * pts[k + 1].Y
                    + ((a * a * a - a) * second[k] + (b * b * b - b) * second[k + 1]) * h * h / 6;
            }
            table[i] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
        }
        return table;
    }

    public SKColorFilter CreateColorFilter()
    {
        var master = BuildTable(MasterCurve, UseNaturalSpline);
        byte[] Table(IReadOnlyList<(float X, float Y)> points)
        {
            var channel = BuildTable(points, UseNaturalSpline);
            return channel.Select(value => master[value]).ToArray();
        }
        if (Mode == ModeRgb && Curves.Count >= 3)
        {
            return SKColorFilter.CreateTable(null,
                Table(Curves[0]), Table(Curves[1]), Table(Curves[2]));
        }
        var table = Table(Curves[0]);
        return SKColorFilter.CreateTable(null, table, table, table);
    }
}
