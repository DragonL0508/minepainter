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
    /// <summary>對任一控制項播一次「滑入」（Motion.Base，原點在頂端中央）。</summary>
    public static void Play(Control target) =>
        Motion.FadeSlideIn(target, "translateY(-6px) scaleY(0.97)", Motion.Base, new RelativePoint(0.5, 0, RelativeUnit.Relative));
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
