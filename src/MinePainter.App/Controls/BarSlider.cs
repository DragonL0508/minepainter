using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MinePainter.Core.Effects;

namespace MinePainter.App.Controls;

/// <summary>
/// paint.net 式厚長條滑桿：數值以填色比例 + 文字顯示在條內，整條都是 hitbox。
/// 拖曳/點擊/滾輪皆可調整；DragCompleted 供「即時預覽 + 放開才進 history」模式使用。
/// </summary>
public sealed class BarSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<BarSlider, double>(nameof(Minimum), 0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<BarSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<BarSlider, double>(nameof(Value), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: CoerceValue);

    public static readonly StyledProperty<string> SuffixProperty =
        AvaloniaProperty.Register<BarSlider, string>(nameof(Suffix), "");

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<BarSlider, string?>(nameof(Label));

    public static readonly StyledProperty<int> DecimalsProperty =
        AvaloniaProperty.Register<BarSlider, int>(nameof(Decimals), 0);

    public static readonly StyledProperty<SliderTrack> TrackProperty =
        AvaloniaProperty.Register<BarSlider, SliderTrack>(nameof(Track), SliderTrack.None);

    /// <summary>
    /// 填滿條實際畫到的值。拖曳時緊跟 Value；其他來源（雙擊回預設、常用值、滾輪、undo、切工具）
    /// 用 Motion.Base 滑過去——條會「跑」到新位置，使用者看得出值是從哪裡變到哪裡。文字永遠顯示真正的 Value。
    /// </summary>
    private static readonly StyledProperty<double> ShownValueProperty =
        AvaloniaProperty.Register<BarSlider, double>("ShownValue");

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>顯示在數值後的單位（例如 "%"、"px"）。</summary>
    public string Suffix { get => GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    /// <summary>顯示在條內左側的名稱（可省）。</summary>
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>數值顯示的小數位數（0 = 整數；同時決定滾輪步進）。</summary>
    public int Decimals { get => GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    /// <summary>條底部的視覺軌（色相環／黑白漸層），提示數值意義。</summary>
    public SliderTrack Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>
    /// 雙擊左鍵要回到的值。**全專案的拉條都有這個行為**（使用者 2026-09-04 明示要一致）：
    /// XAML 建立的自動以標記上寫的 Value 為預設（見 <see cref="EndInit"/>），
    /// 程式建立的請在建構時指定 —— 沒指定就會是 0，多半不是你要的。
    /// </summary>
    public double? DefaultValue
    {
        get => _defaultValue;
        set
        {
            _defaultValue = value;
            ApplyResetTip();
        }
    }

    private double? _defaultValue;

    public event Action<double>? ValueChanged;
    public event Action<double>? DragCompleted;

    private bool _dragging;

    private static readonly IBrush TrackBrush = AppTheme.BarTrackBrush;
    private static readonly IBrush FillBrush = AppTheme.FillBrush;
    private static readonly IBrush FillHoverBrush = AppTheme.FillHoverBrush;
    private static readonly IPen BorderPen = new Pen(AppTheme.SeparatorBrush);

    /// <summary>白色填滿上的字（黑）。與底條的對比由 BarTrack（亮色主題加深的底條）負責。</summary>
    private static readonly IBrush FillTextBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A));
    private bool _hover;

    /// <summary>
    /// XAML 建立時：標記上寫的初始值就是「預設值」（EndInit 時屬性都已套上、還沒被程式改過）。
    /// 這樣新增拉條不會忘了給預設值 —— 忘了給就是不一致的來源。
    /// </summary>
    public override void EndInit()
    {
        base.EndInit();
        _defaultValue ??= Value;
        ApplyResetTip();
    }

    private string? _autoTip;

    /// <summary>沒有自己的提示時，補一句「雙擊重設為 X」讓這個行為看得見（預設值改了就跟著更新）。</summary>
    private void ApplyResetTip()
    {
        if (_defaultValue is not { } def) return;
        var existing = ToolTip.GetTip(this);
        if (existing != null && !ReferenceEquals(existing, _autoTip)) return; // 呼叫端自己設了提示，尊重它
        _autoTip = $"雙擊重設為 {FormatValue(def)}";
        ToolTip.SetTip(this, _autoTip);
    }

    private string FormatValue(double v) =>
        v.ToString(Decimals > 0 ? "F" + Decimals : "0") + Suffix;

    static BarSlider()
    {
        AffectsRender<BarSlider>(MinimumProperty, MaximumProperty, ValueProperty, SuffixProperty, LabelProperty, DecimalsProperty, TrackProperty, ShownValueProperty);
        ValueProperty.Changed.AddClassHandler<BarSlider>((s, _) => s.SyncShown());
    }

    private readonly Transitions _shownTransitions =
    [
        new Avalonia.Animation.DoubleTransition { Property = ShownValueProperty, Duration = Motion.Base, Easing = Motion.Enter },
    ];

    private void SyncShown()
    {
        if (_dragging)
        {
            // 拖曳中不要有延遲：暫時拆掉 transition 直接設
            Transitions = null;
            SetValue(ShownValueProperty, Value);
            Transitions = _shownTransitions;
        }
        else SetValue(ShownValueProperty, Value);
    }

    public BarSlider()
    {
        Height = 24;
        MinWidth = 70;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Transitions = _shownTransitions;
        SetValue(ShownValueProperty, Value);
    }

    // 自繪內容用到 Theme brush：換主題要主動重繪（掛/卸時訂閱，避免 static 事件洩漏）
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AppTheme.Changed -= InvalidateVisual;
    }

    private static double CoerceValue(AvaloniaObject o, double v)
    {
        var s = (BarSlider)o;
        return Math.Clamp(v, s.Minimum, s.Maximum);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var radius = 3.0;
        context.DrawRectangle(TrackBrush, BorderPen, rect, radius, radius);

        var range = Maximum - Minimum;
        var shown = Math.Clamp(GetValue(ShownValueProperty), Minimum, Maximum);
        var t = range <= 0 ? 0 : (shown - Minimum) / range;
        var fillWidth = Math.Max(0, rect.Width * t);
        if (fillWidth > 0.5)
        {
            using (context.PushClip(new RoundedRect(rect, radius)))
            {
                context.FillRectangle(_hover || _dragging ? FillHoverBrush : FillBrush,
                    new Rect(0, 0, fillWidth, rect.Height));
            }
        }

        // 視覺軌：條底 4px 的漸層帶（色相環／黑白）
        if (Track != SliderTrack.None && rect.Width > 8)
        {
            var strip = new Rect(rect.Left + 3, rect.Bottom - 5, rect.Width - 6, 3);
            using (context.PushClip(new RoundedRect(rect, radius)))
                context.FillRectangle(TrackGradient(Track), strip, 1.5f);
        }

        // 白底黑字：落在填滿區內的字畫黑色；未填滿區維持主題文字色
        //（深色主題的底條是深色，黑字會看不見）—— 同一段字各畫一次、以填滿邊界互補裁切
        var value = new FormattedText(
            Value.ToString(Decimals > 0 ? "F" + Decimals : "0") + Suffix,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            FillTextBrush);
        var valuePos = new Point(rect.Width - value.Width - 8, (rect.Height - value.Height) / 2);

        FormattedText? label = null;
        var labelPos = default(Point);
        if (!string.IsNullOrEmpty(Label))
        {
            label = new FormattedText(
                Label,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                FillTextBrush);
            labelPos = new Point(8, (rect.Height - label.Height) / 2);
        }

        using (context.PushClip(new Rect(0, 0, fillWidth, rect.Height)))
        {
            context.DrawText(value, valuePos);
            if (label != null) context.DrawText(label, labelPos);
        }

        value.SetForegroundBrush(AppTheme.TextBrush);
        label?.SetForegroundBrush(AppTheme.TextBrush);
        using (context.PushClip(new Rect(fillWidth, 0, Math.Max(0, rect.Width - fillWidth), rect.Height)))
        {
            context.DrawText(value, valuePos);
            if (label != null) context.DrawText(label, labelPos);
        }
    }

    private static readonly Dictionary<SliderTrack, IBrush> TrackBrushes = new();

    private static IBrush TrackGradient(SliderTrack track)
    {
        if (TrackBrushes.TryGetValue(track, out var cached)) return cached;
        var stops = new Avalonia.Media.GradientStops();
        switch (track)
        {
            case SliderTrack.Hue:
                // -180..180：兩端都是青色（180°），中間 0° 是紅
                for (var i = 0; i <= 12; i++)
                {
                    var deg = -180 + i * 30;
                    var hsv = HsvToRgb(((deg % 360) + 360) % 360, 0.85, 0.95);
                    stops.Add(new Avalonia.Media.GradientStop(hsv, i / 12.0));
                }
                break;
            case SliderTrack.Gray:
                stops.Add(new Avalonia.Media.GradientStop(Colors.Black, 0));
                stops.Add(new Avalonia.Media.GradientStop(Colors.White, 1));
                break;
            default:
                stops.Add(new Avalonia.Media.GradientStop(Color.FromRgb(0x20, 0x20, 0x20), 0));
                stops.Add(new Avalonia.Media.GradientStop(Color.FromRgb(0x80, 0x80, 0x80), 0.5));
                stops.Add(new Avalonia.Media.GradientStop(Color.FromRgb(0xF0, 0xF0, 0xF0), 1));
                break;
        }
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = stops,
        };
        TrackBrushes[track] = brush;
        return brush;
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            // 右鍵＝直接輸入數值（拉條很難拉到精確的小數值時用）
            ShowInputFlyout();
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        // 雙擊＝回到預設值（第一下已經把值拉到指標處，這一下要蓋掉它）
        if (e.ClickCount == 2 && DefaultValue is { } def)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            SetAndNotify(def);
            DragCompleted?.Invoke(Value);
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        ApplyPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        ApplyPointer(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        DragCompleted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = Decimals > 0 ? Math.Pow(10, -Decimals) : Math.Max(1, (Maximum - Minimum) / 100);
        SetAndNotify(Value + WheelInput.Direction(e) * WheelInput.Notches(e) * step); // 往下滾＝變大
        DragCompleted?.Invoke(Value);
        e.Handled = true;
    }

    /// <summary>右鍵輸入數值：小視窗一個文字框，Enter 套用（夾在範圍內）、Esc 關閉。</summary>
    private void ShowInputFlyout()
    {
        var box = new TextBox
        {
            Text = Value.ToString(Decimals > 0 ? "F" + Decimals : "0"),
            Width = 90,
            FontSize = 12,
            Padding = new Thickness(6, 2),
        };
        var hint = new TextBlock
        {
            Text = $"{Minimum.ToString(Decimals > 0 ? "F" + Decimals : "0")} ～ {Maximum.ToString(Decimals > 0 ? "F" + Decimals : "0")}{Suffix}",
            FontSize = 10,
            Foreground = AppTheme.TextMutedBrush,
        };
        var panel = new StackPanel { Spacing = 4, Children = { box, hint } };
        if (!string.IsNullOrEmpty(Label))
            panel.Children.Insert(0, new TextBlock { Text = Label, FontSize = 12 });
        var flyout = new AnimatedFlyout { Content = panel, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };

        void Apply()
        {
            if (double.TryParse(box.Text?.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                SetAndNotify(v);
                DragCompleted?.Invoke(Value);
            }
            flyout.Hide();
        }
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { Apply(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { flyout.Hide(); ke.Handled = true; }
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        flyout.ShowAt(this);
    }

    private void ApplyPointer(Point p)
    {
        var t = Bounds.Width <= 0 ? 0 : Math.Clamp(p.X / Bounds.Width, 0, 1);
        SetAndNotify(Minimum + t * (Maximum - Minimum));
    }

    private void SetAndNotify(double value)
    {
        var clamped = Math.Clamp(value, Minimum, Maximum);
        if (Math.Abs(clamped - Value) < 0.0001) return;
        SetCurrentValue(ValueProperty, clamped);
        ValueChanged?.Invoke(clamped);
    }
}
