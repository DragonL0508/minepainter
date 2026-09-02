using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 浮動調色盤（paint.net 的「色彩」視窗那種寬版）：
/// 左邊色輪、右邊目前色＋Hex＋明度／RGB 滑桿；下方「最近使用」一列（跨次啟動保留），
/// 再下面是 13 欄的色票格（第一欄灰階、其餘 12 個色相各六階深淺）。
/// </summary>
public sealed class PalettePanelContent : UserControl
{
    /// <summary>色票格欄數（也是最近使用列的格數）。</summary>
    private const int Columns = 13;
    private const double Cell = 24;

    private readonly ColorWheel _wheel;
    private readonly BarSlider _valueBar;
    private readonly Border _current;
    private readonly BarSlider _r;
    private readonly BarSlider _g;
    private readonly BarSlider _b;
    private readonly TextBox _hex;
    private readonly Button[] _recentButtons = new Button[Columns];
    private bool _suppress;
    private SKColor _color = SKColors.Black;

    public event Action<SKColor>? ColorSelected;

    /// <summary>
    /// 顏色調整結束（放開滑鼠、滾輪、輸入 Hex）。
    /// 拖色輪／RGB 滑桿會連發上百次 <see cref="ColorSelected"/>，這個事件才是「一步」的界線
    /// （用來把連續調整合併成單一步 undo）。
    /// </summary>
    public event Action<SKColor>? ColorCommitted;

    /// <summary>所有調色盤實例共用的「最近使用」清單（工具列與進階文字視窗的色票鈕都是同一份）。</summary>
    private static readonly List<SKColor> Recent = LoadRecent();
    private static event Action? RecentChanged;

    public PalettePanelContent()
    {
        // 子控制項（色輪／滑桿／色票鈕）自己的 class handler 先跑完才冒泡到這裡，
        // 所以這時 _color 已經是這一下的最終值。
        AddHandler(PointerReleasedEvent, (_, _) => Commit(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => Commit(), RoutingStrategies.Bubble, handledEventsToo: true);

        _wheel = new ColorWheel { VerticalAlignment = VerticalAlignment.Top };
        _wheel.HueSatChanged += OnWheelChanged;

        _valueBar = new BarSlider { Minimum = 0, Maximum = 100, Value = 0, Label = "明度", Suffix = "%", Height = 20 };
        _valueBar.ValueChanged += _ => OnValueBarChanged();

        _current = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(4),
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
        };

        _hex = new TextBox { FontSize = 12, Text = "000000", MinWidth = 0, Height = 28 };
        _hex.LostFocus += (_, _) => ApplyHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyHex();
        };

        _r = MakeChannel("R");
        _g = MakeChannel("G");
        _b = MakeChannel("B");

