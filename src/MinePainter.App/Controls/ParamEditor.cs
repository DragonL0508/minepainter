using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using MinePainter.Core.Effects;

namespace MinePainter.App.Controls;

/// <summary>
/// 由 <see cref="ParamDef"/> 描述長出參數控制項（效果對話框與調整圖層屬性共用）。
/// 目標是不可變物件：每次改動以 With 產生新實例存進 <see cref="Current"/>，
/// 拖曳中發 <see cref="Changed"/>（即時預覽），放開時發 <see cref="Committed"/>（進 history）。
/// </summary>
public sealed class ParamEditor : StackPanel
{
    private readonly Func<object, IReadOnlyList<ParamDef>> _defs;
    private bool _suppress;

    /// <summary>「全新的同型別物件」，用來取得各參數的預設值（滑桿雙擊回預設）。建不出來就是 null。</summary>
    private readonly object? _defaults;

    public object Current { get; private set; }

    /// <summary>曲線編輯器背景的直方圖（可省）。</summary>
    public long[]? Histogram { get; set; }

    /// <summary>選點器底圖：來源範圍縮圖（可省）。</summary>
    public Bitmap? Thumbnail { get; set; }

    public event Action<object>? Changed;
    public event Action<object>? Committed;

    public ParamEditor(object target, Func<object, IReadOnlyList<ParamDef>> defs, long[]? histogram = null)
    {
        Spacing = 6;
        Current = target;
        _defs = defs;
        _defaults = TryCreateDefaults(target);
        Histogram = histogram;
        Rebuild();
    }

    /// <summary>外部換了目標（undo 之類）時同步。</summary>
    public void SetTarget(object target)
    {
        Current = target;
        Rebuild();
    }

    /// <summary>
    /// 參數的預設值（效果／調整都是「不可變 record + 預設值寫在屬性初始式」，
    /// 所以建一個全新的同型別實例讀回來就是預設值）。建不出來（例如缺無參數建構式）回 null。
    /// </summary>
    private static object? TryCreateDefaults(object target)
    {
        try
        {
            // 調整效果包了一層 IAdjustment，要換的是裡面那個
            if (target is AdjustmentEffect adj)
            {
                var inner = Activator.CreateInstance(adj.Adjustment.GetType());
                return inner is MinePainter.Core.Adjustments.IAdjustment a ? new AdjustmentEffect(a) : null;
            }
            return Activator.CreateInstance(target.GetType());
        }
        catch
        {
            return null; // 沒有預設值可回：雙擊就不做事
        }
    }

    private double? DefaultOf(Func<object, double> get)
    {
        if (_defaults == null) return null;
        try
        {
            return get(_defaults);
        }
        catch
        {
            return null;
        }
    }

    private void Update(object next, bool commit)
    {
        if (_suppress) return;
        Current = next;
        Changed?.Invoke(Current);
        if (commit) Committed?.Invoke(Current);
    }

    private void Rebuild()
    {
        _suppress = true;
        Children.Clear();
        foreach (var def in _defs(Current))
        {
            switch (def)
            {
                case SliderParam s:
                    Children.Add(BuildSlider(s));
                    break;
                case BoolParam b:
                    Children.Add(BuildBool(b));
                    break;
                case ChoiceParam c:
                    Children.Add(BuildChoice(c));
                    break;
                case CurvesParam cv:
                    Children.Add(BuildCurves(cv));
                    break;
                case AngleParam an:
                    Children.Add(BuildAngle(an));
                    break;
                case PointParam pt:
                    Children.Add(BuildPoint(pt));
                    break;
                case ColorParam col:
                    Children.Add(BuildColor(col));
                    break;
                case GradientParam gr:
                    Children.Add(BuildGradient(gr));
                    break;
            }
        }
        _suppress = false;
    }

    private Control BuildSlider(SliderParam s)
    {
        var bar = new BarSlider
        {
            Label = s.Label,
            Minimum = s.Min,
            Maximum = s.Max,
            Decimals = s.Decimals,
            Suffix = s.Suffix,
            Track = s.Track,
            Height = 22,
            Value = s.Get(Current),
        };
        bar.ValueChanged += v =>
        {
            if (_suppress) return;
            Update(s.With(Current, v), commit: false);
            // 連動參數（例如色調分離的「連動」）：其他滑桿要跟著動
            SyncSliders();
        };
        bar.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(Current); };
        bar.Tag = s;
        SetResettable(bar, DefaultOf(s.Get), s.Decimals);
        if (!s.IsSeed) return bar;

