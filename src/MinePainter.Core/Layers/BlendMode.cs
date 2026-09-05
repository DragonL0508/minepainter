using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 使用者可見的混合模式；合成時映射到 SKBlendMode。Additive 之後的是 Photoshop 專有、Skia 沒有的模式，
/// 由 <see cref="Compositing.CustomBlend"/> 自己逐像素算（GPU 路徑遇到就退回 CPU 合成）。
/// .mpp 存的是名稱：舊版程式讀到不認得的名稱會退成一般（Enum.TryParse 失敗），檔案照樣開得起來。
/// </summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    Additive,
    LinearBurn,
    LinearLight,
    VividLight,
    PinLight,
    HardMix,
    DarkerColor,
    LighterColor,
    Subtract,
    Divide,
}

public static class BlendModeExtensions
{
    public static SKBlendMode ToSkia(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        BlendMode.Additive => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,   // 自訂模式：Skia 這裡當一般，真正的混合在 CustomBlend
    };
}
