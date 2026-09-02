using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>色相旋轉（-180..180 度）/ 飽和度（-1..1）/ 明度（-1..1），以 color matrix 實作。</summary>
public sealed record HueSaturationAdjustment(float Hue = 0f, float Saturation = 0f, float Lightness = 0f) : IAdjustment
{
    public string DisplayName => "色相 / 飽和度";
    public string TypeId => "hueSaturation";

    public IReadOnlyList<ParamDef> Parameters { get; } =
    [
        new SliderParam("hue", "色相", -180, 180,
            a => ((HueSaturationAdjustment)a).Hue,
            (a, v) => ((HueSaturationAdjustment)a) with { Hue = (float)v }, "°") { Track = SliderTrack.Hue },
        new SliderParam("saturation", "飽和度", -100, 100,
            a => ((HueSaturationAdjustment)a).Saturation * 100,
            (a, v) => ((HueSaturationAdjustment)a) with { Saturation = (float)(v / 100) }),
        new SliderParam("lightness", "明度", -100, 100,
            a => ((HueSaturationAdjustment)a).Lightness * 100,
            (a, v) => ((HueSaturationAdjustment)a) with { Lightness = (float)(v / 100) }) { Track = SliderTrack.Brightness },
    ];

    public Dictionary<string, float> SaveParams() => new() { ["hue"] = Hue, ["saturation"] = Saturation, ["lightness"] = Lightness };

    public SKColorFilter CreateColorFilter()
    {
        var matrix = HueRotation(Hue * MathF.PI / 180f);
        matrix = Multiply(SaturationMatrix(1f + Math.Clamp(Saturation, -1f, 1f)), matrix);
        matrix = Multiply(LightnessMatrix(Math.Clamp(Lightness, -1f, 1f)), matrix);
        return SKColorFilter.CreateColorMatrix(matrix);
    }

    // Rec.601 亮度權重
    private const float LumR = 0.299f;
    private const float LumG = 0.587f;
    private const float LumB = 0.114f;

    private static float[] HueRotation(float rad)
    {
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        // 標準 hue-rotate color matrix（保持亮度）
        return
        [
            LumR + cos * (1 - LumR) + sin * -LumR,     LumG + cos * -LumG + sin * -LumG,          LumB + cos * -LumB + sin * (1 - LumB),     0, 0,
            LumR + cos * -LumR + sin * 0.143f,          LumG + cos * (1 - LumG) + sin * 0.140f,    LumB + cos * -LumB + sin * -0.283f,        0, 0,
            LumR + cos * -LumR + sin * -(1 - LumR),     LumG + cos * -LumG + sin * LumG,           LumB + cos * (1 - LumB) + sin * LumB,      0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] SaturationMatrix(float s)
    {
        var ir = (1 - s) * LumR;
        var ig = (1 - s) * LumG;
        var ib = (1 - s) * LumB;
        return
        [
            ir + s, ig, ib, 0, 0,
            ir, ig + s, ib, 0, 0,
            ir, ig, ib + s, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    private static float[] LightnessMatrix(float l)
    {
        // 正值往白、負值往黑（scale + offset）
        var scale = l < 0 ? 1 + l : 1 - l;
        var offset = l > 0 ? l * 255f : 0f;
        return
        [
            scale, 0, 0, 0, offset,
            0, scale, 0, 0, offset,
            0, 0, scale, 0, offset,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>5×4 color matrix 乘法：result = a × b。</summary>
    private static float[] Multiply(float[] a, float[] b)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                    sum += a[row * 5 + k] * b[k * 5 + col];
                if (col == 4) sum += a[row * 5 + 4];
                result[row * 5 + col] = sum;
            }
        }
        return result;
    }
}
