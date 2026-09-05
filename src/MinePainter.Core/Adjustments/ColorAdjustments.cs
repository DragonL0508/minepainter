using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>臨界值：亮度高於門檻的變白、其餘變黑（Photoshop 的「臨界值」）。</summary>
public sealed record ThresholdAdjustment(int Level = 128) : IAdjustment
{
    public string DisplayName => "臨界值";
    public string TypeId => "threshold";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("level", "臨界值", 1, 255,
            a => ((ThresholdAdjustment)a).Level,
            (a, v) => ((ThresholdAdjustment)a) with { Level = (int)Math.Round(v) }) { Track = SliderTrack.Gray },
    ];

    public Dictionary<string, float> SaveParams() => new() { ["level"] = Level };

    public SKColorFilter CreateColorFilter()
    {
        var level = Math.Clamp(Level, 1, 255);
        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)(i >= level ? 255 : 0);
        // 先轉亮度（Rec.601 權重）再切門檻
        using var luma = SKColorFilter.CreateColorMatrix(
        [
            0.299f, 0.587f, 0.114f, 0, 0,
            0.299f, 0.587f, 0.114f, 0, 0,
            0.299f, 0.587f, 0.114f, 0, 0,
            0, 0, 0, 1, 0,
        ]);
        using var cut = SKColorFilter.CreateTable(null, table, table, table);
        return SKColorFilter.CreateCompose(cut, luma);
    }
}

/// <summary>
/// 色彩平衡（Photoshop 的「色彩平衡」）：陰影／中間調／亮部各自往青紅、洋紅綠、黃藍偏移（−100..100）。
/// 每個通道依自己的亮度落在哪一段加權：暗的吃陰影那組、中間吃中間調、亮的吃亮部，做成三張 LUT。
/// </summary>
public sealed record ColorBalanceAdjustment : IAdjustment
{
    public int ShadowsRed { get; init; }
    public int ShadowsGreen { get; init; }
    public int ShadowsBlue { get; init; }
    public int MidtonesRed { get; init; }
    public int MidtonesGreen { get; init; }
    public int MidtonesBlue { get; init; }
    public int HighlightsRed { get; init; }
    public int HighlightsGreen { get; init; }
    public int HighlightsBlue { get; init; }

    /// <summary>保留明度：偏色之後把亮度拉回原本（PS 的同名選項）。</summary>
    public bool PreserveLuminosity { get; init; } = true;

