using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.AI;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → AI 去背：remove.bg 的 API Key、解析度，以及遮罩後處理（填實／對比／收縮）。
/// 「圖層 → AI 去背」按下去直接用這裡的設定跑，不再每次問。
/// </summary>
public sealed class BackgroundRemovalSettingsPage : SettingsPage
{
    public override string Description => "去背走 remove.bg 線上服務（同 paint.net 的 Remove Background 插件）；顏色一律取原圖的原解析度像素。";

    public BackgroundRemovalSettingsPage()
    {
        var settings = AppSettings.Instance;

        var keyBox = new TextBox
        {
            FontSize = 12,
            Width = 320,
            PasswordChar = '•',
            Watermark = "貼上 remove.bg 的 API Key",
            Text = settings.RemoveBgApiKey ?? "",
        };
        keyBox.TextChanged += (_, _) =>
        {
            var key = (keyBox.Text ?? "").Trim();
            settings.RemoveBgApiKey = key.Length == 0 ? null : key;
        };
        ToolTip.SetTip(keyBox, "登入 remove.bg 後在儀表板取得；存在設定檔裡");

        var keyLink = new TextBlock
        {
            Text = "取得 API Key",
            FontSize = 11,
            Foreground = AppTheme.AccentBrush,
            TextDecorations = Avalonia.Media.TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        keyLink.PointerPressed += (_, _) => OpenUrl(RemoveBgClient.ApiKeyUrl);
        ToolTip.SetTip(keyLink, RemoveBgClient.ApiKeyUrl);

        var sizeCombo = new ComboBox { FontSize = 12, Width = 320 };
        sizeCombo.Items.Add(new ComboBoxItem { Content = "自動（有點數給最高解析度、扣 1 點；沒點數給預覽）", FontSize = 12 });
        sizeCombo.Items.Add(new ComboBoxItem { Content = "預覽（約 0.25 百萬像素；免費額度）", FontSize = 12 });
        sizeCombo.SelectedIndex = settings.RemoveBgPreview ? 1 : 0;
        sizeCombo.SelectionChanged += (_, _) => settings.RemoveBgPreview = sizeCombo.SelectedIndex == 1;

        var contrast = new BarSlider { Minimum = 0, Maximum = 100, Width = 200, Suffix = "%", DefaultValue = 0, Value = settings.RemoveBgContrast };
        contrast.ValueChanged += v => settings.RemoveBgContrast = (int)Math.Round(v);
        var shift = new BarSlider { Minimum = -20, Maximum = 20, Width = 200, Suffix = "px", DefaultValue = 0, Value = settings.RemoveBgShift };
        shift.ValueChanged += v => settings.RemoveBgShift = (int)Math.Round(v);

        Content = SettingsUi.Scroll(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SettingsUi.Section("remove.bg 帳號"),
                Row("API Key", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { keyBox, keyLink },
                }),
                Row("解析度", sizeCombo),
                SettingsUi.Hint("影像會上傳到 remove.bg 處理，需要網路。伺服器結果只當遮罩用：不管回來的是哪種解析度，" +
                                "顏色都是原圖的原解析度像素；伺服器只回預覽尺寸時，遮罩會以原圖做引導濾波精修放大。" +
                                "有選取範圍時只處理範圍內、範圍外一併清除。"),

                new Separator { Margin = new Thickness(0, 6) },

                SettingsUi.Section("遮罩後處理"),
                SettingsUi.Toggle(
                    "內部填實（只在邊緣保留半透明）",
                    "伺服器的遮罩在物件內部偶爾不到全不透明；勾選後離邊界夠遠的內部一律不透明，半透明只留在邊緣（髮絲、毛邊）。",
                    settings.RemoveBgSolidCore,
                    v => settings.RemoveBgSolidCore = v),
                Row("遮罩對比", contrast),
                SettingsUi.Hint("拉高可去掉半透明的殘影，但也會失去柔邊。"),
                Row("邊緣收縮", shift),
                SettingsUi.Hint("負＝收縮（吃掉殘留的背景色邊）、正＝擴張。"),
            },
        });
    }

    private static Control Row(string label, Control control) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock
            {
                Text = label + "：",
                FontSize = 12,
                Width = 72,
                VerticalAlignment = VerticalAlignment.Center,
            },
            control,
        },
    };

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 沒有預設瀏覽器就算了
        }
    }
}
