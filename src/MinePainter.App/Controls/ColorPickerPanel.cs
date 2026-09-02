using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SkiaSharp;

namespace MinePainter.App.Controls;

/// <summary>
/// 選色面板（效果參數的顏色用）：色輪（色相／飽和度）＋明度＋不透明度＋十六進位，
/// 上方一列「最近使用」＋色票格（灰階一欄、12 色相各六階）—— 白／黑／純色一點就到，
/// 不必在色輪中心跟明度滑桿之間來回摸。
/// 拖曳中發 <see cref="Changed"/>（即時預覽），放開發 <see cref="Committed"/>。
/// </summary>
public sealed class ColorPickerPanel : StackPanel
{
    private readonly ColorWheel _wheel = new();
    private readonly BarSlider _value = new() { Minimum = 0, Maximum = 100, Label = "明度", Suffix = "%", Height = 20 };
    private readonly BarSlider _alpha = new() { Minimum = 0, Maximum = 100, Label = "不透明度", Suffix = "%", Height = 20 };
    private readonly TextBox _hex = new() { FontSize = 12, Width = 96, Height = 22, Padding = new Thickness(6, 2) };
    private readonly Border _swatch = new()
    {
        Width = 40, Height = 22, CornerRadius = new CornerRadius(3),
        BorderBrush = AppTheme.SeparatorBrush, BorderThickness = new Thickness(1),
    };
    private readonly Button[] _recentButtons = new Button[Views.PalettePanelContent.SwatchColumns];
    private bool _suppress;
    private SKColor _color = SKColors.Black;

    private const double Cell = 15; // 13 欄 × 15 = 195，塞得進 200 寬的面板

    public SKColor Color
    {
        get => _color;
        set
        {
            _color = value;
            SyncFromColor();
        }
    }

    public event Action<SKColor>? Changed;
    public event Action<SKColor>? Committed;

    public ColorPickerPanel()
    {
        Spacing = 6;
        Width = 200;

        _wheel.HueSatChanged += () =>
        {
            if (_suppress) return;
            if (_value.Value < 1) { _suppress = true; _value.Value = 100; _suppress = false; } // 黑色上轉色輪要看得到顏色
            Compose(commit: false);
        };
        _wheel.PointerReleased += (_, _) => { if (!_suppress) Committed?.Invoke(_color); };
        _value.ValueChanged += _ => { if (!_suppress) Compose(commit: false); };
        _value.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(_color); };
        _alpha.ValueChanged += _ => { if (!_suppress) Compose(commit: false); };
        _alpha.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(_color); };
        _hex.LostFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            ApplyHex();
            e.Handled = true;
        };

        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _swatch, new TextBlock { Text = "#", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, _hex },
        };
        Children.Add(BuildRecentRow());
        Children.Add(BuildSwatchGrid());
        Children.Add(_wheel);
        Children.Add(_value);
        Children.Add(_alpha);
        Children.Add(hexRow);
        Committed += c => Views.PalettePanelContent.RememberRecent(c);
        SyncFromColor();
    }

    // ---- 色票（點一下直接選色，保留目前的不透明度）----

    private Button SwatchButton(SKColor? color)
    {
        var btn = new Button
        {
            Width = Cell - 2,
            Height = Cell - 2,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = AppTheme.SeparatorBrush,
            Background = color is { } c
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(c.Red, c.Green, c.Blue))
                : AppTheme.InnerBrush,
            IsEnabled = color != null,
        };
        if (color is { } sc)
        {
            ToolTip.SetTip(btn, "#" + ToHex(sc));
            btn.Click += (_, _) => PickSwatch(sc);
        }
        return btn;
    }

    private void PickSwatch(SKColor rgb)
    {
        Color = rgb.WithAlpha(_color.Alpha);
        Changed?.Invoke(_color);
        Committed?.Invoke(_color);
    }

    private Control BuildRecentRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < _recentButtons.Length; i++)
        {
            _recentButtons[i] = SwatchButton(null);
            row.Children.Add(_recentButtons[i]);
        }
        RefreshRecent();
        // 面板可能被 flyout 反覆開關；掛在附加／卸離上才不會漏事件或洩漏
        AttachedToVisualTree += (_, _) => { Views.PalettePanelContent.RecentColorsChanged += RefreshRecent; RefreshRecent(); };
        DetachedFromVisualTree += (_, _) => Views.PalettePanelContent.RecentColorsChanged -= RefreshRecent;
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "最近使用", FontSize = 10, Foreground = AppTheme.TextMutedBrush },
                row,
            },
        };
    }

    private void RefreshRecent()
    {
        var recent = Views.PalettePanelContent.RecentColors;
        for (var i = 0; i < _recentButtons.Length; i++)
        {
            var old = _recentButtons[i];
            var fresh = SwatchButton(i < recent.Count ? recent[i] : null);
            if (old.Parent is Panel panel)
            {
                var idx = panel.Children.IndexOf(old);
                panel.Children[idx] = fresh;
            }
            _recentButtons[i] = fresh;
        }
    }

    /// <summary>精簡色票：兩列 × 13 欄 —— 上列灰階（黑→白）、下列純色相；跟最近使用列同寬同欄。</summary>
    private Control BuildSwatchGrid()
    {
        var grid = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Width = Cell * Views.PalettePanelContent.SwatchColumns,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var n = Views.PalettePanelContent.SwatchColumns;
        for (var i = 0; i < n; i++)
        {
            var v = (byte)Math.Round(255.0 * i / (n - 1));
            grid.Children.Add(SwatchButton(new SKColor(v, v, v)));
        }
        for (var i = 0; i < n; i++)
            grid.Children.Add(SwatchButton(SKColor.FromHsv(360f * i / n, 100f, 100f)));
        return grid;
    }

    private void Compose(bool commit)
    {
        var c = SKColor.FromHsv((float)_wheel.Hue, (float)(_wheel.Saturation * 100), (float)_value.Value)
            .WithAlpha((byte)Math.Round(_alpha.Value * 2.55));
        _color = c;
        _suppress = true;
        _hex.Text = ToHex(c);
        _swatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        _suppress = false;
        Changed?.Invoke(c);
        if (commit) Committed?.Invoke(c);
    }

    private void ApplyHex()
    {
        if (_suppress || !TryParseHex(_hex.Text, out var c)) return;
        Color = c;
        Changed?.Invoke(c);
        Committed?.Invoke(c);
    }

    private void SyncFromColor()
    {
        _suppress = true;
        _color.ToHsv(out var h, out var s, out var v);
        _wheel.Hue = h;
        _wheel.Saturation = s / 100.0;
        _value.Value = v;
        _alpha.Value = _color.Alpha / 2.55;
        _hex.Text = ToHex(_color);
        _swatch.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(_color.Alpha, _color.Red, _color.Green, _color.Blue));
        _suppress = false;
    }

    public static string ToHex(SKColor c) =>
        c.Alpha == 255 ? $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}" : $"{c.Alpha:X2}{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    public static bool TryParseHex(string? text, out SKColor color)
    {
        color = default;
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length != 6 && t.Length != 8) return false;
        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        if (t.Length == 6) v |= 0xFF000000;
        color = new SKColor(v);
        return true;
    }
}
