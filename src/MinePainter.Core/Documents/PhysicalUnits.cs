namespace MinePainter.Core.Documents;

/// <summary>長度單位（「新增影像」與尺寸對話框用）。</summary>
public enum LengthUnit
{
    Pixel,
    Centimeter,
    Millimeter,
    Inch,
}

/// <summary>解析度單位：每英寸幾個像素（dpi／ppi）或每公分幾個像素。</summary>
public enum ResolutionUnit
{
    PixelsPerInch,
    PixelsPerCentimeter,
}

/// <summary>
/// 實體尺寸 ↔ 像素的換算，以及新增影像的預設集。文件只存像素與 <see cref="Document.Dpi"/>，
/// 公分／英寸都是顯示時換算出來的（Photoshop 也是這樣：改解析度不改像素，只改印出來的大小）。
/// </summary>
public static class PhysicalUnits
{
    public const float CentimetersPerInch = 2.54f;

    /// <summary>螢幕用文件的預設解析度（Windows 的 96 dpi；Photoshop 預設 72，印刷 300）。</summary>
    public const float ScreenDpi = 96f;
    public const float PrintDpi = 300f;

    public static string Label(LengthUnit unit) => unit switch
    {
        LengthUnit.Pixel => "像素",
        LengthUnit.Centimeter => "公分",
        LengthUnit.Millimeter => "公釐",
        LengthUnit.Inch => "英寸",
        _ => unit.ToString(),
    };

    public static string Label(ResolutionUnit unit) => unit switch
    {
        ResolutionUnit.PixelsPerInch => "像素/英寸",
        ResolutionUnit.PixelsPerCentimeter => "像素/公分",
        _ => unit.ToString(),
    };

    /// <summary>某單位的長度 → 像素（四捨五入，至少 1）。</summary>
    public static int ToPixels(double value, LengthUnit unit, double dpi) =>
        Math.Max(1, (int)Math.Round(ToPixelsExact(value, unit, dpi)));

    public static double ToPixelsExact(double value, LengthUnit unit, double dpi) => unit switch
    {
        LengthUnit.Pixel => value,
        LengthUnit.Inch => value * dpi,
        LengthUnit.Centimeter => value / CentimetersPerInch * dpi,
        LengthUnit.Millimeter => value / 10 / CentimetersPerInch * dpi,
        _ => value,
    };

    /// <summary>像素 → 某單位的長度。</summary>
    public static double FromPixels(double pixels, LengthUnit unit, double dpi) => unit switch
    {
        LengthUnit.Pixel => pixels,
        LengthUnit.Inch => pixels / dpi,
        LengthUnit.Centimeter => pixels / dpi * CentimetersPerInch,
        LengthUnit.Millimeter => pixels / dpi * CentimetersPerInch * 10,
        _ => pixels,
    };

    /// <summary>顯示某單位要幾位小數（像素整數、英寸兩位、公分兩位、公釐一位）。</summary>
    public static int Decimals(LengthUnit unit) => unit switch
    {
        LengthUnit.Pixel => 0,
        LengthUnit.Millimeter => 1,
        _ => 2,
    };

    public static double ToDpi(double value, ResolutionUnit unit) =>
        unit == ResolutionUnit.PixelsPerCentimeter ? value * CentimetersPerInch : value;

    public static double FromDpi(double dpi, ResolutionUnit unit) =>
        unit == ResolutionUnit.PixelsPerCentimeter ? dpi / CentimetersPerInch : dpi;

    /// <summary>新增影像的預設集：螢幕類直接給像素；印刷類給實體尺寸 + 300 dpi，像素算出來。</summary>
    public sealed record Preset(string Group, string Label, int Width, int Height, float Dpi)
    {
        public static Preset Pixels(string label, int width, int height) => new("螢幕", label, width, height, ScreenDpi);

        public static Preset Print(string label, double widthMm, double heightMm, float dpi = PrintDpi) =>
            new("印刷", label,
                ToPixels(widthMm, LengthUnit.Millimeter, dpi),
                ToPixels(heightMm, LengthUnit.Millimeter, dpi), dpi);

        public static Preset PrintInches(string label, double widthIn, double heightIn, float dpi = PrintDpi) =>
            new("印刷", label,
                ToPixels(widthIn, LengthUnit.Inch, dpi),
                ToPixels(heightIn, LengthUnit.Inch, dpi), dpi);
    }

    public static readonly Preset[] Presets =
    [
        Preset.Pixels("640 × 480", 640, 480),
        Preset.Pixels("800 × 600", 800, 600),
        Preset.Pixels("1024 × 768", 1024, 768),
        Preset.Pixels("1280 × 720（HD）", 1280, 720),
        Preset.Pixels("1920 × 1080（Full HD）", 1920, 1080),
        Preset.Pixels("2560 × 1440（2K）", 2560, 1440),
        Preset.Pixels("3840 × 2160（4K）", 3840, 2160),
        Preset.Pixels("1080 × 1080（Instagram 方形）", 1080, 1080),
        Preset.Pixels("1080 × 1920（限時動態／直式）", 1080, 1920),
        Preset.Pixels("1280 × 720（YouTube 縮圖）", 1280, 720),
        Preset.Pixels("Minecraft 材質 16 × 16", 16, 16),
        Preset.Pixels("Minecraft 材質 32 × 32", 32, 32),
        Preset.Pixels("Minecraft 皮膚 64 × 64", 64, 64),
        Preset.Print("A3（297 × 420 mm）", 297, 420),
        Preset.Print("A4（210 × 297 mm）", 210, 297),
        Preset.Print("A5（148 × 210 mm）", 148, 210),
        Preset.Print("B5（176 × 250 mm）", 176, 250),
        Preset.PrintInches("Letter（8.5 × 11 in）", 8.5, 11),
        Preset.PrintInches("相片 4 × 6 in", 4, 6),
        Preset.Print("名片（90 × 54 mm）", 90, 54),
        Preset.Print("明信片（100 × 148 mm）", 100, 148),
    ];
}
