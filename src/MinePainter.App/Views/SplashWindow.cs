using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MinePainter.App.Views;

/// <summary>
/// 啟動畫面：主視窗還在建構時，螢幕正中央淡入 app icon。
///
/// 設計：無邊框全透明視窗（只有 icon 本體與一圈暖色光暈可見），不搶焦點、不進工作列、永遠在最上層。
/// 進場 = icon 淡入 + 從 0.86 放大到 1（CubicEaseOut）、光暈慢半拍浮現、字標再慢一點從下方浮上；
/// 等待期間光暈緩慢「呼吸」；退場 = 淡出 + 微放大（像被主視窗接走）。
/// icon 是像素圖，一律最近鄰縮放，尺寸取 16 的倍數才會每格一樣寬。
/// </summary>
public sealed class SplashWindow : Window
{
    private const double IconSize = 112; // 16 格 × 7px
    private static readonly TimeSpan IconIn = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan GlowIn = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan NameIn = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan Out = TimeSpan.FromMilliseconds(260);

    private readonly Control _root;
    private readonly Ellipse _glow;
    private readonly Image _icon;
    private readonly TextBlock _name;
    private readonly Stopwatch _shownAt = new();
    private readonly CancellationTokenSource _breathing = new();
    private bool _closing;

    public SplashWindow()
    {
        Width = 300;
        Height = 300;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false; // 不搶焦點：主視窗出來時才是真正的焦點目標
        Focusable = false;
        CanResize = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _glow = new Ellipse
        {
            Width = 250,
            Height = 250,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.Parse("#5CFFD24A"), 0),
                    new GradientStop(Color.Parse("#26FFC61A"), 0.45),
                    new GradientStop(Color.Parse("#00FFC61A"), 1),
                },
            },
            Opacity = 0,
            RenderTransform = Scale(0.7),
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = GlowIn, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = GlowIn, Easing = new CubicEaseOut() },
            ],
        };

        _icon = new Image
        {
            Width = IconSize,
            Height = IconSize,
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://MinePainter.App/Assets/icon.png"))),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 22),
            RenderTransformOrigin = RelativePoint.Center,
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 8, BlurRadius = 26, Color = Color.Parse("#66000000") },
            Opacity = 0,
            RenderTransform = Scale(0.86),
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = IconIn, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = IconIn, Easing = new CubicEaseOut() },
            ],
        };
        RenderOptions.SetBitmapInterpolationMode(_icon, BitmapInterpolationMode.None);

        _name = new TextBlock
        {
            Text = "MinePainter",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 2.4,
            Foreground = new SolidColorBrush(Color.Parse("#F2FFFFFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2.4, IconSize + 14, 0, 0), // 補回 LetterSpacing 造成的右側空白，讓字視覺置中
            Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 1, BlurRadius = 8, Color = Color.Parse("#B0000000") },
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateY(8px)"),
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = NameIn, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = NameIn, Easing = new CubicEaseOut() },
            ],
        };

        _root = new Panel { Children = { _glow, _icon, _name } };
        Content = _root;

        Opened += (_, _) =>
        {
            _shownAt.Start();
            // 起始值先套進去，下一輪 layout 再設目標值 —— 同一幀內設會直接跳到終點
            Dispatcher.UIThread.Post(() =>
            {
                _icon.Opacity = 1;
                _icon.RenderTransform = Scale(1);
                _glow.Opacity = 1;
                _glow.RenderTransform = Scale(1);
            }, DispatcherPriority.Loaded);
            DispatcherTimer.RunOnce(() =>
            {
                if (_closing) return;
                _name.Opacity = 1;
                _name.RenderTransform = TransformOperations.Identity;
            }, TimeSpan.FromMilliseconds(160));
            DispatcherTimer.RunOnce(() =>
            {
                if (!_closing) _ = Breathe(_breathing.Token);
            }, GlowIn);
        };
    }

    /// <summary>已顯示多久（用來保證最短顯示時間，讓進場動畫播得完）。</summary>
    public TimeSpan Elapsed => _shownAt.Elapsed;

    /// <summary>播放退場並關閉；重複呼叫無害。</summary>
    public async Task FadeOutAndCloseAsync()
    {
        if (_closing) return;
        _closing = true;
        _breathing.Cancel(); // 呼吸動畫一取消，光暈就回到 local 值，之後交給 transition 淡出

        foreach (var control in new Control[] { _glow, _icon, _name })
        {
            control.Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = Out, Easing = new CubicEaseIn() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Out, Easing = new CubicEaseIn() },
            ];
        }
        // 取消動畫的還原與 transition 的裝回都要先落地，下一輪再設目標值才會真的淡出
        Dispatcher.UIThread.Post(() =>
        {
            _glow.Opacity = 0;
            _icon.Opacity = 0;
            _name.Opacity = 0;
            _icon.RenderTransform = Scale(1.08);
            _glow.RenderTransform = Scale(1.15);
        }, DispatcherPriority.Loaded);

        await Task.Delay(Out + TimeSpan.FromMilliseconds(60));
        Close();
    }

    /// <summary>光暈緩慢呼吸：1.6 秒一趟，來回交替，直到退場。</summary>
    private async Task Breathe(CancellationToken token)
    {
        _glow.Transitions = null; // 呼吸交給 Animation，transition 留著會跟它打架
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1600),
            Easing = new SineEaseInOut(),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1.0), new Setter(RenderTransformProperty, Scale(1)) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 0.72), new Setter(RenderTransformProperty, Scale(1.07)) } },
            },
        };
        try { await animation.RunAsync(_glow, token); }
        catch (OperationCanceledException) { }
    }

    private static ITransform Scale(double s) =>
        TransformOperations.Parse($"scale({s.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
}
