using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SkiaSharp;

namespace MinePainter.App;

/// <summary>
/// 應用程式主題。所有自繪 UI 共用這裡的「可變 brush 單例」——
/// 換主題時只改各 brush 的 Color，所有引用處（axaml x:Static 與 C# 直接引用）即時重繪，
/// 不需要重建任何視窗。Fluent 控制項（選單、按鈕、輸入框）由 RequestedThemeVariant 跟著切。
/// </summary>
public static class AppTheme
{
    public sealed record Palette(
        string Id, string Name, ThemeVariant Variant,
        uint Chrome, uint Panel, uint Header, uint Border, uint Separator, uint Inner,
        uint Text, uint TextMuted, uint Fill, uint FillHover, uint BarTrack,
        uint ToastBg, uint ToastText, uint Surround, uint Progress);

    public static readonly Palette[] Palettes =
    [
        new("midnight", "午夜黑", ThemeVariant.Dark,
            Chrome: 0xFF0D0D0F, Panel: 0xFF121215, Header: 0xFF1A1A1E, Border: 0xFF050507,
            Separator: 0xFF2A2A30, Inner: 0xFF08080A, Text: 0xFFD6D6DB, TextMuted: 0xFF80808A,
            Fill: 0xFFE4E4E9, FillHover: 0xFFFFFFFF, BarTrack: 0xFF08080A,
            ToastBg: 0xF01A1A20, ToastText: 0xFFE8E8EC, Surround: 0xFF0A0A0C, Progress: 0xFFFFFFFF),
        new("dark", "暗色", ThemeVariant.Dark,
            Chrome: 0xFF232327, Panel: 0xFF2A2A2E, Header: 0xFF35353B, Border: 0xFF1A1A1D,
            Separator: 0xFF45454B, Inner: 0xFF1E1E22, Text: 0xFFDDDDE2, TextMuted: 0xFF9A9AA2,
            Fill: 0xFFE4E4E9, FillHover: 0xFFFFFFFF, BarTrack: 0xFF1E1E22,
            ToastBg: 0xF0323238, ToastText: 0xFFF0F0F4, Surround: 0xFF252529, Progress: 0xFFFFFFFF),
        new("light", "亮色", ThemeVariant.Light,
            Chrome: 0xFFE2E2E7, Panel: 0xFFECECF0, Header: 0xFFD8D8DF, Border: 0xFFB9B9C2,
            Separator: 0xFFC4C4CC, Inner: 0xFFDFDFE5, Text: 0xFF26262B, TextMuted: 0xFF6E6E78,
            Fill: 0xFFF9F9FC, FillHover: 0xFFFFFFFF, BarTrack: 0xFFC7C7D0,
            ToastBg: 0xF0F2F2F6, ToastText: 0xFF26262B, Surround: 0xFFA9ADB5, Progress: 0xFF3A3A42),
        new("white", "極淨白", ThemeVariant.Light,
            Chrome: 0xFFFBFBFD, Panel: 0xFFFFFFFF, Header: 0xFFF1F1F5, Border: 0xFFE0E0E6,
            Separator: 0xFFE8E8EE, Inner: 0xFFF4F4F8, Text: 0xFF202025, TextMuted: 0xFF8A8A94,
            Fill: 0xFFFBFBFD, FillHover: 0xFFFFFFFF, BarTrack: 0xFFDDDDE5,
            ToastBg: 0xF0FFFFFF, ToastText: 0xFF202025, Surround: 0xFFEDEEF1, Progress: 0xFF3A3A42),
    ];

    // ---- 可變 brush 單例（初始值 = 暗色，與舊版硬編碼一致） ----

    public static SolidColorBrush ChromeBrush { get; } = new(Color.FromUInt32(0xFF232327));
    public static SolidColorBrush PanelBrush { get; } = new(Color.FromUInt32(0xFF2A2A2E));
    public static SolidColorBrush HeaderBrush { get; } = new(Color.FromUInt32(0xFF35353B));
    public static SolidColorBrush BorderBrush { get; } = new(Color.FromUInt32(0xFF1A1A1D));
    public static SolidColorBrush SeparatorBrush { get; } = new(Color.FromUInt32(0xFF45454B));
    public static SolidColorBrush InnerBrush { get; } = new(Color.FromUInt32(0xFF1E1E22));
    public static SolidColorBrush TextBrush { get; } = new(Color.FromUInt32(0xFFDDDDE2));
    public static SolidColorBrush TextMutedBrush { get; } = new(Color.FromUInt32(0xFF9A9AA2));
    public static SolidColorBrush FillBrush { get; } = new(Color.FromUInt32(0xFFE4E4E9));
    public static SolidColorBrush FillHoverBrush { get; } = new(Color.FromUInt32(0xFFFFFFFF));

