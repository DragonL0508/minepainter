using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>亮度/對比（-1..1, -1..1）。以 256 項 LUT 實作（SKColorFilter.CreateTable）。</summary>
public sealed record BrightnessContrastAdjustment(float Brightness = 0f, float Contrast = 0f) : IAdjustment
{
    public string DisplayName => "亮度 / 對比";
    public string TypeId => "brightnessContrast";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("brightness", "亮度", -100, 100,
            a => ((BrightnessContrastAdjustment)a).Brightness * 100,
            (a, v) => ((BrightnessContrastAdjustment)a) with { Brightness = (float)(v / 100) }) { Track = SliderTrack.Brightness },
        new SliderParam("contrast", "對比", -100, 100,
            a => ((BrightnessContrastAdjustment)a).Contrast * 100,
            (a, v) => ((BrightnessContrastAdjustment)a) with { Contrast = (float)(v / 100) }),
    ];

    public Dictionary<string, float> SaveParams() => new() { ["brightness"] = Brightness, ["contrast"] = Contrast };

    public SKColorFilter CreateColorFilter()
    {
        var brightness = Math.Clamp(Brightness, -1f, 1f) * 255f;
        var c = Math.Clamp(Contrast, -1f, 1f) * 255f;
        var k = (259f * (c + 255f)) / (255f * (259f - c)); // 經典對比係數

        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var v = (i + brightness - 128f) * k + 128f;
            table[i] = (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
        }

        // alpha 保持 identity
        return SKColorFilter.CreateTable(null, table, table, table);
    }
}
