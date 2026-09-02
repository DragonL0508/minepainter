using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace MinePainter.App.Controls;

/// <summary>
/// 全 app 共用的動畫節奏（motion tokens）與小工具。
///
/// 只有四個時長，所有微動畫都從這裡挑，介面才會有「同一個人做的」一致感：
/// <list type="bullet">
/// <item><see cref="Quick"/> 100ms：退場、按壓——東西消失要比出現快，使用者才不會覺得在等。</item>
/// <item><see cref="Base"/> 160ms：進場、狀態切換（hover／選中／顯示群組）。</item>
/// <item><see cref="Move"/> 200ms：位置移動（FLIP 補位、選取指示器滑動）——走得遠，要多一點時間眼睛才跟得上。</item>
/// <item><see cref="Emphasis"/> 240ms：需要被注意到的（toast）。</item>
/// </list>
/// 進場一律 <see cref="Enter"/>（CubicEaseOut：快出慢停），退場一律 <see cref="Exit"/>（CubicEaseIn：慢起快收）。
///
/// 所有 helper 都遵守同一個 Avalonia 規則：起始值要先套進一輪 layout，下一輪（<see cref="DispatcherPriority.Loaded"/>）
/// 才設目標值，否則 transition 看不到變化、直接跳到終點。
/// 加 transition 時永遠換成「自己的」新集合（複製既有項目再加）——樣式 Setter 給的 Transitions
/// 是所有同型控制項共用的同一個實例，直接 Add 會改到整個 app。
/// </summary>
public static class Motion
{
    public static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Move = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Emphasis = TimeSpan.FromMilliseconds(240);

    public static readonly Easing Enter = new CubicEaseOut();
    public static readonly Easing Exit = new CubicEaseIn();

    /// <summary>每個控制項自己的淡入／位移 transition（不與樣式共用），以及進行中的顯示／隱藏世代。</summary>
    private sealed class State
    {
        public DoubleTransition? Fade;
        public TransformOperationsTransition? Transform;
        public int Generation;
        /// <summary><see cref="SetVisible"/> 最後一次被要求的目標（避免淡入途中被每幀重複呼叫重播）。</summary>
        public bool? VisibleTarget;
    }

    private static readonly ConditionalWeakTable<Control, State> States = new();

    /// <summary>
    /// 確保控制項有「淡入＋位移」兩條 transition，並把時長／easing 調成指定值。
    /// 第一次呼叫會把樣式給的 transition 複製進一個新的集合再加上自己的。
    /// </summary>
    public static void EnsureFadeSlide(Control c, TimeSpan duration, Easing easing)
    {
        var st = States.GetOrCreateValue(c);
        if (st.Fade == null || st.Transform == null)
        {
            st.Fade = new DoubleTransition { Property = Visual.OpacityProperty };
            st.Transform = new TransformOperationsTransition { Property = Visual.RenderTransformProperty };
            var list = new Transitions();
            if (c.Transitions != null)
            {
                foreach (var t in c.Transitions)
                {
                    // 樣式若已對同一屬性有 transition，讓我們的取代它（同屬性兩條會互搶）
                    if (t is DoubleTransition { Property: { } p } && p == Visual.OpacityProperty) continue;
                    if (t is TransformOperationsTransition) continue;
                    list.Add(t);
                }
            }
            list.Add(st.Fade);
            list.Add(st.Transform);
            c.Transitions = list;
        }
        st.Fade.Duration = duration;
        st.Fade.Easing = easing;
        st.Transform.Duration = duration;
        st.Transform.Easing = easing;
    }