    public string DisplayName => "色彩平衡";
    public string TypeId => "colorBalance";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        Slider("sr", "陰影 青 ↔ 紅", a => a.ShadowsRed, (a, v) => a with { ShadowsRed = v }),
        Slider("sg", "陰影 洋紅 ↔ 綠", a => a.ShadowsGreen, (a, v) => a with { ShadowsGreen = v }),
        Slider("sb", "陰影 黃 ↔ 藍", a => a.ShadowsBlue, (a, v) => a with { ShadowsBlue = v }),
        Slider("mr", "中間調 青 ↔ 紅", a => a.MidtonesRed, (a, v) => a with { MidtonesRed = v }),
        Slider("mg", "中間調 洋紅 ↔ 綠", a => a.MidtonesGreen, (a, v) => a with { MidtonesGreen = v }),
        Slider("mb", "中間調 黃 ↔ 藍", a => a.MidtonesBlue, (a, v) => a with { MidtonesBlue = v }),
        Slider("hr", "亮部 青 ↔ 紅", a => a.HighlightsRed, (a, v) => a with { HighlightsRed = v }),
        Slider("hg", "亮部 洋紅 ↔ 綠", a => a.HighlightsGreen, (a, v) => a with { HighlightsGreen = v }),
        Slider("hb", "亮部 黃 ↔ 藍", a => a.HighlightsBlue, (a, v) => a with { HighlightsBlue = v }),
        new BoolParam("preserve", "保留明度", a => ((ColorBalanceAdjustment)a).PreserveLuminosity,
            (a, v) => ((ColorBalanceAdjustment)a) with { PreserveLuminosity = v }),
    ];

    private static SliderParam Slider(string key, string label, Func<ColorBalanceAdjustment, int> get,
        Func<ColorBalanceAdjustment, int, ColorBalanceAdjustment> with) =>
        new(key, label, -100, 100, a => get((ColorBalanceAdjustment)a),
            (a, v) => with((ColorBalanceAdjustment)a, (int)Math.Round(v)));

    public Dictionary<string, float> SaveParams() => new()
    {
        ["sr"] = ShadowsRed, ["sg"] = ShadowsGreen, ["sb"] = ShadowsBlue,
        ["mr"] = MidtonesRed, ["mg"] = MidtonesGreen, ["mb"] = MidtonesBlue,
        ["hr"] = HighlightsRed, ["hg"] = HighlightsGreen, ["hb"] = HighlightsBlue,
        ["preserve"] = PreserveLuminosity ? 1 : 0,
    };

    public static ColorBalanceAdjustment Load(IReadOnlyDictionary<string, float> p) => new()
    {
        ShadowsRed = (int)p.GetValueOrDefault("sr"), ShadowsGreen = (int)p.GetValueOrDefault("sg"), ShadowsBlue = (int)p.GetValueOrDefault("sb"),
        MidtonesRed = (int)p.GetValueOrDefault("mr"), MidtonesGreen = (int)p.GetValueOrDefault("mg"), MidtonesBlue = (int)p.GetValueOrDefault("mb"),
        HighlightsRed = (int)p.GetValueOrDefault("hr"), HighlightsGreen = (int)p.GetValueOrDefault("hg"), HighlightsBlue = (int)p.GetValueOrDefault("hb"),
        PreserveLuminosity = p.GetValueOrDefault("preserve", 1) != 0,
    };

    /// <summary>某通道的 LUT：v + 陰影權重×s + 中間調權重×m + 亮部權重×h（權重和為 1）。</summary>
    public static byte[] BuildTable(int shadows, int midtones, int highlights)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255f;
            var ws = (1 - t) * (1 - t);
            var wh = t * t;
            var wm = 1 - ws - wh;
            var shift = (shadows * ws + midtones * wm + highlights * wh) * 1.0f;
            table[i] = (byte)Math.Clamp(MathF.Round(i + shift), 0, 255);
        }
        return table;
    }

    public SKColorFilter CreateColorFilter()
    {
        var r = BuildTable(ShadowsRed, MidtonesRed, HighlightsRed);
        var g = BuildTable(ShadowsGreen, MidtonesGreen, HighlightsGreen);
        var b = BuildTable(ShadowsBlue, MidtonesBlue, HighlightsBlue);
        if (!PreserveLuminosity) return SKColorFilter.CreateTable(null, r, g, b);

        // 保留明度：偏移量的加權和為 0 → 三張表各扣掉「平均偏移」。近似 PS（它是在 Lab 做），對灰階完全成立。
        for (var i = 0; i < 256; i++)
        {
            var mean = (0.299f * (r[i] - i) + 0.587f * (g[i] - i) + 0.114f * (b[i] - i));
            r[i] = (byte)Math.Clamp(MathF.Round(r[i] - mean), 0, 255);
            g[i] = (byte)Math.Clamp(MathF.Round(g[i] - mean), 0, 255);
            b[i] = (byte)Math.Clamp(MathF.Round(b[i] - mean), 0, 255);
        }
        return SKColorFilter.CreateTable(null, r, g, b);
    }
}

/// <summary>
/// 相片濾鏡（Photoshop 的「相片濾鏡」）：像在鏡頭前加一片有色濾鏡 —— 每個通道乘上濾鏡色的比例，
/// 濃度決定乘多少；保留明度時把三個增益整體拉回讓白色亮度不變。
/// </summary>
public sealed record PhotoFilterAdjustment : IAdjustment
{
    /// <summary>PS 的預設暖色濾鏡（Warming Filter 85）。</summary>
    public SKColor Color { get; init; } = new(0xEC, 0x8A, 0x00);
    public int Density { get; init; } = 25;
    public bool PreserveLuminosity { get; init; } = true;

