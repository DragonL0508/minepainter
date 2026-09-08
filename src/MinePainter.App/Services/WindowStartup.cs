using Avalonia.Controls;
using Avalonia.Threading;

namespace MinePainter.App.Services;

/// <summary>預熱階段不執行可能開啟對話框的工作；主視窗完成 Show 後再交回 UI 執行緒。</summary>
internal static class WindowStartup
{
    public static void WhenShown(Window owner, Action action)
    {
        if (owner.IsVisible)
        {
            action();
            return;
        }

        var closed = false;
        owner.Opened += Opened;
        owner.Closed += Closed;

        void Closed(object? sender, EventArgs args)
        {
            closed = true;
            owner.Opened -= Opened;
            owner.Closed -= Closed;
        }

        void Opened(object? sender, EventArgs args)
        {
            owner.Opened -= Opened;
            Dispatcher.UIThread.Post(() =>
            {
                owner.Closed -= Closed;
                if (closed) return;
                // Show 與排程之間可能又被隱藏；仍須等待下一次顯示，不能把隱藏視窗當 owner。
                WhenShown(owner, action);
            }, DispatcherPriority.Loaded);
        }
    }
}
