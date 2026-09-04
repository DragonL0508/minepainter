using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Services;

namespace MinePainter.App.Views;

/// <summary>
/// 「有新版本」對話框：版本、更新說明、三顆鈕（更新並重啟／略過此版／稍後）。
/// 按更新後在同一個視窗顯示下載進度；準備好時 <see cref="UpdaterScript"/> 有值，
/// 由主視窗關閉流程的最後一步啟動它。
/// </summary>
public sealed class UpdateDialog : ModalDialog
{
    public enum Choice { Later, Skip, Update }

    private readonly UpdateInfo _info;
    private readonly ProgressBar _bar = new()
    {
        Minimum = 0, Maximum = 1, Height = 14, IsVisible = false,
        Foreground = AppTheme.ProgressBrush, // 不吃 Windows 的系統強調色
        Background = AppTheme.BarTrackBrush,
    };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush, IsVisible = false };
    private readonly Button _update;
    private readonly Button _skip;
    private readonly Button _later;
    private bool _busy;

    public Choice Result { get; private set; } = Choice.Later;

    /// <summary>準備完成的 updater 腳本；null = 沒更新。</summary>
    public string? UpdaterScript { get; private set; }

    public UpdateDialog(UpdateInfo info) : base("有新版本", 440)
    {
        _info = info;

        var headline = new TextBlock
        {
            Text = $"MinePainter {info.Version.ToString(3)} 可以更新（目前 {UpdateService.CurrentVersion.ToString(3)}）",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        var size = info.Size > 0 ? $"　下載 {info.Size / 1048576.0:0.#} MB" : "";
        var sub = new TextBlock
        {
            Text = $"{info.AssetName}{size}",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
        };
        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "（這個版本沒有寫更新說明）" : info.Notes.Trim(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var notesBox = new Border
        {
            Background = AppTheme.InnerBrush,
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            MaxHeight = 220,
            Child = new ScrollViewer { Content = notes },
        };
        var hint = new TextBlock
        {
            Text = "更新會關閉程式、覆蓋目前的 MinePainter.exe，然後自動重新開啟。未儲存的文件會先問你要不要存。",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children = { headline, sub, notesBox, hint, _bar, _status },
        };

        _update = new Button { Content = "更新並重啟", Padding = new Thickness(14, 6), FontSize = 12 };
        _update.Classes.Add("accent");
        _update.Click += async (_, _) => await RunUpdateAsync();
        _skip = new Button { Content = "略過此版", Padding = new Thickness(14, 6), FontSize = 12 };
        _skip.Click += (_, _) => { Result = Choice.Skip; Close(); };
        _later = new Button { Content = "稍後", Padding = new Thickness(14, 6), FontSize = 12 };
        _later.Click += (_, _) => { Result = Choice.Later; Close(); };

        SetBody(body, ButtonRow(_update, _skip, _later));
    }

    private async Task RunUpdateAsync()
    {
        if (_busy) return;
        _busy = true;
        _update.IsEnabled = false;
        _skip.IsEnabled = false;
        _later.IsEnabled = false;
        _bar.IsVisible = true;
        _status.IsVisible = true;
        _status.Text = "下載中…";
        _bar.IsIndeterminate = true;
        try
        {
            var progress = new Progress<double>(v =>
            {
                _bar.IsIndeterminate = false;
                _bar.Value = v;
                _status.Text = v >= 1 ? "準備完成，即將重新啟動…" : $"下載中… {v * 100:0}%";
            });
            UpdaterScript = await UpdateService.PrepareAsync(_info, progress);
            Result = Choice.Update;
            _busy = false; // 不復位的話 OnClosing 會把這次 Close 取消掉，視窗就卡在「準備完成」
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "更新失敗：" + ex.Message;
            _bar.IsVisible = false;
            _busy = false;
            _update.IsEnabled = true;
            _skip.IsEnabled = true;
            _later.IsEnabled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 下載中不讓關；準備好腳本後一定放行（否則更新流程走不下去）
        if (_busy && UpdaterScript == null && !Controls.WindowAnimator.IsShuttingDown) { e.Cancel = true; return; }
        base.OnClosing(e);
    }
}
