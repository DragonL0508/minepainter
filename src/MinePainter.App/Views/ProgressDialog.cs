using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>
/// 背景工作的進度對話框（存檔／匯出用）。不可被使用者關閉（無 ✕、吃掉 Esc、
/// 攔下 Closing），工作完成時由 <see cref="Finish"/> 收掉。
/// 一般不直接 new，走 <see cref="RunAsync"/>：工作丟背景執行緒，
/// 150ms 內完成就完全不顯示對話框（小文件存檔不閃視窗）。
/// </summary>
public sealed class ProgressDialog : ModalDialog
{
    private readonly ProgressBar _bar = new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 16,
        IsIndeterminate = true, // 第一次 Report 前先跑不定長度動畫
    };
    private bool _done;

    public ProgressDialog(string title) : base(title, 300)
    {
        SetBody(_bar, new Border(), showClose: false);
    }

    public void Report(double value)
    {
        _bar.IsIndeterminate = false;
        _bar.Value = Math.Clamp(value, 0, 1);
    }

    /// <summary>工作完成（成功或失敗都要呼叫），關掉對話框讓 ShowDialog 返回。</summary>
    public void Finish()
    {
        _done = true;
        if (IsVisible) Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_done) Close(); // Finish 趕在開窗前到了
    }

    // 進行中不可取消：Esc/Enter 全吃掉
    protected override void OnKeyDown(KeyEventArgs e) => e.Handled = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_done && !WindowAnimator.IsShuttingDown)
        {
            e.Cancel = true; // Alt+F4 之類的使用者關閉
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// 在背景執行緒跑 <paramref name="work"/>；超過 150ms 未完成才顯示進度對話框。
    /// 例外會在這裡重新拋出（呼叫端照原本的 try/catch 處理）。
    /// </summary>
    public static async Task RunAsync(Window owner, string title, Action<IProgress<double>> work)
    {
        var dialog = new ProgressDialog(title);
        var progress = new Progress<double>(dialog.Report); // 在 UI 執行緒建立 → Report 自動回到 UI 執行緒
        var task = Task.Run(() => work(progress));

        if (await Task.WhenAny(task, Task.Delay(150)) == task)
        {
            await task; // 已完成：拿結果（或讓例外浮出），不顯示對話框
            return;
        }

        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Finish));
        await dialog.ShowDialog(owner);
        await task;
    }
}
