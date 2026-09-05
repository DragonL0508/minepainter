using Avalonia.Headless.XUnit;
using MinePainter.App.Views.Settings;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 設定視窗（一個窗、左邊分類、右邊內容）：四頁都是用程式碼組的，
/// 排版一寫壞就是建構時當場炸掉——所以每頁都真的開一次。
/// </summary>
public class SettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(SettingsWindow.Page.General)]
    [InlineData(SettingsWindow.Page.Appearance)]
    [InlineData(SettingsWindow.Page.Shortcuts)]
    [InlineData(SettingsWindow.Page.FileAssociations)]
    public void 每個分類都開得起來(SettingsWindow.Page page)
    {
        var window = new SettingsWindow(page);
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 開在指定分類之後，其他分類也要都切得過去（每頁第一次切過去才真的被建出來）
        foreach (var other in Enum.GetValues<SettingsWindow.Page>())
        {
            window.Select(other);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        window.Close();
    }
}
