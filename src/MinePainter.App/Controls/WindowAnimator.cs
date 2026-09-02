using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace MinePainter.App.Controls;

/// <summary>
/// 無邊框浮動視窗的開關動畫：內容 Border 做 淡入 + 輕微放大。
///
/// 動不了 OS 視窗本身（無邊框 + 透明底），所以動的是內容；
/// 關閉時先播完退場再真正 Hide/Close，否則會看到瞬間消失。
/// 時長用 Motion 的 token（進場 Base、退場 Quick）。
/// </summary>
public static class WindowAnimator
{
    private static readonly TimeSpan InDuration = Motion.Base;
    private static readonly TimeSpan OutDuration = Motion.Quick;

    /// <summary>
    /// 應用程式正在關閉。此時所有子視窗必須立刻真的關掉 ——
    /// 為了播退場而 Cancel 掉一次 Closing 會讓整個關閉流程被中止（要按兩次才關得掉）。
    /// </summary>
    public static bool IsShuttingDown { get; set; }

    /// <summary>把 Border 準備成可動畫的狀態（設 transition 與變換原點）。</summary>
    public static void Prepare(Border root)
    {
        root.RenderTransformOrigin = RelativePoint.Center;
        root.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = InDuration,
                Easing = new CubicEaseOut(),
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = InDuration,
                Easing = new CubicEaseOut(),
            },
        ];
        SetHidden(root);
    }

    /// <summary>播放進場（每次 Show 都呼叫）。</summary>
    public static void PlayIn(Border root)
    {
        SetHidden(root);
        // 先讓起始值套進去，下一輪 layout 再設目標值 —— 同一幀內設會直接跳到終點
        Dispatcher.UIThread.Post(() =>
        {
            root.Opacity = 1;
            root.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>播放退場，播完呼叫 <paramref name="onFinished"/>（真正的 Hide/Close）。</summary>
    public static void PlayOut(Border root, Action onFinished)
    {
        if (IsShuttingDown)
        {
            onFinished();
            return;
        }

        if (root.Transitions is { Count: > 0 } transitions)
        {
            foreach (var t in transitions)
            {
                if (t is DoubleTransition d) d.Duration = OutDuration;
                else if (t is TransformOperationsTransition tr) tr.Duration = OutDuration;
            }
        }

        root.Opacity = 0;
        root.RenderTransform = ScaleTransform(0.97);

        DispatcherTimer.RunOnce(() =>
        {
            // 還原進場時長，下次 Show 才是原本的節奏
            if (root.Transitions is { Count: > 0 } ts)
            {
                foreach (var t in ts)
                {
                    if (t is DoubleTransition d) d.Duration = InDuration;
                    else if (t is TransformOperationsTransition tr) tr.Duration = InDuration;
                }
            }
            onFinished();
        }, OutDuration);
    }

    private static void SetHidden(Border root)
    {
        root.Opacity = 0;
        root.RenderTransform = ScaleTransform(0.96);
    }

    private static ITransform ScaleTransform(double scale) =>
        TransformOperations.Parse($"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
}
