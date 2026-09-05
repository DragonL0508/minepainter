using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Adjustments;

/// <summary>
/// 非破壞性調整的參數物件。實作必須是不可變的 —— 改參數 = 換一個新實例，
/// undo 因此只需換參考（與點陣 tile 快照同構）。
/// 同一份物件也給「調整」選單做破壞性套用（<see cref="AdjustmentEffect"/>）。
/// </summary>
public interface IAdjustment : IParameterized
{
    string DisplayName { get; }

    /// <summary>存檔用的類型識別（.mpp）。</summary>
    string TypeId { get; }

    /// <summary>建立對應的 Skia 色彩濾鏡（caller 負責 Dispose）。</summary>
    SKColorFilter CreateColorFilter();

    /// <summary>存檔用參數（與 <see cref="AdjustmentRegistry.Load"/> 對稱）。</summary>
    Dictionary<string, float> SaveParams();
}

/// <summary>調整類型目錄：選單、調整圖層新增、.mpp 載入都查這裡。</summary>
public static class AdjustmentRegistry
{
    public sealed record Entry(
        string TypeId,
        string DisplayName,
        Func<IAdjustment> CreateDefault,
        Func<IReadOnlyDictionary<string, float>, IAdjustment> Load,
        bool HasDialog);

    public static readonly Entry[] All =
    [
        new("brightnessContrast", "亮度 / 對比", () => new BrightnessContrastAdjustment(),
            p => new BrightnessContrastAdjustment(p.GetValueOrDefault("brightness"), p.GetValueOrDefault("contrast")), true),
        new("curves", "曲線", () => new CurvesAdjustment(), CurvesAdjustment.Load, true),
        new("hueSaturation", "色相 / 飽和度", () => new HueSaturationAdjustment(),
            p => new HueSaturationAdjustment(p.GetValueOrDefault("hue"), p.GetValueOrDefault("saturation"), p.GetValueOrDefault("lightness")), true),
        new("levels", "色階", () => new LevelsAdjustment(), LevelsAdjustment.Load, true),
        new("exposure", "曝光度", () => new ExposureAdjustment(),
            p => new ExposureAdjustment(p.GetValueOrDefault("exposure"), p.GetValueOrDefault("offset"), p.GetValueOrDefault("gamma", 1f)), true),
        new("temperatureTint", "色溫 / 色調", () => new TemperatureTintAdjustment(),
            p => new TemperatureTintAdjustment(p.GetValueOrDefault("temperature"), p.GetValueOrDefault("tint")), true),
        new("posterize", "色調分離", () => new PosterizeAdjustment(),
            p => new PosterizeAdjustment((int)p.GetValueOrDefault("red", 16), (int)p.GetValueOrDefault("green", 16),
                (int)p.GetValueOrDefault("blue", 16), p.GetValueOrDefault("linked", 1) != 0), true),
        new("blackWhite", "黑白", () => new BlackWhiteAdjustment(), _ => new BlackWhiteAdjustment(), false),
        new("invert", "負片效果", () => new InvertAdjustment(), _ => new InvertAdjustment(), false),
        new("sepia", "懷舊", () => new SepiaAdjustment(), _ => new SepiaAdjustment(), false),
    ];

    public static Entry? Find(string typeId) => Array.Find(All, e => e.TypeId == typeId);

    public static IAdjustment Load(string typeId, IReadOnlyDictionary<string, float>? parameters)
    {
        var entry = Find(typeId) ?? throw new InvalidDataException($"未知調整類型：{typeId}");
        return entry.Load(parameters ?? new Dictionary<string, float>());
    }
}
