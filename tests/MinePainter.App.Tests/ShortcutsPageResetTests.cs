using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MinePainter.App.Services;
using MinePainter.App.Views.Settings;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 快捷鍵頁的「全部重設」：按下去之後畫面上每一顆按鈕都要跟著回到預設值
/// （使用者 2026-09-05 回報：按了沒有刷新）。
/// </summary>
[Collection("ShortcutMap")]
public class ShortcutsPageResetTests : IDisposable
{
    public void Dispose()
    {
        ShortcutMap.ResetAll();
        WheelMap.ResetAll();
    }

    private static Button FindButton(Visual root, string content) =>
        root.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == content);

    private static IEnumerable<string> ButtonTexts(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? "");

    [AvaloniaFact]
    public void 全部重設之後按鍵與滾輪的按鈕都回到預設()
    {
        // 先改掉一組按鍵與一組滾輪，畫面上才看得出「有沒有刷新」
        ShortcutMap.SetGesture("tool.brush", 0, new KeyGesture(Key.F9));
        WheelMap.Set("wheel.brushSize", KeyModifiers.Control | KeyModifiers.Shift);

        var page = new ShortcutsSettingsPage();
        var window = new Window { Width = 800, Height = 600, Content = page };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("F9", ButtonTexts(page));
        Assert.Contains("Control + Shift + 滾輪", ButtonTexts(page));

        FindButton(page, "全部重設").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("F9", ButtonTexts(page));
        Assert.Contains("B", ButtonTexts(page));                  // 筆刷回到預設的 B
        Assert.DoesNotContain("Control + Shift + 滾輪", ButtonTexts(page));
        Assert.Contains("Alt + 滾輪", ButtonTexts(page));          // 筆刷大小回到預設的 Alt

        window.Close();
    }

    /// <summary>
    /// ShortcutMap 是靜態的，Changed 在改表的那條執行緒上同步發出。頁面開著時從別的執行緒改表
    /// （CI 上平行跑的一般 [Fact] 測試就是這樣）不能炸 "Call from invalid thread"，而且畫面還是要刷新。
    /// 2026-09-07 CI 第一次跑就抓到這個，本機因為測試順序不同從沒出現過。
    /// </summary>
    [AvaloniaFact]
    public void 從別的執行緒改快捷鍵_頁面不炸且會刷新()
    {
        var page = new ShortcutsSettingsPage();
        var window = new Window { Width = 800, Height = 600, Content = page };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("F9", ButtonTexts(page));

        // 在背景執行緒改表：handler 會在那條執行緒被叫到，它得自己排回 UI 執行緒
        var worker = Task.Run(() => ShortcutMap.SetGesture("tool.brush", 0, new KeyGesture(Key.F9)));
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "背景執行緒改快捷鍵卡住或丟例外，代表 handler 直接在非 UI 執行緒動了控制項");

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("F9", ButtonTexts(page)); // 排回 UI 執行緒後有真的刷新，不是悄悄略過

        window.Close();
    }
}
