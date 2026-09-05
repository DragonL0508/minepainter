using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// 色溫／色調（Lightroom 的白平衡滑桿）：色溫往暖＝紅增藍減、往冷相反；色調往洋紅＝綠減紅藍增、往綠相反。
/// 用「增益」而不是「加常數」，黑的地方才不會被染成一片色。
/// </summary>
public sealed record TemperatureTintAdjustment(float Temperature = 0f, float Tint = 0f) : IAdjustment
{
    public string DisplayName => "色溫 / 色調";
    public string TypeId => "temperatureTint";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("temperature", "色溫", -100, 100,
            a => ((TemperatureTintAdjustment)a).Temperature * 100,
            (a, v) => ((TemperatureTintAdjustment)a) with { Temperature = (float)(v / 100) }) { Track = SliderTrack.Temperature },
        new SliderParam("tint", "色調", -100, 100,
            a => ((TemperatureTintAdjustment)a).Tint * 100,
            (a, v) => ((TemperatureTintAdjustment)a) with { Tint = (float)(v / 100) }) { Track = SliderTrack.Tint },
    ];

    public Dictionary<string, float> SaveParams() => new() { ["temperature"] = Temperature, ["tint"] = Tint };

    /// <summary>三個通道的增益（測試與濾鏡共用）。</summary>
    public (float R, float G, float B) Gains()
    {
        var t = Math.Clamp(Temperature, -1f, 1f);
        var u = Math.Clamp(Tint, -1f, 1f);
        return (1f + 0.30f * t + 0.10f * u, 1f - 0.25f * u, 1f - 0.30f * t + 0.10f * u);
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
/// 曝光度（Photoshop 的曝光度）：曝光以 EV 計（每 +1 亮一倍）、偏移量平移暗部、Gamma 校正中間調。
/// v' = (v × 2^曝光 + 偏移) ^ (1 / gamma)。
/// </summary>
public sealed record ExposureAdjustment(float Exposure = 0f, float Offset = 0f, float Gamma = 1f) : IAdjustment
{
    public string DisplayName => "曝光度";
    public string TypeId => "exposure";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("exposure", "曝光度", -3, 3,
            a => ((ExposureAdjustment)a).Exposure,
            (a, v) => ((ExposureAdjustment)a) with { Exposure = (float)v }, " EV", Decimals: 2) { Track = SliderTrack.Brightness },
        new SliderParam("offset", "偏移量", -0.5, 0.5,
            a => ((ExposureAdjustment)a).Offset,
            (a, v) => ((ExposureAdjustment)a) with { Offset = (float)v }, Decimals: 3),
        new SliderParam("gamma", "Gamma 校正", 0.1, 3,
            a => ((ExposureAdjustment)a).Gamma,
            (a, v) => ((ExposureAdjustment)a) with { Gamma = (float)Math.Max(0.01, v) }, Decimals: 2),
    ];

    public Dictionary<string, float> SaveParams() => new() { ["exposure"] = Exposure, ["offset"] = Offset, ["gamma"] = Gamma };

    public byte[] BuildTable()
    {
        var gain = MathF.Pow(2f, Math.Clamp(Exposure, -10f, 10f));
        var invGamma = 1f / Math.Max(0.01f, Gamma);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var v = i / 255f * gain + Offset;
            v = v <= 0 ? 0 : MathF.Pow(v, invGamma);
            table[i] = (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);
        }
        return table;
    }

    public SKColorFilter CreateColorFilter()
    {
        var table = BuildTable();
        return SKColorFilter.CreateTable(null, table, table, table);
    }
}
