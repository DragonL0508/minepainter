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
    // ---- 更新 ----

    /// <summary>更新已下載好時的 updater 腳本；程式關閉的最後一步啟動它。</summary>
    private string? _pendingUpdaterScript;
    private bool _checkingUpdates;

    private void OnCheckUpdatesClicked(object? sender, RoutedEventArgs e) => _ = CheckUpdatesAsync(silent: false);

    /// <summary>
    /// 查 GitHub 最新版。silent＝啟動時的靜默檢查：沒新版、查不到、開發建置、使用者略過的版本都不出聲；
    /// 手動檢查則每種結果都回報。
    /// </summary>
    private async Task CheckUpdatesAsync(bool silent)
    {
        if (_checkingUpdates) return;
        if (silent)
        {
            if (!Services.AppSettings.Instance.CheckUpdatesOnStartup) return;
            if (Services.UpdateService.IsDevBuild || !Services.UpdateService.IsSupported) return;
            await Task.Delay(TimeSpan.FromSeconds(3)); // 讓啟動先安靜完成
        }
        else if (!Services.UpdateService.IsSupported)
        {
            Toasts.Show("這個建置不支援程式內更新，請到下載頁取得新版");
            return;
        }

        _checkingUpdates = true;
        Services.UpdateInfo? info;
        try
        {
            info = await Services.UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            if (!silent) Toasts.Show("檢查更新失敗：" + ex.Message);
            _checkingUpdates = false;
            return;
        }
        _checkingUpdates = false;

        if (info == null)
        {
            if (!silent) Toasts.Show($"已是最新版（{Services.UpdateService.CurrentVersion.ToString(3)}）");
            return;
        }
        if (silent && string.Equals(Services.AppSettings.Instance.SkippedUpdateTag, info.Tag, StringComparison.OrdinalIgnoreCase))
            return;

        var dialog = new UpdateDialog(info);
        await dialog.ShowDialog(this);
        switch (dialog.Result)
        {
            case UpdateDialog.Choice.Skip:
                Services.AppSettings.Instance.SkippedUpdateTag = info.Tag;
                Services.AppSettings.Instance.Save();
                break;
            case UpdateDialog.Choice.Update when dialog.UpdaterScript != null:
                _pendingUpdaterScript = dialog.UpdaterScript;
                Close(); // 走正常關閉流程（未儲存會問）；真的關掉時才啟動 updater
                break;
        }
    }
}
