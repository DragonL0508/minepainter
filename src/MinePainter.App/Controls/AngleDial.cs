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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
