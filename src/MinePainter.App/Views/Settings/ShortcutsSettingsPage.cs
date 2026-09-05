using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Services;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → 快捷鍵：所有指令依分類列出，每個指令兩格（主鍵／副鍵）——
/// 點一格的按鍵鈕後直接按下新組合鍵重新綁定（Esc 取消、Backspace 清除）。
/// 撞到別的指令會自動解除對方並提示。
///
/// 最後一段是「滾輪」：滾輪手勢錄不進按鍵表（沒有 Key 可以填），所以另外一張表
/// （<see cref="WheelMap"/>），綁定方式是「按下按鈕之後直接在上面滾一下滑鼠」。
///
/// 上方搜尋框可依名稱／分類／按鍵過濾（同 VS Code 的快捷鍵頁）。
/// 變更立即生效（MainWindow 與 CanvasView 查同一張表）；設定視窗關窗後 Save。
/// </summary>
public sealed class ShortcutsSettingsPage : SettingsPage
{
    public override string Description =>
        "點一列右邊的按鍵鈕，然後直接按下新的組合鍵；每個指令可以綁兩組。最下面是滾輪手勢。";

    private readonly Dictionary<(string Id, int Slot), Button> _gestureButtons = new();
    private readonly List<(ShortcutDef Def, Control Row)> _rows = [];
    private readonly List<(string Category, Control Header)> _headers = [];
    private readonly TextBox _search = new()
    {
        Watermark = "搜尋指令、分類或按鍵…",
        FontSize = 12,
        Height = 28,
        Padding = new Thickness(8, 3),
    };
    private readonly TextBlock _hint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
        Text = "",
    };

    private readonly Dictionary<string, Button> _wheelButtons = new();
    private readonly List<(WheelDef Def, Control Row)> _wheelRows = [];

    private (string Id, int Slot)? _capturing;

    /// <summary>正在等使用者滾一下滑鼠的滾輪動作 id（null = 沒在錄）。</summary>
    private string? _capturingWheel;

    private const string WheelCategory = "滾輪";

    public ShortcutsSettingsPage()
    {
        var list = new StackPanel { Spacing = 2 };
        string? lastCategory = null;
        foreach (var def in ShortcutMap.Defs)
        {
            if (def.Category != lastCategory)
            {
                lastCategory = def.Category;
                var header = new TextBlock
                {
                    Text = def.Category,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 3),
                };
                _headers.Add((def.Category, header));
                list.Children.Add(header);
            }

            var row = BuildRow(def);
            _rows.Add((def, row));
            list.Children.Add(row);
        }

        // ---- 滾輪 ----
        var wheelHeader = new TextBlock
        {
            Text = WheelCategory,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 10, 0, 3),
        };
        _headers.Add((WheelCategory, wheelHeader));
        list.Children.Add(wheelHeader);

        var wheelNote = SettingsUi.Hint("按下按鈕之後，壓著想要的修飾鍵在按鈕上滾一下滑鼠即可綁定。");
        wheelNote.Margin = new Thickness(0, 0, 0, 4);
        _headers.Add((WheelCategory, wheelNote));
        list.Children.Add(wheelNote);

        foreach (var def in WheelMap.Defs)
        {
            var row = BuildWheelRow(def);
            _wheelRows.Add((def, row));
            list.Children.Add(row);
        }

        _search.TextChanged += (_, _) => ApplyFilter();

        var resetButton = new Button { Content = "全部重設", FontSize = 12, Padding = new Thickness(14, 6) };
        resetButton.Click += (_, _) =>
        {
            _capturing = null;
            _capturingWheel = null;
            ShortcutMap.ResetAll();
            WheelMap.ResetAll();
            RefreshAllButtons();
            ApplyFilter(); // 重設後按鍵字串變了，過濾結果跟著更新
            _hint.Text = "已全部重設為預設值。";
        };

        // 搜尋框釘在上面、提示與「全部重設」釘在下面，中間那段清單才捲
        _search.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_search, Dock.Top);

        var footer = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                _hint,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Children = { resetButton },
                },
            },
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        Content = new DockPanel
        {
            Children = { _search, footer, SettingsUi.Scroll(list) },
        };

        AddHandler(PointerWheelChangedEvent, OnWheelTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private Control BuildRow(ShortcutDef def)
    {
        var name = new TextBlock
        {
            Text = def.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var slot = 0; slot < ShortcutMap.Slots; slot++)
        {
            var captured = slot;
            var button = new Button
            {
                FontSize = 11,
                MinWidth = 116,
                Height = 24,
                Padding = new Thickness(8, 2),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 空的副鍵看起來要像「可以加一組」，不是壞掉
                [ToolTip.TipProperty] = captured == 0 ? "主鍵" : "副鍵（第二組，可留空）",
            };
            button.Click += (_, _) => BeginCapture(def.Id, captured);
            _gestureButtons[(def.Id, captured)] = button;
            buttons.Children.Add(button);
            RefreshButton(def.Id, captured);
        }

        DockPanel.SetDock(buttons, Dock.Right);
        return new DockPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            Children = { buttons, name },
        };
    }

    private Control BuildWheelRow(WheelDef def)
    {
        var name = new TextBlock
        {
            Text = def.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = def.Hint,
        };

        var button = new Button
        {
            FontSize = 11,
            MinWidth = 236, // 與上面兩格按鍵鈕（116×2 + 間距）對齊
            Height = 24,
            Padding = new Thickness(8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = def.Hint,
        };
        button.Click += (_, _) => BeginWheelCapture(def.Id);
        _wheelButtons[def.Id] = button;
        RefreshWheelButton(def.Id);

        DockPanel.SetDock(button, Dock.Right);
        return new DockPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            Children = { button, name },
        };
    }

    private void BeginWheelCapture(string id)
    {
        if (_capturing is { } previous) RefreshButton(previous.Id, previous.Slot);
        if (_capturingWheel != null) RefreshWheelButton(_capturingWheel);
        _capturing = null;
        _capturingWheel = id;
        _wheelButtons[id].Content = "在這裡滾一下滑鼠…";
        _hint.Text = "壓著想要的修飾鍵（可以都不壓）在按鈕上滾一下；Esc 取消、Backspace 取消綁定。";
    }

    /// <summary>
    /// 錄製中的滾動＝這次的修飾鍵就是新綁定。
    ///
    /// 走 Tunnel（由外往內）而不是覆寫 OnPointerWheelChanged：按鈕外面就是清單的
    /// ScrollViewer，冒泡的話會先被它吃掉去捲畫面，這裡永遠收不到。
    /// </summary>
    private void OnWheelTunnel(object? sender, PointerWheelEventArgs e)
    {
        if (_capturingWheel is not { } id) return;

        _capturingWheel = null;
        var displaced = WheelMap.Set(id, e.KeyModifiers);
        RefreshAllWheelButtons();
        e.Handled = true;

        var name = WheelMap.Defs.First(d => d.Id == id).Name;
        var gesture = WheelMap.Describe(e.KeyModifiers);
        _hint.Text = displaced != null
            ? $"「{name}」已綁定 {gesture}；原本用這組的「{displaced.Name}」已取消綁定。"
            : $"「{name}」已綁定 {gesture}。";
    }

    private void RefreshWheelButton(string id) =>
        _wheelButtons[id].Content = WheelMap.Describe(WheelMap.Get(id));

    private void RefreshAllWheelButtons()
    {
        foreach (var id in _wheelButtons.Keys) RefreshWheelButton(id);
    }

    /// <summary>依搜尋字串顯示／隱藏列；一個分類底下全被濾掉時連標題一起收起來。</summary>
    private void ApplyFilter()
    {
        var query = (_search.Text ?? "").Trim();
        var visibleCategories = new HashSet<string>();

        foreach (var (def, row) in _rows)
        {
            var match = query.Length == 0
                || def.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || def.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || GestureText(def.Id, 0).Contains(query, StringComparison.OrdinalIgnoreCase)
                || GestureText(def.Id, 1).Contains(query, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = match;
            if (match) visibleCategories.Add(def.Category);
        }

        foreach (var (def, row) in _wheelRows)
        {
            var match = query.Length == 0
                || def.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || WheelCategory.Contains(query, StringComparison.OrdinalIgnoreCase)
                || WheelMap.Describe(WheelMap.Get(def.Id)).Contains(query, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = match;
            if (match) visibleCategories.Add(WheelCategory);
        }

        foreach (var (category, header) in _headers)
            header.IsVisible = visibleCategories.Contains(category);
    }

    private void BeginCapture(string id, int slot)
    {
        // 點另一格會先放掉上一個
        if (_capturing is { } previous) RefreshButton(previous.Id, previous.Slot);
        if (_capturingWheel != null)
        {
            RefreshWheelButton(_capturingWheel);
            _capturingWheel = null;
        }
        _capturing = (id, slot);
        _gestureButtons[(id, slot)].Content = "按下組合鍵…";
        _hint.Text = "按下新的組合鍵；Esc 取消、Backspace 清除綁定。";
        // 焦點離開按鈕（也離開搜尋框），Space/Enter 才會被當成新綁定捕捉，而不是觸發按鈕
        (TopLevel.GetTopLevel(this) as Window)?.Focus();
    }

    private static string GestureText(string id, int slot) =>
        ShortcutMap.GetGesture(id, slot)?.ToString() ?? "";

    private void RefreshButton(string id, int slot)
    {
        var text = GestureText(id, slot);
        _gestureButtons[(id, slot)].Content = text.Length > 0 ? text : slot == 0 ? "—" : "＋";
    }

    private void RefreshAllButtons()
    {
        foreach (var (id, slot) in _gestureButtons.Keys) RefreshButton(id, slot);
    }

    public override bool HandleKeyDown(KeyEventArgs e)
    {
        // 滾輪錄製中：只認 Esc（取消）與 Backspace（取消綁定），其餘等滾輪
        if (_capturingWheel is { } wheelId)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _capturingWheel = null;
                    RefreshWheelButton(wheelId);
                    _hint.Text = "已取消。";
                    return true;
                case Key.Back:
                    _capturingWheel = null;
                    WheelMap.Set(wheelId, null);
                    RefreshWheelButton(wheelId);
                    _hint.Text = "已取消綁定。";
                    return true;
            }
            return true;
        }

        if (_capturing is not { } capturing)
        {
            // 沒在錄鍵時，Esc 先用來清搜尋（有東西可清才攔，不然照常關窗）
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = "";
                return true;
            }
            return false;
        }

        var (id, slot) = capturing;

        switch (e.Key)
        {
            case Key.Escape:
                _capturing = null;
                RefreshButton(id, slot);
                _hint.Text = "已取消。";
                return true;

            case Key.Back:
                _capturing = null;
                ShortcutMap.SetGesture(id, slot, null);
                RefreshButton(id, slot);
                _hint.Text = "已清除綁定。";
                return true;

            // 純修飾鍵：等實際按鍵一起來
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return true;
        }

        _capturing = null;
        var gesture = new KeyGesture(ShortcutMap.NormalizeKey(e.Key), e.KeyModifiers);
        var displaced = ShortcutMap.SetGesture(id, slot, gesture);
        RefreshAllButtons(); // 撞到的那格也要更新

        var defName = ShortcutMap.Defs.First(d => d.Id == id).Name;
        var which = slot == 0 ? "主鍵" : "副鍵";
        _hint.Text = displaced != null
            ? $"「{defName}」的{which}已綁定 {gesture}；原本用這組鍵的「{displaced.Name}」已解除。"
            : $"「{defName}」的{which}已綁定 {gesture}。";
        return true;
    }
}
