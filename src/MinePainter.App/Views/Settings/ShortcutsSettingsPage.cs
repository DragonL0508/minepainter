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
/// 上方搜尋框可依指令名稱／分類／按鍵過濾（同 VS Code 的快捷鍵頁）。
/// 變更立即生效（MainWindow 與 CanvasView 查同一張表）；設定視窗關窗後 Save。
/// </summary>
public sealed class ShortcutsSettingsPage : SettingsPage
{
    public override string Description => "點一列右邊的按鍵鈕，然後直接按下新的組合鍵。每個指令可以綁兩組鍵。";

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

    private (string Id, int Slot)? _capturing;

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

        _search.TextChanged += (_, _) => ApplyFilter();

        var resetButton = new Button { Content = "全部重設", FontSize = 12, Padding = new Thickness(14, 6) };
        resetButton.Click += (_, _) =>
        {
            _capturing = null;
            ShortcutMap.ResetAll();
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

        foreach (var (category, header) in _headers)
            header.IsVisible = visibleCategories.Contains(category);
    }

    private void BeginCapture(string id, int slot)
    {
        // 點另一格會先放掉上一個
        if (_capturing is { } previous) RefreshButton(previous.Id, previous.Slot);
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
