using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Services;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → 快捷鍵：所有指令依分類列出，點一列的按鍵鈕後直接按下新組合鍵重新綁定
/// （Esc 取消、Backspace 清除）。撞到別的指令會自動解除對方並提示。
/// 上方搜尋框可依指令名稱／分類／目前按鍵過濾（同 VS Code 的快捷鍵頁）。
/// 變更立即生效（MainWindow 與 CanvasView 查同一張表）；設定視窗關窗後 Save。
/// </summary>
public sealed class ShortcutsSettingsPage : SettingsPage
{
    public override string Description => "點一列右邊的按鍵鈕，然後直接按下新的組合鍵。";

    private readonly Dictionary<string, Button> _gestureButtons = new();
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

    private string? _capturingId;

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
            _capturingId = null;
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

        var button = new Button
        {
            FontSize = 11,
            MinWidth = 130,
            Height = 24,
            Padding = new Thickness(8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => BeginCapture(def.Id);
        _gestureButtons[def.Id] = button;
        RefreshButton(def.Id);

        DockPanel.SetDock(button, Dock.Right);
        return new DockPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            Children = { button, name },
        };
    }

    /// <summary>依搜尋字串顯示／隱藏列；一個分類底下全被濾掉時連標題一起收起來。</summary>
    private void ApplyFilter()
    {
        var query = (_search.Text ?? "").Trim();
        var visibleCategories = new HashSet<string>();

        foreach (var (def, row) in _rows)
        {
            var gesture = ShortcutMap.GetGesture(def.Id)?.ToString() ?? "";
            var match = query.Length == 0
                || def.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || def.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || gesture.Contains(query, StringComparison.OrdinalIgnoreCase);
            row.IsVisible = match;
            if (match) visibleCategories.Add(def.Category);
        }

        foreach (var (category, header) in _headers)
            header.IsVisible = visibleCategories.Contains(category);
    }

    private void BeginCapture(string id)
    {
        // 點另一列會先放掉上一個
        if (_capturingId != null) RefreshButton(_capturingId);
        _capturingId = id;
        _gestureButtons[id].Content = "按下組合鍵…";
        _hint.Text = "按下新的組合鍵；Esc 取消、Backspace 清除綁定。";
        // 焦點離開按鈕（也離開搜尋框），Space/Enter 才會被當成新綁定捕捉，而不是觸發按鈕
        (TopLevel.GetTopLevel(this) as Window)?.Focus();
    }

    private void RefreshButton(string id) =>
        _gestureButtons[id].Content = ShortcutMap.GetGesture(id)?.ToString() ?? "—";

    private void RefreshAllButtons()
    {
        foreach (var id in _gestureButtons.Keys) RefreshButton(id);
    }

    public override bool HandleKeyDown(KeyEventArgs e)
    {
        if (_capturingId == null)
        {
            // 沒在錄鍵時，Esc 先用來清搜尋（有東西可清才攔，不然照常關窗）
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = "";
                return true;
            }
            return false;
        }

        var id = _capturingId;

        switch (e.Key)
        {
            case Key.Escape:
                _capturingId = null;
                RefreshButton(id);
                _hint.Text = "已取消。";
                return true;

            case Key.Back:
                _capturingId = null;
                ShortcutMap.SetGesture(id, null);
                RefreshButton(id);
                _hint.Text = "已清除綁定。";
                return true;

            // 純修飾鍵：等實際按鍵一起來
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return true;
        }

        _capturingId = null;
        var gesture = new KeyGesture(ShortcutMap.NormalizeKey(e.Key), e.KeyModifiers);
        var displaced = ShortcutMap.SetGesture(id, gesture);
        RefreshAllButtons(); // 撞到的那列也要更新

        var defName = ShortcutMap.Defs.First(d => d.Id == id).Name;
        _hint.Text = displaced != null
            ? $"「{defName}」已綁定 {gesture}；原本用這組鍵的「{displaced.Name}」已解除。"
            : $"「{defName}」已綁定 {gesture}。";
        return true;
    }
}
