using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MinePainter.App.Controls;

/// <summary>
/// 角度轉盤（paint.net 的 angle chooser）：圓盤 + 指針，拖曳即設定角度；
/// Shift 吸附 15°。0° 指向右、正角度逆時針（數學慣例，與效果的 cos/−sin 一致）。
/// </summary>
public sealed class AngleDial : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AngleDial, double>(nameof(Value), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public double Minimum { get; set; } = -180;
    public double Maximum { get; set; } = 180;

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public event Action<double>? ValueChanged;
    public event Action<double>? DragCompleted;

    /// <summary>雙擊左鍵要回到的值（與拉條同一套約定，見 <see cref="BarSlider.DefaultValue"/>）。</summary>
    public double? DefaultValue { get; set; }

    /// <summary>滾輪一格轉幾度。</summary>
    public double Step { get; set; } = 1;

    private bool _dragging;

    static AngleDial()
    {
        AffectsRender<AngleDial>(ValueProperty);
    }

    public AngleDial()
    {
        Width = 64;
        Height = 64;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2 - 2;

        context.DrawEllipse(AppTheme.InnerBrush, new Pen(AppTheme.SeparatorBrush, 1), c, r, r);

        // 刻度：每 45° 一小段
        var tick = new Pen(AppTheme.SeparatorBrush, 1);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var inner = new Point(c.X + Math.Cos(a) * (r - 5), c.Y - Math.Sin(a) * (r - 5));
            var outer = new Point(c.X + Math.Cos(a) * r, c.Y - Math.Sin(a) * r);
            context.DrawLine(tick, inner, outer);
        }

        var rad = Value * Math.PI / 180;
        var tip = new Point(c.X + Math.Cos(rad) * (r - 3), c.Y - Math.Sin(rad) * (r - 3));
        context.DrawLine(new Pen(AppTheme.AccentBrush, 2, lineCap: PenLineCap.Round), c, tip);
        context.DrawEllipse(AppTheme.AccentBrush, null, c, 3, 3);
        context.DrawEllipse(Brushes.White, new Pen(AppTheme.AccentBrush, 1.5), tip, 3.5, 3.5);
    }

    private void Apply(Point p, KeyModifiers modifiers, bool notify)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var deg = Math.Atan2(-(p.Y - c.Y), p.X - c.X) * 180 / Math.PI;
        if (modifiers.HasFlag(KeyModifiers.Shift)) deg = Math.Round(deg / 15) * 15;

        // 映射到允許範圍（0..360 或 -180..180）
        if (Minimum >= 0 && deg < 0) deg += 360;
        deg = Math.Clamp(deg, Minimum, Maximum);
        if (Math.Abs(deg - Value) < 0.01) return;
        SetCurrentValue(ValueProperty, deg);
        if (notify) ValueChanged?.Invoke(deg);
    }

    /// <summary>設定角度並通知（滾輪／雙擊用；不經過指標位置換算）。</summary>
    private void SetAndNotify(double deg)
    {
        if (Minimum >= 0 && deg < 0) deg += 360;
        deg = Math.Clamp(deg, Minimum, Maximum);
        if (Math.Abs(deg - Value) < 0.001) return;
        SetCurrentValue(ValueProperty, deg);
        ValueChanged?.Invoke(deg);
        DragCompleted?.Invoke(deg);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetAndNotify(Value + WheelInput.Direction(e) * WheelInput.Notches(e) * Step); // 往下滾＝變大
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 雙擊＝回到預設值（第一下已經把指針轉到指標處，這一下要蓋掉它）
        if (e.ClickCount == 2 && DefaultValue is { } def)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            SetAndNotify(def);
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this), e.KeyModifiers, notify: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        Apply(e.GetPosition(this), e.KeyModifiers, notify: true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke(Value);
        e.Handled = true;
    }
}