        // 亂數種子：加一顆「重新產生」骰子（paint.net 的 Reseed）
        var dice = new Button
        {
            Content = "🎲",
            FontSize = 13,
            Padding = new Thickness(6, 0),
            Height = 22,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        ToolTip.SetTip(dice, "重新產生");
        var rng = new Random();
        dice.Click += (_, _) =>
        {
            if (_suppress) return;
            var v = Math.Round(s.Min + rng.NextDouble() * (s.Max - s.Min));
            Update(s.With(Current, v), commit: true);
            SyncSliders();
        };
        DockPanel.SetDock(dice, Dock.Right);
        return new DockPanel { Children = { dice, bar } };
    }

    private Control BuildAngle(AngleParam an)
    {
        var dial = new AngleDial { Minimum = an.Min, Maximum = an.Max, Value = an.Get(Current) };
        var bar = new BarSlider
        {
            Label = an.Label,
            Minimum = an.Min,
            Maximum = an.Max,
            Suffix = "°",
            Height = 22,
            Value = an.Get(Current),
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetResettable(bar, DefaultOf(an.Get), 0);
        dial.ValueChanged += v =>
        {
            if (_suppress) return;
            _suppress = true;
            bar.Value = v;
            _suppress = false;
            Update(an.With(Current, v), commit: false);
        };
        dial.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(Current); };
        bar.ValueChanged += v =>
        {
            if (_suppress) return;
            _suppress = true;
            dial.Value = v;
            _suppress = false;
            Update(an.With(Current, v), commit: false);
        };
        bar.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(Current); };