    /// <summary>
    /// 進場：從透明＋<paramref name="fromTransform"/>（CSS 寫法，例如 "translateY(-6px) scale(0.96)"）
    /// 滑到原位。<paramref name="origin"/> 預設以中心縮放。
    /// </summary>
    public static void FadeSlideIn(Control c, string fromTransform = "translateY(-6px)", TimeSpan? duration = null,
        RelativePoint? origin = null)
    {
        var st = States.GetOrCreateValue(c);
        var gen = ++st.Generation;
        if (origin is { } o) c.RenderTransformOrigin = o;
        EnsureFadeSlide(c, duration ?? Base, Enter);
        c.Opacity = 0;
        c.RenderTransform = TransformOperations.Parse(fromTransform);
        Dispatcher.UIThread.Post(() =>
        {
            if (st.Generation != gen) return; // 期間又被叫去做別的事
            c.Opacity = 1;
            c.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 退場：淡出到 <paramref name="toTransform"/>，播完呼叫 <paramref name="then"/>（真正移除／隱藏）。
    /// 若在播的期間又被 <see cref="FadeSlideIn"/>／<see cref="SetVisible"/> 叫回來，<paramref name="then"/> 不會被執行。
    /// </summary>
    public static void FadeOut(Control c, Action? then = null, string toTransform = "scale(0.96)", TimeSpan? duration = null)
    {
        var st = States.GetOrCreateValue(c);
        var gen = ++st.Generation;
        var d = duration ?? Quick;
        EnsureFadeSlide(c, d, Exit);
        c.Opacity = 0;
        c.RenderTransform = TransformOperations.Parse(toTransform);
        if (then == null) return;
        DispatcherTimer.RunOnce(() =>
        {
            if (st.Generation == gen) then();
        }, d + TimeSpan.FromMilliseconds(10));
    }

    /// <summary>
    /// 顯示／隱藏都帶動畫的 IsVisible：出現＝淡入放大，消失＝淡出縮小後才真的 IsVisible=false。
    /// 已是目標狀態時什麼都不做，所以每幀呼叫也沒關係。
    /// </summary>
    public static void SetVisible(Control c, bool visible, string hiddenTransform = "scale(0.94)")
    {
        var st = States.GetOrCreateValue(c);
        if (st.VisibleTarget == visible) return;
        if (st.VisibleTarget == null && c.IsVisible == visible) { st.VisibleTarget = visible; return; }
        st.VisibleTarget = visible;
        if (visible)
        {
            c.IsVisible = true;
            FadeSlideIn(c, hiddenTransform);
        }
        else
        {
            FadeOut(c, () => c.IsVisible = false, hiddenTransform);
        }
    }

    /// <summary>
    /// 單行工具列那種「只換內容、不能讓版面跳」的顯示切換：
    /// 隱藏立刻生效（不然淡出中會佔位），出現時從下方 4px 淡入。
    /// </summary>
    public static void Reveal(Control c, bool visible)
    {
        if (c.IsVisible == visible) return;
        if (!visible)
        {
            var st = States.GetOrCreateValue(c);
            st.Generation++; // 取消進行中的淡入
            c.IsVisible = false;
            return;
        }
        c.IsVisible = true;
        FadeSlideIn(c, "translateY(4px)");
    }

    /// <summary>
    /// FLIP 補位：控制項已經排到新位置，從舊位置的偏移 (dx, dy) 滑回原位。
    /// 播完會把 transition 拆掉——之後若有人直接設 TranslateTransform（拖曳跟隨），不會被插值成 Identity。
    /// </summary>
    public static void Slide(Control c, double dx, double dy, TimeSpan? duration = null)
    {
        var st = States.GetOrCreateValue(c);
        var gen = ++st.Generation;
        var d = duration ?? Move;
        EnsureFadeSlide(c, d, Enter);
        c.RenderTransform = TransformOperations.Parse(
            FormattableString.Invariant($"translate({dx}px, {dy}px)"));
        Dispatcher.UIThread.Post(() =>
        {
            if (st.Generation != gen) return;
            c.RenderTransform = TransformOperations.Identity;
            DispatcherTimer.RunOnce(() =>
            {
                if (st.Generation == gen) Detach(c);
            }, d + TimeSpan.FromMilliseconds(20));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>拆掉本類加上的 transition（保留樣式來的），RenderTransform 歸零。</summary>
    public static void Detach(Control c)
    {
        if (!States.TryGetValue(c, out var st)) return;
        if (c.Transitions != null && (st.Fade != null || st.Transform != null))
        {
            var list = new Transitions();
            foreach (var t in c.Transitions)
                if (!ReferenceEquals(t, st.Fade) && !ReferenceEquals(t, st.Transform)) list.Add(t);
            c.Transitions = list.Count > 0 ? list : null;
        }
        st.Fade = null;
        st.Transform = null;
        // 只清掉我們自己設的變換；若有人已經接手（拖曳中設了 TranslateTransform）就不要動
        if (c.RenderTransform is TransformOperations) c.RenderTransform = null;
    }

    /// <summary>給沒有樣式 transition 的控制項（Border 之類）加一條顏色過渡。</summary>
    public static void BrushTransition(Control c, AvaloniaProperty<IBrush?> property, TimeSpan? duration = null)
    {
        var list = new Transitions();
        if (c.Transitions != null) foreach (var t in c.Transitions) list.Add(t);
        list.Add(new BrushTransition { Property = property, Duration = duration ?? Base, Easing = Enter });
        c.Transitions = list;
    }

    /// <summary>會平滑改變位置的變換（例如工具面板的選取指示器）：呼叫後之後每次設 RenderTransform 都會滑過去。</summary>
    public static void TrackTransform(Control c, TimeSpan? duration = null)
    {
        var list = new Transitions();
        if (c.Transitions != null) foreach (var t in c.Transitions) list.Add(t);
        list.Add(new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty, Duration = duration ?? Move, Easing = Enter,
        });
        c.Transitions = list;
    }

    public static ITransform Translate(double x, double y) =>
        TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px)"));
}
