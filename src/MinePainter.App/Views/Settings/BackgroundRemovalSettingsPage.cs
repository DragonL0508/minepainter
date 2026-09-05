using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.AI;

namespace MinePainter.App.Views.Settings;

/// <summary>設定 → AI 去背：remove.bg 的 API Key、解析度、遮罩後處理。去背直接用這裡的設定跑。</summary>
public sealed class BackgroundRemovalSettingsPage : SettingsPage
{
    public BackgroundRemovalSettingsPage()
    {
        var settings = AppSettings.Instance;

        var keyBox = new TextBox
        {
            FontSize = 12,
            Width = 320,
            MinHeight = 72,
            AcceptsReturn = true,
            Watermark = "API Key（一行一組，依序備用）",
            Text = string.Join(Environment.NewLine, settings.RemoveBgApiKeys),
        };
        keyBox.TextChanged += (_, _) =>
        {
            settings.RemoveBgApiKeys = (keyBox.Text ?? "")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        };

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

        var sizeCombo = new ComboBox { FontSize = 12, Width = 320 };
        sizeCombo.Items.Add(new ComboBoxItem { Content = "自動（扣點）", FontSize = 12 });
        sizeCombo.Items.Add(new ComboBoxItem { Content = "預覽（免費）", FontSize = 12 });
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
                SettingsUi.Section("remove.bg"),
                Row("API Key", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { keyBox, keyLink },
                }),
                Row("解析度", sizeCombo),
                new Separator { Margin = new Thickness(0, 6) },

                SettingsUi.Section("遮罩後處理"),
                SettingsUi.Toggle("內部填實", null, settings.RemoveBgSolidCore, v => settings.RemoveBgSolidCore = v),
                Row("遮罩對比", contrast),
                Row("邊緣收縮", shift),
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