        DockPanel.SetDock(dial, Dock.Left);
        dial.Margin = new Thickness(0, 0, 8, 0);
        return new DockPanel { Children = { dial, bar } };
    }

    /// <summary>滑桿雙擊回預設值（paint.net 的「重設」；沒有預設值可回時不掛）。</summary>
    private static void SetResettable(BarSlider bar, double? def, int decimals)
    {
        if (def == null) return;
        bar.DefaultValue = def;
        ToolTip.SetTip(bar, $"雙擊重設為 {def.Value.ToString(decimals > 0 ? "F" + decimals : "0")}{bar.Suffix}");
    }

    private Control BuildColor(ColorParam col)
    {
        var current = col.Get(Current);
        var swatch = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(1),
            Background = ToBrush(current),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        var hexText = new TextBlock
        {
            Text = "#" + ColorPickerPanel.ToHex(current),
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        ToolTip.SetTip(swatch, "點一下選色");

        // 點色票 → 彈出選色面板（色輪／明度／不透明度／hex），拖曳中即時預覽
        var picker = new ColorPickerPanel { Color = current, Margin = new Thickness(4) };
        var flyout = new AnimatedFlyout { Content = picker, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };
        picker.Changed += c =>
        {
            if (_suppress) return;
            swatch.Background = ToBrush(c);
            hexText.Text = "#" + ColorPickerPanel.ToHex(c);
            Update(col.With(Current, c), commit: false);
        };
        picker.Committed += _ => { if (!_suppress) Committed?.Invoke(Current); };
        swatch.PointerPressed += (_, e) =>
        {
            picker.Color = col.Get(Current);
            flyout.ShowAt(swatch);
            e.Handled = true;
        };

        var label = new TextBlock { Text = col.Label, FontSize = 12, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { label, swatch, hexText } };
    }

    /// <summary>
    /// 多節點漸層：漸層條＋節點標記；下面一列是「選中節點」的顏色色票、位置滑桿，
    /// 以及刪除／反轉／平均分佈。新增節點＝點漸層條空白處；刪除＝右鍵標記或往下拖出去。
    /// </summary>
    private Control BuildGradient(GradientParam gr)
    {
        var editor = new GradientEditor { Stops = gr.Get(Current) };

        var swatch = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(1),
            Background = ToBrush(editor.SelectedStop.Color),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(swatch, "選中節點的顏色（點一下選色）");
        var picker = new ColorPickerPanel { Color = editor.SelectedStop.Color, Margin = new Thickness(4) };
        var flyout = new AnimatedFlyout { Content = picker, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Transient };
        picker.Changed += c =>
        {
            if (_suppress) return;
            swatch.Background = ToBrush(c);
            editor.SetSelectedColor(c, commit: false);
        };
        picker.Committed += _ => { if (!_suppress) Committed?.Invoke(Current); };
        void OpenPicker()
        {
            picker.Color = editor.SelectedStop.Color;
            flyout.ShowAt(swatch);
        }
        swatch.PointerPressed += (_, e) => { OpenPicker(); e.Handled = true; };
        editor.StopActivated += _ => OpenPicker();

        var position = new BarSlider
        {
            Label = "位置",
            Minimum = 0,
            Maximum = 100,
            Suffix = "%",
            Height = 22,
            Value = editor.SelectedStop.Position * 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        position.ValueChanged += v => { if (!_suppress) editor.SetSelectedPosition((float)(v / 100), commit: false); };
        position.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(Current); };

        Button SmallButton(string text, string tip)
        {
            var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 2), Height = 22, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            return b;
        }
        var remove = SmallButton("刪除", "刪除選中的節點（至少留兩個）");
        remove.Click += (_, _) => editor.RemoveSelected();
        var reverse = SmallButton("反轉", "顛倒漸層方向");
        reverse.Click += (_, _) => editor.Reverse();
        var distribute = SmallButton("平均", "節點平均分佈");
        distribute.Click += (_, _) => editor.Distribute();

        void SyncSelected()
        {
            _suppress = true;
            swatch.Background = ToBrush(editor.SelectedStop.Color);
            position.Value = editor.SelectedStop.Position * 100;
            remove.IsEnabled = editor.Stops.Count > 2;
            _suppress = false;
        }
        editor.SelectionChanged += _ => SyncSelected();
        editor.Changed += stops =>
        {
            if (_suppress) return;
            Update(gr.With(Current, stops), commit: false);
            SyncSelected();
        };
        editor.Committed += _ => { if (!_suppress) Committed?.Invoke(Current); };
        SyncSelected();

        var label = new TextBlock { Text = gr.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(remove);
        buttons.Children.Add(reverse);
        buttons.Children.Add(distribute);
        DockPanel.SetDock(swatch, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        position.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(swatch);
        row.Children.Add(buttons);
        row.Children.Add(position);

        return new StackPanel { Spacing = 3, Children = { label, editor, row } };
    }

    private static Avalonia.Media.IBrush ToBrush(SkiaSharp.SKColor c) =>
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    private Control BuildPoint(PointParam pt)
    {
        var picker = new PointPicker { Thumbnail = Thumbnail, Value = pt.Get(Current) };
        picker.ValueChanged += v => { if (!_suppress) Update(pt.With(Current, v), commit: false); };
        picker.DragCompleted += _ => { if (!_suppress) Committed?.Invoke(Current); };

        var label = new TextBlock
        {
            Text = pt.Label + "（雙擊回中心）",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
        };
        return new StackPanel { Spacing = 3, Children = { label, picker } };
    }

    private void SyncSliders()
    {
        _suppress = true;
        foreach (var child in Children)
        {
            if (child is BarSlider bar && bar.Tag is SliderParam def)
            {
                var v = def.Get(Current);
                if (Math.Abs(bar.Value - v) > 1e-6) bar.Value = v;
            }
        }
        _suppress = false;
    }

    private Control BuildBool(BoolParam b)
    {
        var box = new CheckBox { Content = b.Label, FontSize = 12, IsChecked = b.Get(Current) };
        box.IsCheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            Update(b.With(Current, box.IsChecked == true), commit: true);
        };
        return box;
    }

    private Control BuildChoice(ChoiceParam c)
    {
        var combo = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var o in c.Options) combo.Items.Add(o);
        combo.SelectedIndex = Math.Clamp(c.Get(Current), 0, c.Options.Length - 1);
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppress || combo.SelectedIndex < 0) return;
            Update(c.With(Current, combo.SelectedIndex), commit: true);
            Rebuild(); // 選項可能改變其他參數的形狀（例如曲線的通道數）
        };
        var label = new TextBlock
        {
            Text = c.Label,
            FontSize = 12,
            Width = 64,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(label, Dock.Left);
        return new DockPanel { Children = { label, combo } };
    }

    private Control BuildCurves(CurvesParam cv)
    {
        var editor = new CurveEditor { Histogram = Histogram, Curves = cv.Get(Current) };
        var panel = new StackPanel { Spacing = 4 };

        var channels = editor.Curves.Count == 3 ? new[] { "紅", "綠", "藍" } : cv.Channels;
        if (channels.Length > 1)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var buttons = new List<ToggleButton>();
            for (var i = 0; i < channels.Length; i++)
            {
                var index = i;
                var b = new ToggleButton { Content = channels[i], FontSize = 12, Padding = new Thickness(10, 3), IsChecked = i == 0 };
                b.IsCheckedChanged += (_, _) =>
                {
                    if (b.IsChecked != true) return;
                    editor.ActiveChannel = index;
                    foreach (var other in buttons) if (!ReferenceEquals(other, b)) other.IsChecked = false;
                };
                buttons.Add(b);
                row.Children.Add(b);
            }
            panel.Children.Add(row);
        }

        panel.Children.Add(editor);

        var reset = new Button { Content = "重設", FontSize = 12, Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Right };
        reset.Click += (_, _) => editor.ResetActive();
        panel.Children.Add(reset);

        editor.Changed += () => { if (!_suppress) Update(cv.With(Current, editor.Curves), commit: false); };
        editor.Committed += () => { if (!_suppress) Committed?.Invoke(Current); };
        return panel;
    }
}
