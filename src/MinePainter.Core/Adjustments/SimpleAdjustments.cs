using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>黑白（去飽和，Rec.601 亮度權重；paint.net 的 Black and White）。</summary>
public sealed record BlackWhiteAdjustment : IAdjustment
{
    public string DisplayName => "黑白";
    public string TypeId => "blackWhite";
    public IReadOnlyList<ParamDef> Parameters { get; } = [];
    public Dictionary<string, float> SaveParams() => new();

    public SKColorFilter CreateColorFilter() => SKColorFilter.CreateColorMatrix(
    [
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0, 0, 0, 1, 0,
    ]);
}

/// <summary>負片效果（RGB 反轉，alpha 不動）。</summary>
public sealed record InvertAdjustment : IAdjustment
{
    public string DisplayName => "負片效果";
    public string TypeId => "invert";
    public IReadOnlyList<ParamDef> Parameters { get; } = [];
    public Dictionary<string, float> SaveParams() => new();

    public SKColorFilter CreateColorFilter()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)(255 - i);
        return SKColorFilter.CreateTable(null, table, table, table);
    }
}

/// <summary>懷舊（去飽和後套棕褐色調；paint.net 的 Sepia）。</summary>
public sealed record SepiaAdjustment : IAdjustment
{
    public string DisplayName => "懷舊";
    public string TypeId => "sepia";
    public IReadOnlyList<ParamDef> Parameters { get; } = [];
    public Dictionary<string, float> SaveParams() => new();

    public SKColorFilter CreateColorFilter()
    {
        // 先去飽和（亮度），再以棕褐 LUT 分配到三通道（paint.net：Desaturate + Level 曲線）
        using var desaturate = new BlackWhiteAdjustment().CreateColorFilter();
        var r = new byte[256];
        var g = new byte[256];
        var b = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255f;
            r[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(t, 0.85f) * 1.0f), 0, 255);
            g[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(t, 1.0f) * 0.90f), 0, 255);
            b[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(t, 1.25f) * 0.72f), 0, 255);
        }
        using var tint = SKColorFilter.CreateTable(null, r, g, b);
        return SKColorFilter.CreateCompose(tint, desaturate); // 先 desaturate 再 tint
    }
}

/// <summary>色調分離：各通道保留 2..64 階。</summary>
public sealed record PosterizeAdjustment(int Red = 16, int Green = 16, int Blue = 16, bool Linked = true) : IAdjustment
{
    public string DisplayName => "色調分離";
    public string TypeId => "posterize";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("red", "紅", 2, 64,
            a => ((PosterizeAdjustment)a).Red,
            (a, v) => ((PosterizeAdjustment)a).SetChannel(0, (int)Math.Round(v))),
        new SliderParam("green", "綠", 2, 64,
            a => ((PosterizeAdjustment)a).Green,
            (a, v) => ((PosterizeAdjustment)a).SetChannel(1, (int)Math.Round(v))),
        new SliderParam("blue", "藍", 2, 64,
            a => ((PosterizeAdjustment)a).Blue,
            (a, v) => ((PosterizeAdjustment)a).SetChannel(2, (int)Math.Round(v))),
        new BoolParam("linked", "連動",
            a => ((PosterizeAdjustment)a).Linked,
            (a, v) => ((PosterizeAdjustment)a) with { Linked = v }),
    ];

    private PosterizeAdjustment SetChannel(int channel, int value)
    {
        value = Math.Clamp(value, 2, 64);
        if (Linked) return this with { Red = value, Green = value, Blue = value };
        return channel switch
        {
            0 => this with { Red = value },
            1 => this with { Green = value },
            _ => this with { Blue = value },
        };
    }

    public Dictionary<string, float> SaveParams() => new()
    {
        ["red"] = Red, ["green"] = Green, ["blue"] = Blue, ["linked"] = Linked ? 1 : 0,
    };

    public static byte[] BuildTable(int levels)
    {
        levels = Math.Clamp(levels, 2, 256);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var bucket = Math.Min(levels - 1, i * levels / 256);
            table[i] = (byte)Math.Round(bucket * 255.0 / (levels - 1));
        }
        return table;
    }

    public SKColorFilter CreateColorFilter() =>
        SKColorFilter.CreateTable(null, BuildTable(Red), BuildTable(Green), BuildTable(Blue));
}

