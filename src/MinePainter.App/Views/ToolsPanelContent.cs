using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;

namespace MinePainter.App.Views;

/// <summary>paint.net 式直條工具面板：雙欄 icon-only 按鈕。</summary>
public sealed class ToolsPanelContent : UserControl
{
    private static readonly (string Key, MaterialIconKind Icon, string Tip)[] Tools =
    [
        ("rectselect", MaterialIconKind.Select, "矩形選取 (S)"),
        ("lasso", MaterialIconKind.Lasso, "套索選取 (L)"),
        ("wand", MaterialIconKind.AutoFix, "魔術棒 (W)"),
        ("move", MaterialIconKind.CursorMove, "移動 (M)"),
        ("brush", MaterialIconKind.Brush, "筆刷 (B)"),
        ("eraser", MaterialIconKind.Eraser, "橡皮擦 (E)"),
        ("fill", MaterialIconKind.FormatColorFill, "油漆桶 (F)"),
        ("eyedropper", MaterialIconKind.Eyedropper, "滴管 (I)"),
        ("text", MaterialIconKind.FormatText, "文字 (T)"),
        ("shape", MaterialIconKind.ShapeOutline, "形狀 (O)"),
    ];

    private readonly Dictionary<string, ToggleButton> _buttons = new();
    private bool _suppress;

    public event Action<string>? ToolSelected;

    public ToolsPanelContent()
    {
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (key, icon, tip) in Tools)
        {
            var button = new ToggleButton
            {
                Content = new MaterialIcon { Kind = icon, Width = 18, Height = 18 },
                Width = 34,
                Height = 30,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, tip);

            var captured = key;
            button.IsCheckedChanged += (_, _) =>
            {
                if (_suppress) return;
                if (button.IsChecked != true)
                {
                    // 不允許取消目前工具：點已選中的按鈕維持選中
                    _suppress = true;
                    button.IsChecked = true;
                    _suppress = false;
                    return;
                }
                ToolSelected?.Invoke(captured);
            };
            _buttons[key] = button;
            grid.Children.Add(button);
        }
        Content = grid;
    }

    public void SetActive(string key)
    {
        _suppress = true;
        foreach (var (k, b) in _buttons)
            b.IsChecked = k == key;
        _suppress = false;
    }
}
