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

    /// <summary>常用值清單（逗號分隔，例如 "1,2,4,8,16"）：設了就在右側多一顆 ▾，點開直接選。</summary>
    public string? Presets
    {
        get => _presets;
        set
        {
            _presets = value;
            _presetButton.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }
    private string? _presets;
    private readonly Button _presetButton;

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
        _presetButton = new Button
        {
            Content = "▾",
            FontSize = 10,
            Width = 16,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 0, 0),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsVisible = false,
        };
        ToolTip.SetTip(_presetButton, "常用值");
        _presetButton.Click += (_, _) => ShowPresets();
        Content = new DockPanel
        {
            Children = { _presetButton, _text },
        };
        DockPanel.SetDock(_presetButton, Avalonia.Controls.Dock.Right);
    }

    /// <summary>常用值選單：4 欄的格子，點一下就套用。</summary>
    private void ShowPresets()
    {
        var values = new List<double>();
        foreach (var part in (_presets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                values.Add(Math.Clamp(v, Minimum, Maximum));
        if (values.Count == 0) return;

        var grid = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Width = 4 * 44 };
        var flyout = new Flyout { Content = grid, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };
        foreach (var v in values)
        {
            var value = v;
            var b = new Button
            {
                Content = value.ToString("0"),
                Width = 40,
                Height = 24,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                FontSize = 12,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontWeight = Math.Abs(value - _value) < 0.0001 ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
            };
            b.Click += (_, _) => { SetAndNotify(value); flyout.Hide(); };
            grid.Children.Add(b);
        }
        flyout.ShowAt(this);
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
