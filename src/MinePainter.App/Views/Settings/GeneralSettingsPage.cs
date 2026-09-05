using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MinePainter.App.Services;
using MinePainter.Core.Documents;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → 一般：啟動音效、更新檢查（原本是選單裡兩個勾選項 + 一個「檢查更新」），
/// 以及快速模式的代理級別（多大的畫布才提示快速模式、開下去縮到多大）。
/// </summary>
public sealed class GeneralSettingsPage : SettingsPage
{
    public override string Description => "啟動、更新與快速模式的行為。";

    public GeneralSettingsPage(Action checkUpdatesRequested)
    {
        var settings = AppSettings.Instance;

        var checkNow = new Button { Content = "立即檢查更新", FontSize = 12, Padding = new Thickness(14, 6) };
        checkNow.Click += (_, _) => checkUpdatesRequested();

        var version = UpdateService.CurrentVersion.ToString(3);

        Content = SettingsUi.Scroll(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SettingsUi.Section("啟動"),
                SettingsUi.Toggle(
                    "啟動音效",
                    "啟動畫面出現、載入完成、主視窗現身時的三段提示音。",
                    settings.StartupSounds,
                    v => settings.StartupSounds = v),

                new Separator { Margin = new Thickness(0, 6) },

                SettingsUi.Section("更新"),
                SettingsUi.Toggle(
                    "啟動時檢查更新",
                    "開啟程式幾秒後靜默去 GitHub 看有沒有新版；沒有新版不會出聲（開發建置不檢查）。",
                    settings.CheckUpdatesOnStartup,
                    v => settings.CheckUpdatesOnStartup = v),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        checkNow,
                        new TextBlock
                        {
                            Text = $"目前版本 {version}",
                            FontSize = 11,
                            Foreground = AppTheme.TextMutedBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },

                new Separator { Margin = new Thickness(0, 6) },

                SettingsUi.Section("快速模式"),
                SettingsUi.Hint("畫布比這個級別大的專案才會提示可以用快速模式；開下去畫布就縮到這個級別，"
                              + "輸出仍然是專案原本的解析度。調小＝更多專案適用、編輯更順，但代理畫布上的像素更粗。"),
                BuildProxyLevelRow(settings),
            },
        });
    }

    /// <summary>代理級別下拉：清單是 <see cref="FastMode.Levels"/>，另外容得下手改 settings.json 的值。</summary>
    private static Control BuildProxyLevelRow(AppSettings settings)
    {
        // 讀實際生效的值（啟動時已經夾過範圍），不是 settings.json 裡可能被手改壞的那個數字
        var current = FastMode.ProxyHeight;
        var heights = FastMode.Levels.ToList();
        if (!heights.Contains(current)) heights.Add(current);
        heights.Sort();

        var combo = new ComboBox { FontSize = 12, Width = 220 };
        foreach (var h in heights)
        {
            var text = $"{h}p（{FastMode.WidthFor(h)} × {h}）";
            if (h == FastMode.DefaultProxyHeight) text += "，預設";
            combo.Items.Add(new ComboBoxItem { Content = text, FontSize = 12, Tag = h });
        }
        combo.SelectedIndex = heights.IndexOf(current);

        combo.SelectionChanged += (_, _) =>
        {
            if ((combo.SelectedItem as ComboBoxItem)?.Tag is not int height) return;
            FastMode.ProxyHeight = height; // 立即生效：之後開的檔、按「轉成快速模式」都用新門檻
            settings.FastModeProxyHeight = FastMode.ProxyHeight;
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "代理解析度：",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                combo,
            },
        };
    }
}
