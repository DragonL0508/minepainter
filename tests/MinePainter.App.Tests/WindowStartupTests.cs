using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MinePainter.App.Services;
using MinePainter.App.Views;
using Xunit;

namespace MinePainter.App.Tests;

public class WindowStartupTests
{
    [AvaloniaFact]
    public async Task 開圖模式對話框等主視窗顯示後才開啟且只執行一次()
    {
        var owner = new Window();
        var dialog = FastModeOpenDialog.ForFastProject(1920, 1080, 3840, 2160);
        var calls = 0;
        Task? pending = null;
        WindowStartup.WhenShown(owner, () =>
        {
            Assert.True(owner.IsVisible);
            calls++;
            pending = dialog.ShowDialog(owner);
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, calls);
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, calls);
        Assert.True(dialog.IsVisible);
        Assert.Same(owner, dialog.Owner);
        dialog.Close();
        await pending!;
        owner.Hide();
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, calls);
        owner.Close();
    }

    [AvaloniaFact]
    public void 已顯示的主視窗立即處理外部開檔()
    {
        var owner = new Window();
        owner.Show();
        var calls = 0;
        WindowStartup.WhenShown(owner, () => calls++);
        Assert.Equal(1, calls);
        owner.Close();
    }

    [AvaloniaFact]
    public void 顯示後立即關閉不再打開延後的對話框()
    {
        var owner = new Window();
        var calls = 0;
        WindowStartup.WhenShown(owner, () => calls++);
        owner.Show();
        owner.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, calls);
    }
}