        // 右欄只有 ~126px：色票 40 + 8 + Hex 欄 76（標籤在上、輸入框在下，六碼才放得下）
        var hexLabel = new TextBlock { Text = "Hex", FontSize = 11, Foreground = AppTheme.TextMutedBrush };
        _hex.Padding = new Thickness(6, 4);
        var hexRow = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { hexLabel, _hex },
        };
        DockPanel.SetDock(_current, Dock.Left);
        _current.Width = 40;
        _current.Height = 40;
        _current.Margin = new Thickness(0, 2, 8, 0);
        _current.VerticalAlignment = VerticalAlignment.Top;

        var right = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                new DockPanel { Children = { _current, hexRow } },
                _valueBar, _r, _g, _b,
            },
        };
        DockPanel.SetDock(_wheel, Dock.Left);
        var top = new DockPanel { Children = { _wheel, right } };

        // 最近使用：固定 13 格，空格畫成暗底
        var recentRow = new WrapPanel { ItemWidth = Cell, ItemHeight = Cell };
        for (var i = 0; i < Columns; i++)
        {
            var slot = i;
            var button = SwatchButton(null);
            button.Click += (_, _) =>
            {
                if (slot < Recent.Count) SetColorInternal(Recent[slot], notify: true);
            };
            _recentButtons[i] = button;
            recentRow.Children.Add(button);
        }

        var swatches = new WrapPanel { ItemWidth = Cell, ItemHeight = Cell };
        foreach (var color in BuildSwatchColors())
        {
            var swatch = SwatchButton(color);
            var captured = color;
            swatch.Click += (_, _) => SetColorInternal(captured, notify: true);
            swatches.Children.Add(swatch);
        }

        Content = new StackPanel
        {
            Width = Columns * Cell,
            Spacing = 6,
            Children =
            {
                top,
                SectionLabel("最近使用"),
                recentRow,
                SectionLabel("色票"),
                swatches,
            },
        };

        RecentChanged += RefreshRecent;
        RefreshRecent();
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        Margin = new Thickness(1, 4, 0, -2),
    };

    private static Button SwatchButton(SKColor? color) => new()
    {
        Width = Cell - 2,
        Height = Cell - 2,
        Margin = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(0),
        BorderThickness = new Thickness(1),
        BorderBrush = AppTheme.SeparatorBrush,
        Background = color is { } c ? new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)) : AppTheme.InnerBrush,
    };

    private BarSlider MakeChannel(string label)
    {
        var bar = new BarSlider { Minimum = 0, Maximum = 255, Label = label, Height = 20 };
        bar.ValueChanged += _ => OnChannelChanged();
        return bar;
    }

    /// <summary>
    /// 色票：6 列 × 13 欄。第一欄灰階（黑→白），其餘 12 欄是 30° 一階的色相，
    /// 由上而下 深 → 純 → 淡（paint.net 的預設調色盤也是同一種「色相 × 深淺」排法）。
    /// </summary>
    private static IEnumerable<SKColor> BuildSwatchColors()
    {
        (float S, float V)[] rows = [(1f, 0.45f), (1f, 0.72f), (1f, 1f), (0.68f, 1f), (0.42f, 1f), (0.18f, 1f)];
        byte[] grays = [0x00, 0x33, 0x66, 0x99, 0xCC, 0xFF];
        for (var row = 0; row < rows.Length; row++)
        {
            yield return new SKColor(grays[row], grays[row], grays[row]);
            for (var hue = 0; hue < 12; hue++)
                yield return SKColor.FromHsv(hue * 30f, rows[row].S * 100f, rows[row].V * 100f);
        }
    }

    // ---- 最近使用 ----

    private const int RecentMax = Columns;

    private static List<SKColor> LoadRecent()
    {
        var list = new List<SKColor>();
        foreach (var hex in AppSettings.Instance.RecentColors)
        {
            if (hex.Length == 6 &&
                uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            {
                list.Add(new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            }
        }
        return list.Take(RecentMax).ToList();
    }

    /// <summary>記一筆最近使用（去重、最新在最前、最多一列）；同一色連續用不重複記。</summary>
    public static void RememberRecent(SKColor color)
    {
        var rgb = new SKColor(color.Red, color.Green, color.Blue);
        if (Recent.Count > 0 && Recent[0] == rgb) return;
        Recent.RemoveAll(c => c == rgb);
        Recent.Insert(0, rgb);
        if (Recent.Count > RecentMax) Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);

        AppSettings.Instance.RecentColors = Recent.Select(c => $"{c.Red:X2}{c.Green:X2}{c.Blue:X2}").ToList();
        AppSettings.Instance.Save();
        RecentChanged?.Invoke();
    }

    private void RefreshRecent()
    {
        for (var i = 0; i < Columns; i++)
        {
            var button = _recentButtons[i];
            if (i < Recent.Count)
            {
                var c = Recent[i];
                button.Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
                ToolTip.SetTip(button, $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}");
            }
            else
            {
                button.Background = AppTheme.InnerBrush;
                ToolTip.SetTip(button, null);
            }
        }
    }

    private void Commit()
    {
        RememberRecent(_color);
        ColorCommitted?.Invoke(_color);
    }

    // ---- 顏色 ↔ 控制項 ----

    private void OnWheelChanged()
    {
        if (_suppress) return;

        // 明度 0（純黑）時點色輪看不到任何變化 → 自動拉到 100%。
        // 只在「動色輪」時這麼做；明度滑桿自己往 0 拉是使用者要黑色，不能跳回去。
        if (_valueBar.Value < 1)
        {
            _suppress = true;
            _valueBar.Value = 100;
            _suppress = false;
        }
        ApplyHsv();
    }

    private void OnValueBarChanged()
    {
        if (_suppress) return;
        ApplyHsv();
    }

    private void ApplyHsv()
    {
        var color = SKColor.FromHsv((float)_wheel.Hue, (float)(_wheel.Saturation * 100), (float)_valueBar.Value);
        SetColorInternal(color, notify: true, syncWheel: false);
    }

    private void OnChannelChanged()
    {
        if (_suppress) return;
        SetColorInternal(new SKColor((byte)_r.Value, (byte)_g.Value, (byte)_b.Value), notify: true);
    }

    private void ApplyHex()
    {
        var text = (_hex.Text ?? "").Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            SetColorInternal(new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v), notify: true);
            Commit();
        }
        else
        {
            SetColorInternal(_color, notify: false); // 還原顯示
        }
    }

    /// <summary>外部（滴管等）同步顏色進來，不回發事件。</summary>
    public void SetColor(SKColor color) => SetColorInternal(color, notify: false);

    private void SetColorInternal(SKColor color, bool notify, bool syncWheel = true)
    {
        _color = color;
        _suppress = true;
        _r.Value = color.Red;
        _g.Value = color.Green;
        _b.Value = color.Blue;
        _hex.Text = $"{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        _current.Background = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        if (syncWheel)
        {
            color.ToHsv(out var h, out var s, out var v);
            _wheel.Hue = h;
            _wheel.Saturation = s / 100.0;
            _valueBar.Value = v;
        }
        _suppress = false;
        if (notify) ColorSelected?.Invoke(color);
    }
}
