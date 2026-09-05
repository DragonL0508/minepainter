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

    /// <summary>
    /// 參數之外的大塊資料（LUT 表）：一行文字，存進 .mpp 的 AdjustmentData／效果堆疊的 "data"。
    /// 大多數調整沒有。
    /// </summary>
    string? SaveData() => null;

    /// <summary>
    /// true = Skia 色彩濾鏡表達不了（3D LUT），所有 CPU 路徑改呼叫 <see cref="ApplyPixels"/>，
    /// GPU 路徑整份退回合成器。<see cref="CreateColorFilter"/> 只剩近似用途。
    /// </summary>
    bool RequiresPixelPath => false;

    /// <summary>逐像素套用（premul BGRA，就地改）；只有 <see cref="RequiresPixelPath"/> 的調整需要實作。</summary>
    void ApplyPixels(uint[] pixels, int count) => throw new NotSupportedException($"{DisplayName} 沒有像素路徑");
}

/// <summary>調整類型目錄：選單、調整圖層新增、.mpp 載入都查這裡。</summary>
public static class AdjustmentRegistry
{
    public sealed record Entry(
        string TypeId,
        string DisplayName,
        Func<IAdjustment> CreateDefault,
        Func<IReadOnlyDictionary<string, float>, IAdjustment> Load,
        bool HasDialog)
    {
        /// <summary>要吃附加資料（<see cref="IAdjustment.SaveData"/>）的調整用這個載入；有就優先於 Load。</summary>
        public Func<IReadOnlyDictionary<string, float>, string?, IAdjustment>? LoadWithData { get; init; }
    }

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
        new("colorBalance", "色彩平衡", () => new ColorBalanceAdjustment(), ColorBalanceAdjustment.Load, true),
        new("photoFilter", "相片濾鏡", () => new PhotoFilterAdjustment(), PhotoFilterAdjustment.Load, true),
        new("channelMixer", "通道混合器", () => new ChannelMixerAdjustment(), ChannelMixerAdjustment.Load, true),
        new("threshold", "臨界值", () => new ThresholdAdjustment(),
            p => new ThresholdAdjustment((int)p.GetValueOrDefault("level", 128)), true),
        new("posterize", "色調分離", () => new PosterizeAdjustment(),
            p => new PosterizeAdjustment((int)p.GetValueOrDefault("red", 16), (int)p.GetValueOrDefault("green", 16),
                (int)p.GetValueOrDefault("blue", 16), p.GetValueOrDefault("linked", 1) != 0), true),
        new("blackWhite", "黑白", () => new BlackWhiteAdjustment(), _ => new BlackWhiteAdjustment(), false),
        new("invert", "負片效果", () => new InvertAdjustment(), _ => new InvertAdjustment(), false),
        new("sepia", "懷舊", () => new SepiaAdjustment(), _ => new SepiaAdjustment(), false),
        new("lut", "LUT 調色", () => new LutAdjustment(), p => LutAdjustment.Load(p, null), true)
            { LoadWithData = LutAdjustment.Load },
    ];

    public static Entry? Find(string typeId) => Array.Find(All, e => e.TypeId == typeId);

    public static IAdjustment Load(string typeId, IReadOnlyDictionary<string, float>? parameters, string? data = null)
    {
        var entry = Find(typeId) ?? throw new InvalidDataException($"未知調整類型：{typeId}");
        parameters ??= new Dictionary<string, float>();
        return entry.LoadWithData != null ? entry.LoadWithData(parameters, data) : entry.Load(parameters);
    }
}
