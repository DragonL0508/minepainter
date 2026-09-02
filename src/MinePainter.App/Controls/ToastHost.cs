using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MinePainter.App.Controls;

/// <summary>
/// 右下角的短訊息提示：滑入淡出，最多同時顯示數則，舊的自動退場。
/// 不攔截滑鼠（IsHitTestVisible = false），不會擋到畫布操作。
/// </summary>
public sealed class ToastHost : StackPanel
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2.6);

    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 18, 18);
        IsHitTestVisible = false;
    }

    public void Show(string message)
    {
        while (Children.Count >= MaxVisible)
            Children.RemoveAt(0);

        var toast = BuildToast(message);
        Children.Add(toast);

        // 滑入 + 淡入
        Dispatcher.UIThread.Post(async () =>
        {
            await Animate(toast, fromOpacity: 0, toOpacity: 1, fromX: 26, toX: 0,
                duration: Motion.Emphasis, Motion.Enter);

            await Task.Delay(Lifetime);
            if (!Children.Contains(toast)) return;

            await Animate(toast, fromOpacity: 1, toOpacity: 0, fromX: 0, toX: 14,
                duration: Motion.Emphasis + Motion.Quick, Motion.Exit);
            Children.Remove(toast);
        }, DispatcherPriority.Background);
    }

    private static Border BuildToast(string message) => new()
    {
        Background = AppTheme.ToastBgBrush,
        BorderBrush = AppTheme.AccentBrush,
        BorderThickness = new Thickness(0, 0, 0, 2),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 9),
        Opacity = 0,
        BoxShadow = BoxShadows.Parse("0 4 16 0 #70000000"),
        RenderTransform = new TranslateTransform(26, 0),
        Child = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = AppTheme.ToastTextBrush,
        },
    };

    private static async Task Animate(Visual target, double fromOpacity, double toOpacity,
        double fromX, double toX, TimeSpan duration, Easing easing)
    {
        var animation = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, fromOpacity),
                        new Setter(TranslateTransform.XProperty, fromX),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, toOpacity),
                        new Setter(TranslateTransform.XProperty, toX),
                    },
                },
            },
        };
        await animation.RunAsync(target);
    }
}
