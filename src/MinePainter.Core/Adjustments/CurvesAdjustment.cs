using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// 曲線：亮度模式（一條曲線套 RGB 三通道）或 RGB 模式（三條各自的曲線）。
/// 控制點為 0..1 正規化座標，以單調三次樣條（Fritsch–Carlson）插值成 256 項 LUT，不會過衝。
/// </summary>
public sealed record CurvesAdjustment : IAdjustment
{
    public const int ModeLuminosity = 0;
    public const int ModeRgb = 1;

    public static readonly IReadOnlyList<(float X, float Y)> Identity = [(0f, 0f), (1f, 1f)];

    public int Mode { get; init; } = ModeLuminosity;

    /// <summary>亮度模式 1 條、RGB 模式 3 條（R, G, B）。</summary>
    public IReadOnlyList<IReadOnlyList<(float X, float Y)>> Curves { get; init; } = [Identity];

    public string DisplayName => "曲線";
    public string TypeId => "curves";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new ChoiceParam("mode", "模式", ["亮度", "RGB"],
            a => ((CurvesAdjustment)a).Mode,
            (a, v) => ((CurvesAdjustment)a).WithMode(v)),
        new CurvesParam("curves", "曲線", ["亮度"],
            a => ((CurvesAdjustment)a).Curves,
            (a, v) => ((CurvesAdjustment)a) with { Curves = v }),
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
        return new CurvesAdjustment { Mode = mode, Curves = curves };
    }

    /// <summary>控制點 → 256 項 LUT（單調三次樣條，端點外延平）。</summary>
    public static byte[] BuildTable(IReadOnlyList<(float X, float Y)> points)
    {
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

    public SKColorFilter CreateColorFilter()
    {
        if (Mode == ModeRgb && Curves.Count >= 3)
        {
            return SKColorFilter.CreateTable(null,
                BuildTable(Curves[0]), BuildTable(Curves[1]), BuildTable(Curves[2]));
        }
        var table = BuildTable(Curves[0]);
        return SKColorFilter.CreateTable(null, table, table, table);
    }
}
