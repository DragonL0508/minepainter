using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace MinePainter.App.Controls;

/// <summary>
/// 彈出層（Flyout／MenuFlyout）的開啟動畫：淡入 + 從上方 6px 滑下、微放大。
///
/// ComboBox 下拉與選單走 Styles/Animations.axaml 的 keyframe（有 :dropdownopen／:open
/// 偽類可觸發）；Flyout 沒有偽類、presenter 又只建一次，所以用子類在 OnOpened 裡動
/// Popup.Child（＝整塊 presenter）。時間比照其他微動畫（140ms）。
/// </summary>
public static class PopupAnimator
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(140);

    /// <summary>對任一控制項播一次「滑入」（起始值先套用，下一輪 layout 再設目標值）。</summary>
    public static void Play(Control target)
    {
        target.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);
        target.Transitions ??= [];
        if (!target.Transitions.Any(t => t is DoubleTransition d && d.Property == Visual.OpacityProperty))
        {
            target.Transitions.Add(new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = Duration,
                Easing = new CubicEaseOut(),
            });
            target.Transitions.Add(new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = Duration,
                Easing = new CubicEaseOut(),
            });
        }

        target.Opacity = 0;
        target.RenderTransform = TransformOperations.Parse("translateY(-6px) scaleY(0.97)");
        Dispatcher.UIThread.Post(() =>
        {
            target.Opacity = 1;
            target.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Loaded);
    }
}

/// <summary>會播開啟動畫的 Flyout（用法同 Flyout）。</summary>
public sealed class AnimatedFlyout : Flyout
{
    protected override void OnOpened()
    {
        base.OnOpened();
        if (Popup.Child is Control presenter) PopupAnimator.Play(presenter);
    }
}

/// <summary>會播開啟動畫的 MenuFlyout（用法同 MenuFlyout）。</summary>
public sealed class AnimatedMenuFlyout : MenuFlyout
{
    protected override void OnOpened()
    {
        base.OnOpened();
        if (Popup.Child is Control presenter) PopupAnimator.Play(presenter);
    }
}
