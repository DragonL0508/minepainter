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

    /// <summary>滾輪一格的增量。AdaptiveStep 為 true 時改由數值大小決定。</summary>
    public double Step { get; set; } = 1;

    /// <summary>
    /// 滾輪加速：數值越大一格跳越多，且結果會對齊 1/2/5/10 這種整齊的級距，
    /// 不會停在 37、113 之類的隨機數字。
    /// </summary>
    public bool AdaptiveStep { get; set; }

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
        var flyout = new AnimatedFlyout { Content = grid, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };
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
        StepBy(WheelInput.Direction(e), WheelInput.Notches(e));
        e.Handled = true;
    }

    /// <summary>
    /// 走一格（direction：+1 變大、−1 變小）。畫布上的滾輪手勢（Alt + 滾輪＝筆刷大小）
    /// 要動的是工具列上這個框，級距、上下限、通知都該與直接在框上滾一模一樣。
    /// </summary>
    public void StepBy(int direction, int notches = 1)
    {
        if (direction == 0) return;
        var v = _value;
        for (var i = 0; i < Math.Max(1, notches); i++)
            v = AdaptiveStep ? NextAdaptive(v, direction > 0) : v + direction * Step;
        SetAndNotify(v);
    }

    /// <summary>數值越大，一格的級距越大：1、2、5、10、20、50、100…</summary>
    private static double StepFor(double value)
    {
        var v = Math.Abs(value);
        if (v < 10) return 1;
        if (v < 20) return 2;
        if (v < 50) return 5;
        if (v < 100) return 10;
        if (v < 200) return 20;
        if (v < 500) return 50;
        if (v < 1000) return 100;
        return 200;
    }

    /// <summary>往上或往下跳一格，並對齊該級距的整數倍（20 → 25 → 30、20 → 18 → 16）。</summary>
    private static double NextAdaptive(double value, bool up)
    {
        const double eps = 1e-6;
        if (up)
        {
            var step = StepFor(value);
            return Math.Floor(value / step + eps) * step + step;
        }
        else
        {
            // 往下時用「下一段」的級距，才不會 20 一次掉到 15。
            var step = StepFor(value - eps);
            return Math.Ceiling(value / step - eps) * step - step;
        }
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
