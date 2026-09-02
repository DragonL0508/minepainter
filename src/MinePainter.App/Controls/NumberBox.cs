using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MinePainter.App.Controls;

/// <summary>
/// paint.net 式數值輸入框：直接打字，滑鼠停在上面滾輪即可增減。
/// </summary>
public sealed class NumberBox : UserControl
{
    private readonly TextBox _text;
    private double _value;
    private bool _suppress;

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;

    /// <summary>滾輪一格的增量。</summary>
    public double Step { get; set; } = 1;

    public event Action<double>? ValueChanged;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(clamped - _value) < 0.0001) return;
            _value = clamped;
            _suppress = true;
            _text.Text = clamped.ToString("0");
            _suppress = false;
        }
    }

    public NumberBox()
    {
        _text = new TextBox
        {
            FontSize = 12,
            Text = "0",
            MinWidth = 0,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(5, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        _text.LostFocus += (_, _) => Parse();
        _text.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Parse();
                e.Handled = true;
            }
        };
        Content = _text;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetAndNotify(_value + Math.Sign(e.Delta.Y) * Step);
        e.Handled = true;
    }

    private void Parse()
    {
        if (_suppress) return;
        if (double.TryParse(_text.Text, out var parsed))
            SetAndNotify(parsed);
        else
            Value = _value; // 還原顯示
    }

    private void SetAndNotify(double value)
    {
        var clamped = Math.Clamp(value, Minimum, Maximum);
        if (Math.Abs(clamped - _value) < 0.0001)
        {
            Value = clamped; // 仍同步文字（打了非法值時）
            return;
        }
        _value = clamped;
        _suppress = true;
        _text.Text = clamped.ToString("0");
        _suppress = false;
        ValueChanged?.Invoke(clamped);
    }
}