    public string DisplayName => "相片濾鏡";
    public string TypeId => "photoFilter";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new ColorParam("color", "濾鏡顏色", a => ((PhotoFilterAdjustment)a).Color,
            (a, v) => ((PhotoFilterAdjustment)a) with { Color = v }),
        new SliderParam("density", "濃度", 0, 100, a => ((PhotoFilterAdjustment)a).Density,
            (a, v) => ((PhotoFilterAdjustment)a) with { Density = (int)Math.Round(v) }, "%"),
        new BoolParam("preserve", "保留明度", a => ((PhotoFilterAdjustment)a).PreserveLuminosity,
            (a, v) => ((PhotoFilterAdjustment)a) with { PreserveLuminosity = v }),
    ];

    public Dictionary<string, float> SaveParams() => new()
    {
        ["r"] = Color.Red, ["g"] = Color.Green, ["b"] = Color.Blue, ["density"] = Density, ["preserve"] = PreserveLuminosity ? 1 : 0,
    };

    public static PhotoFilterAdjustment Load(IReadOnlyDictionary<string, float> p) => new()
    {
        Color = new SKColor((byte)p.GetValueOrDefault("r", 0xEC), (byte)p.GetValueOrDefault("g", 0x8A), (byte)p.GetValueOrDefault("b", 0)),
        Density = (int)p.GetValueOrDefault("density", 25),
        PreserveLuminosity = p.GetValueOrDefault("preserve", 1) != 0,
    };

    public (float R, float G, float B) Gains()
    {
        var d = Math.Clamp(Density, 0, 100) / 100f;
        var r = 1 - d + d * Color.Red / 255f;
        var g = 1 - d + d * Color.Green / 255f;
        var b = 1 - d + d * Color.Blue / 255f;
        if (PreserveLuminosity)
        {
            var luma = 0.299f * r + 0.587f * g + 0.114f * b;
            if (luma > 1e-4f)
            {
                r /= luma;
                g /= luma;
                b /= luma;
            }
        }
        return (r, g, b);
    }

    public SKColorFilter CreateColorFilter()
    {
        var (r, g, b) = Gains();
        return SKColorFilter.CreateColorMatrix(
        [
            r, 0, 0, 0, 0,
            0, g, 0, 0, 0,
            0, 0, b, 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }
}

/// <summary>
/// 通道混合器（Photoshop 的「通道混合器」）：每個輸出通道 = 三個輸入通道的百分比加權 + 常數；
/// 單色模式時三個輸出都用同一組（灰色）權重。就是一個 3×4 的色彩矩陣。
/// </summary>
public sealed record ChannelMixerAdjustment : IAdjustment
{
    /// <summary>列＝輸出 R/G/B（單色時只看第 0 列），欄＝輸入 R、G、B 的百分比與常數（%）。</summary>
    public int[] Rows { get; init; } = [100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0];
    public bool Monochrome { get; init; }

    public string DisplayName => "通道混合器";
    public string TypeId => "channelMixer";

    private static readonly string[] OutputNames = ["紅", "綠", "藍"];
    private static readonly string[] InputNames = ["紅", "綠", "藍", "常數"];

    public IReadOnlyList<ParamDef> Parameters { get; } = BuildParams();

    private static ParamDef[] BuildParams()
    {
        var list = new List<ParamDef>
        {
            new BoolParam("mono", "單色", a => ((ChannelMixerAdjustment)a).Monochrome,
                (a, v) => ((ChannelMixerAdjustment)a) with { Monochrome = v }),
        };
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 4; col++)
        {
            var index = row * 4 + col;
            list.Add(new SliderParam($"m{index}", $"輸出{OutputNames[row]} ← {InputNames[col]}", -200, 200,
                a => ((ChannelMixerAdjustment)a).Rows[index],
                (a, v) => ((ChannelMixerAdjustment)a).WithCell(index, (int)Math.Round(v)), "%"));
        }
        return list.ToArray();
    }

    private ChannelMixerAdjustment WithCell(int index, int value)
    {
        var rows = (int[])Rows.Clone();
        rows[index] = value;
        return this with { Rows = rows };
    }

    public Dictionary<string, float> SaveParams()
    {
        var p = new Dictionary<string, float> { ["mono"] = Monochrome ? 1 : 0 };
        for (var i = 0; i < 12; i++) p[$"m{i}"] = Rows[i];
        return p;
    }

    public static ChannelMixerAdjustment Load(IReadOnlyDictionary<string, float> p)
    {
        var rows = new int[12];
        var identity = new ChannelMixerAdjustment().Rows;
        for (var i = 0; i < 12; i++) rows[i] = (int)p.GetValueOrDefault($"m{i}", identity[i]);
        return new ChannelMixerAdjustment { Rows = rows, Monochrome = p.GetValueOrDefault("mono") != 0 };
    }

    public SKColorFilter CreateColorFilter()
    {
        var m = new float[20];
        for (var row = 0; row < 3; row++)
        {
            var source = Monochrome ? 0 : row;
            for (var col = 0; col < 3; col++) m[row * 5 + col] = Rows[source * 4 + col] / 100f;
            m[row * 5 + 4] = Rows[source * 4 + 3] / 100f * 255f;   // 常數欄以 0..255 計
        }
        m[18] = 1f;
        return SKColorFilter.CreateColorMatrix(m);
    }
}
