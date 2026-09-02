using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Services;

namespace MinePainter.App.Views;

/// <summary>
/// 「設定 → 快捷鍵」：所有指令依分類列出，點一列的按鍵鈕後直接按下新組合鍵重新綁定
/// （Esc 取消、Backspace 清除）。撞到別的指令會自動解除對方並提示。
/// 變更立即生效（MainWindow 與 CanvasView 查同一張表）；呼叫端關窗後 Save。
/// </summary>
public sealed class ShortcutsWindow : ModalDialog
{
    private readonly Dictionary<string, Button> _gestureButtons = new();
    private string? _capturingId;
    private readonly TextBlock _hint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
        Text = "點一下按鍵欄位，然後按下新的組合鍵（Esc 取消、Backspace 清除綁定）。",
    };

    public ShortcutsWindow() : base("快捷鍵", 440)
    {
        var list = new StackPanel { Spacing = 2 };
        string? lastCategory = null;
        foreach (var def in ShortcutMap.Defs)
        {
            if (def.Category != lastCategory)
            {
                lastCategory = def.Category;
                list.Children.Add(new TextBlock
                {
                    Text = def.Category,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 3),
                });
            }
            list.Children.Add(BuildRow(def));
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 460,
            Content = list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var resetButton = new Button { Content = "全部重設", FontSize = 12, Padding = new Thickness(14, 6) };
        resetButton.Click += (_, _) =>
        {
            _capturingId = null;
            ShortcutMap.ResetAll();
            RefreshAllButtons();
            _hint.Text = "已全部重設為預設值。";
        };

        var body = new StackPanel { Spacing = 8, Children = { scroll, _hint } };

        var closeButton = MakeButton("關閉", primary: true);
        SetBody(body, new DockPanel
        {
            Children =
            {
                Docked(resetButton, Dock.Left),
                Docked(closeButton, Dock.Right),
            },
        });

        static Control Docked(Control c, Dock dock)
        {
            DockPanel.SetDock(c, dock);
            c.HorizontalAlignment = dock == Dock.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            return c;
        }
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

    private void BeginCapture(string id)
    {
        // 點另一列會先放掉上一個
        if (_capturingId != null) RefreshButton(_capturingId);
        _capturingId = id;
        _gestureButtons[id].Content = "按下組合鍵…";
        _hint.Text = "按下新的組合鍵；Esc 取消、Backspace 清除綁定。";
        Focus(); // 焦點離開按鈕，Space/Enter 才能被當成新綁定捕捉，而不是觸發按鈕
    }

    private void RefreshButton(string id) =>
        _gestureButtons[id].Content = ShortcutMap.GetGesture(id)?.ToString() ?? "—";

    private void RefreshAllButtons()
    {
        foreach (var id in _gestureButtons.Keys) RefreshButton(id);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_capturingId == null)
        {
            base.OnKeyDown(e); // 一般狀態：Esc 關窗等預設行為
            return;
        }

        e.Handled = true;
        var id = _capturingId;

        switch (e.Key)
        {
            case Key.Escape:
                _capturingId = null;
                RefreshButton(id);
                _hint.Text = "已取消。";
                return;

            case Key.Back:
                _capturingId = null;
                ShortcutMap.SetGesture(id, null);
                RefreshButton(id);
                _hint.Text = "已清除綁定。";
                return;

            // 純修飾鍵：等實際按鍵一起來
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return;
        }

        _capturingId = null;
        var gesture = new KeyGesture(ShortcutMap.NormalizeKey(e.Key), e.KeyModifiers);
        var displaced = ShortcutMap.SetGesture(id, gesture);
        RefreshAllButtons(); // 撞到的那列也要更新

        var defName = ShortcutMap.Defs.First(d => d.Id == id).Name;
        _hint.Text = displaced != null
            ? $"「{defName}」已綁定 {gesture}；原本用這組鍵的「{displaced.Name}」已解除。"
            : $"「{defName}」已綁定 {gesture}。";
    }
}