/// <summary>
/// 色階：輸入黑／白點、gamma、輸出黑／白點（paint.net 的 Levels，套用於 RGB 三通道）。
/// </summary>
public sealed record LevelsAdjustment(
    int InputLow = 0, int InputHigh = 255, float Gamma = 1f, int OutputLow = 0, int OutputHigh = 255) : IAdjustment
{
    public string DisplayName => "色階";
    public string TypeId => "levels";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("inputLow", "輸入黑點", 0, 254,
            a => ((LevelsAdjustment)a).InputLow,
            (a, v) => ((LevelsAdjustment)a).WithInputLow((int)Math.Round(v))) { Track = SliderTrack.Gray },
        new SliderParam("inputHigh", "輸入白點", 1, 255,
            a => ((LevelsAdjustment)a).InputHigh,
            (a, v) => ((LevelsAdjustment)a).WithInputHigh((int)Math.Round(v))) { Track = SliderTrack.Gray },
        new SliderParam("gamma", "Gamma", 0.1, 10,
            a => ((LevelsAdjustment)a).Gamma,
            (a, v) => ((LevelsAdjustment)a) with { Gamma = (float)v }, "", 2),
        new SliderParam("outputLow", "輸出黑點", 0, 255,
            a => ((LevelsAdjustment)a).OutputLow,
            (a, v) => ((LevelsAdjustment)a) with { OutputLow = (int)Math.Round(v) }) { Track = SliderTrack.Gray },
        new SliderParam("outputHigh", "輸出白點", 0, 255,
            a => ((LevelsAdjustment)a).OutputHigh,
            (a, v) => ((LevelsAdjustment)a) with { OutputHigh = (int)Math.Round(v) }) { Track = SliderTrack.Gray },
    ];

    private LevelsAdjustment WithInputLow(int v) =>
        this with { InputLow = v, InputHigh = Math.Max(InputHigh, v + 1) };

    private LevelsAdjustment WithInputHigh(int v) =>
        this with { InputHigh = v, InputLow = Math.Min(InputLow, v - 1) };

    public Dictionary<string, float> SaveParams() => new()
    {
        ["inputLow"] = InputLow, ["inputHigh"] = InputHigh, ["gamma"] = Gamma,
        ["outputLow"] = OutputLow, ["outputHigh"] = OutputHigh,
    };

    public static LevelsAdjustment Load(IReadOnlyDictionary<string, float> p) => new(
        (int)p.GetValueOrDefault("inputLow", 0), (int)p.GetValueOrDefault("inputHigh", 255),
        p.GetValueOrDefault("gamma", 1f),
        (int)p.GetValueOrDefault("outputLow", 0), (int)p.GetValueOrDefault("outputHigh", 255));

    public byte[] BuildTable()
    {
        var table = new byte[256];
        var inLo = Math.Clamp(InputLow, 0, 254);
        var inHi = Math.Clamp(InputHigh, inLo + 1, 255);
        var gamma = Math.Clamp(Gamma, 0.1f, 10f);
        var outLo = Math.Clamp(OutputLow, 0, 255);
        var outHi = Math.Clamp(OutputHigh, 0, 255);
        for (var i = 0; i < 256; i++)
        {
            var t = Math.Clamp((i - inLo) / (float)(inHi - inLo), 0f, 1f);
            t = MathF.Pow(t, 1f / gamma);
            var v = outLo + t * (outHi - outLo);
            table[i] = (byte)Math.Clamp(MathF.Round(v), 0, 255);
        }
        return table;
    }

    public SKColorFilter CreateColorFilter()
    {
        var table = BuildTable();
        return SKColorFilter.CreateTable(null, table, table, table);
    }

    /// <summary>
    /// 自動色階：由 RGB 直方圖取 0.5% / 99.5% 分位當輸入黑／白點，
    /// 並把中位數推到 0.5 灰調整 gamma（paint.net Auto-Level 的精神）。
    /// </summary>
    public static LevelsAdjustment FromHistogram(long[] histogram)
    {
        long total = 0;
        foreach (var c in histogram) total += c;
        if (total == 0) return new LevelsAdjustment();

        var lo = Percentile(histogram, total, 0.005);
        var hi = Percentile(histogram, total, 0.995);
        if (hi <= lo) hi = Math.Min(255, lo + 1);

        var median = Percentile(histogram, total, 0.5);
        var t = Math.Clamp((median - lo) / (double)(hi - lo), 0.01, 0.99);
        // t^(1/gamma) = 0.5 → gamma = ln(t) / ln(0.5)
        var gamma = (float)Math.Clamp(Math.Log(t) / Math.Log(0.5), 0.1, 10);
        return new LevelsAdjustment(lo, hi, gamma);
    }

    private static int Percentile(long[] histogram, long total, double q)
    {
        var target = (long)(total * q);
        long acc = 0;
        for (var i = 0; i < 256; i++)
        {
            acc += histogram[i];
            if (acc >= target) return i;
        }
        return 255;
    }
}
