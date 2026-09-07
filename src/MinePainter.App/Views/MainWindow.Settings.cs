using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;

public partial class MainWindow
{
    // ---- 設定 ----

    /// <summary>
    /// 設定選單的每一項都開同一個設定視窗，只是直接跳到對應分類
    /// （CommandParameter＝<see cref="Settings.SettingsWindow.Page"/> 的名字）。
    /// 設定改動是即時生效的，關窗時統一存檔一次。
    /// </summary>
    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var page = Settings.SettingsWindow.Page.General;
        if ((sender as MenuItem)?.CommandParameter is string name &&
            Enum.TryParse<Settings.SettingsWindow.Page>(name, out var parsed))
        {
            page = parsed;
        }
        await OpenSettingsAsync(page);
    }

    /// <summary>開設定視窗到指定頁；關窗時存檔。</summary>
    private async Task OpenSettingsAsync(Settings.SettingsWindow.Page page)
    {
        var window = new Settings.SettingsWindow(page);
        var checkUpdates = false;
        window.CheckUpdatesRequested += () =>
        {
            checkUpdates = true;
            window.Close();
        };

        await window.ShowDialog(this);
        Services.AppSettings.Instance.Save();
        RefreshUiState(); // 快速模式門檻可能改了，「轉成快速模式」能不能按要跟著更新

        if (checkUpdates) await CheckUpdatesAsync(silent: false);
    }

    /// <summary>
    /// 另一個 MinePainter 程序把使用者點開的檔案轉過來（同 paint.net：不再開一個視窗）：
    /// 開成新分頁並把視窗叫到前景。
    /// </summary>
    private void OpenFilesFromOtherInstance(string[] files)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = Services.AppSettings.Instance.WindowMaximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        Activate();
        foreach (var file in files)
        {
            if (File.Exists(file)) OpenFile(file);
        }
    }

    /// <summary>
    /// 啟動時在背景做兩件事：（1）還沒安裝就裝到 %LocalAppData%\Programs\MinePainter，
    /// 檔案關聯才有不會被搬走的落點；（2）把關聯對齊現況 —— 第一次執行自動登記，
    /// 之後只在目標路徑變了時改寫。複製 exe 與寫登錄檔都要一段時間，不放在啟動路徑上。
    /// </summary>
    private void EnsureInstalledAndAssociated() => Task.Run(() =>
    {
        var settings = Services.AppSettings.Instance;

        var installed = false;
        if (settings.AutoInstall)
        {
            try
            {
                installed = Services.AppInstaller.EnsureInstalled();
            }
            catch
            {
                // 沒權限／磁碟滿了之類：照樣往下登記關聯（會指向現在這份 exe）
            }
        }

        bool registered;
        try
        {
            // 剛裝好＝全新的一次設定，關聯要跟著重建（例如安裝資料夾被手動刪掉過）
            registered = Services.FileAssociations.EnsureUpToDate(
                settings.FileAssociationsOptOut, settings.FileAssociationsRegistered && !installed);
        }
        catch
        {
            registered = false; // 登錄檔被政策鎖住之類：關聯沒了不影響其他功能
        }

        if (!installed && !registered) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (registered) settings.FileAssociationsRegistered = true;
            settings.Save();
            // 使用者的 exe 是自己解 zip 放的，突然多一份在 AppData 會嚇到人，講一聲
            if (installed) Toasts.Show("MinePainter 已安裝到本機，開始功能表找得到");
        });
    });
}
