using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MinePainter.App.Services;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → 一般：啟動音效與更新檢查（原本是選單裡兩個勾選項 + 一個「檢查更新」）。
/// </summary>
public sealed class GeneralSettingsPage : SettingsPage
{
    public override string Description => "啟動與更新的行為。";

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
            },
        });
    }
}