    /// <summary>BarSlider 的底條色：亮色主題下要比 Inner 深，白色填滿才有對比（免畫邊界線）。</summary>
    public static SolidColorBrush BarTrackBrush { get; } = new(Color.FromUInt32(0xFF1E1E22));

    /// <summary>
    /// 進度條（存檔／匯出／下載）的填色。以前沒指定，Avalonia 會拿 Windows 的系統強調色去畫，
    /// 每台機器不一樣；統一成暗色主題白、亮色主題深灰（白條在淺色軌道上根本看不見）。
    /// </summary>
    public static SolidColorBrush ProgressBrush { get; } = new(Color.FromUInt32(0xFFFFFFFF));
    public static SolidColorBrush ToastBgBrush { get; } = new(Color.FromUInt32(0xF0323238));
    public static SolidColorBrush ToastTextBrush { get; } = new(Color.FromUInt32(0xFFF0F0F4));

    /// <summary>強調色（各主題共用；選取框、把手、徽章都是它）。</summary>
    public static SolidColorBrush AccentBrush { get; } = new(Color.FromRgb(0x2A, 0x9D, 0xF4));

    /// <summary>畫布外圍的底色（render thread 讀，struct 直接換整個值）。</summary>
    public static SKColor CanvasSurround { get; private set; } = new(0x25, 0x25, 0x29);

    public static string CurrentId { get; private set; } = "dark";

    public static event Action? Changed;

    /// <summary>套用主題（UI 執行緒）。未知 id 落回暗色。</summary>
    public static void Apply(string id)
    {
        var p = Palettes.FirstOrDefault(x => x.Id == id) ?? Palettes[1];
        CurrentId = p.Id;

        ChromeBrush.Color = Color.FromUInt32(p.Chrome);
        PanelBrush.Color = Color.FromUInt32(p.Panel);
        HeaderBrush.Color = Color.FromUInt32(p.Header);
        BorderBrush.Color = Color.FromUInt32(p.Border);
        SeparatorBrush.Color = Color.FromUInt32(p.Separator);
        InnerBrush.Color = Color.FromUInt32(p.Inner);
        TextBrush.Color = Color.FromUInt32(p.Text);
        TextMutedBrush.Color = Color.FromUInt32(p.TextMuted);
        FillBrush.Color = Color.FromUInt32(p.Fill);
        FillHoverBrush.Color = Color.FromUInt32(p.FillHover);
        BarTrackBrush.Color = Color.FromUInt32(p.BarTrack);
        ProgressBrush.Color = Color.FromUInt32(p.Progress);
        ToastBgBrush.Color = Color.FromUInt32(p.ToastBg);
        ToastTextBrush.Color = Color.FromUInt32(p.ToastText);
        CanvasSurround = new SKColor((byte)(p.Surround >> 16), (byte)(p.Surround >> 8), (byte)p.Surround);

        if (Application.Current is { } app) app.RequestedThemeVariant = p.Variant;
        Changed?.Invoke();
    }
}

/// <summary>
/// 畫布外圍的背景圖（使用者自選的圖 + 不透明度）。
/// UI 執行緒寫、render thread 讀 —— 影像引用整個換掉，舊圖延遲釋放，
/// 避免 render thread 正在畫的那張被 Dispose。
/// </summary>
public static class CanvasBackdrop
{
    private static volatile SKImage? _image;
    private static volatile byte _alpha = 26; // 10%

    public static SKImage? Image => _image;
    public static byte Alpha => _alpha;
    public static string? Path { get; private set; }

    /// <summary>載入失敗回傳 false（檔案不存在/不是影像）；path = null 表示清除。</summary>
    public static bool Set(string? path, int opacityPercent)
    {
        SetOpacity(opacityPercent);
        if (path == Path && path != null) return true;

        SKImage? loaded = null;
        if (path != null)
        {
            try
            {
                using var bmp = SKBitmap.Decode(path);
                if (bmp == null) return false;
                loaded = SKImage.FromBitmap(bmp);
            }
            catch
            {
                return false;
            }
        }

        var old = _image;
        Path = path;
        _image = loaded;

        if (old != null)
        {
            // render thread 每幀重畫且不跨幀持有影像；延遲幾秒釋放實務上安全
            Avalonia.Threading.DispatcherTimer.RunOnce(old.Dispose, TimeSpan.FromSeconds(5));
        }
        return true;
    }

    public static void SetOpacity(int percent) =>
        _alpha = (byte)Math.Clamp(percent * 255 / 100, 0, 255);
}
