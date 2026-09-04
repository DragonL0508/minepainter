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
/// 畫面正下方的短訊息提示：從下方升起淡入、停一段時間後往下淡出，最多同時顯示數則。
/// 訊息太長就在固定寬度內以跑馬燈來回捲，不讓提示長到橫跨整個畫面。
/// 不攔截滑鼠（IsHitTestVisible = false），不會擋到畫布操作。
/// </summary>
public sealed class ToastHost : StackPanel
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2.6);

    /// <summary>文字區的最大寬度（超過就跑馬燈）。</summary>
    private const double MaxTextWidth = 420;

    /// <summary>跑馬燈捲動速度（px/秒）—— 慢到看得完，快到不會等太久。</summary>
    private const double MarqueeSpeed = 70;

    /// <summary>跑馬燈到兩端的停頓：頭尾各停一下，眼睛才跟得上。</summary>
    private static readonly TimeSpan MarqueeHold = TimeSpan.FromMilliseconds(850);

    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 32);
        IsHitTestVisible = false;
    }

    public void Show(string message)
    {
        while (Children.Count >= MaxVisible)
            Children.RemoveAt(0);

        var toast = BuildToast(message, out var marquee);
        Children.Add(toast);

        Dispatcher.UIThread.Post(async () =>
        {
            // 從下方升起：位置在畫面底部，往上冒出來才符合它出現的方向
            await Animate(toast, fromOpacity: 0, toOpacity: 1, fromY: 22, toY: 0,
                duration: Motion.Emphasis, Motion.Enter);

            var stay = Lifetime;
            if (marquee is { } m)
            {
                _ = RunMarquee(m.Text, m.Overflow, m.Cycle); // 跟著 toast 一起消失，不必等它
                stay = m.Cycle; // 捲完一趟來回才收，長訊息才讀得完
            }
            await Task.Delay(stay);
            if (!Children.Contains(toast)) return;

            await Animate(toast, fromOpacity: 1, toOpacity: 0, fromY: 0, toY: 12,
                duration: Motion.Emphasis + Motion.Quick, Motion.Exit);
            Children.Remove(toast);
        }, DispatcherPriority.Background);
    }

    /// <summary>需要跑馬燈時，回報要捲的文字、超出的寬度、以及來回一趟的時間。</summary>
    private readonly record struct Marquee(TextBlock Text, double Overflow, TimeSpan Cycle);

    private static Border BuildToast(string message, out Marquee? marquee)
    {
        var text = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = AppTheme.ToastTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Measure(Size.Infinity);
        var natural = text.DesiredSize;

        Control content;
        if (natural.Width <= MaxTextWidth)
        {
            marquee = null;
            content = text;
        }
        else
        {
            // Canvas 給子項無限的量測空間，文字才會保持原本的寬度（其他容器會把它壓成一行省略號）
            var overflow = natural.Width - MaxTextWidth;
            var travel = TimeSpan.FromSeconds(Math.Max(1.0, overflow / MarqueeSpeed));
            marquee = new Marquee(text, overflow, MarqueeHold + travel + MarqueeHold + travel);
            text.RenderTransform = new TranslateTransform();
            content = new Canvas
            {
                Width = MaxTextWidth,
                Height = natural.Height,
                ClipToBounds = true,
                Children = { text },
            };
        }

        return new Border
        {
            // TODO(素材)：背景要換成指定的圖（Assets/ 已經是 AvaloniaResource，放進去就能用
            // avares://MinePainter.App/Assets/<檔名> 取到）。圓角兩端會被拉扁的話改走三段切片。
            Background = AppTheme.ToastBgBrush,
            BorderBrush = AppTheme.AccentBrush,
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 9),
            Opacity = 0,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #70000000"),
            RenderTransform = new TranslateTransform(0, 22),
            Child = content,
        };
    }

    /// <summary>
    /// 跑馬燈：停一下 → 捲到底 → 停一下 → 捲回來，一直重複到 toast 被移除。
    /// 來回而不是繞圈，因為訊息是一句話，繞圈會在中間接出讀不通的句子。
    /// </summary>
    private static async Task RunMarquee(TextBlock text, double overflow, TimeSpan cycle)
    {
        var travel = TimeSpan.FromSeconds(Math.Max(1.0, overflow / MarqueeSpeed));
        var holdCue = MarqueeHold.TotalSeconds / cycle.TotalSeconds;
        var outCue = holdCue + travel.TotalSeconds / cycle.TotalSeconds;
        var backCue = outCue + holdCue;

        var animation = new Animation
        {
            Duration = cycle,
            Easing = new SineEaseInOut(), // 起步與停下都收斂，來回不會頓
            IterationCount = IterationCount.Infinite,
            Children =
            {
                Frame(0d, 0),
                Frame(holdCue, 0),
                Frame(outCue, -overflow),
                Frame(backCue, -overflow),
                Frame(1d, 0),
            },
        };
        await animation.RunAsync(text);

        static KeyFrame Frame(double cue, double x) => new()
        {
            Cue = new Cue(cue),
            Setters = { new Setter(TranslateTransform.XProperty, x) },
        };
    }

    private static async Task Animate(Visual target, double fromOpacity, double toOpacity,
        double fromY, double toY, TimeSpan duration, Easing easing)
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
                        new Setter(TranslateTransform.YProperty, fromY),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, toOpacity),
                        new Setter(TranslateTransform.YProperty, toY),
                    },
                },
            },
        };
        await animation.RunAsync(target);
    }
}
