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
        ("bgeraser", MaterialIconKind.EraserVariant, "去背筆 (Shift+E)：擦掉與筆刷中心相近的顏色，物件留下"),
        ("fill", MaterialIconKind.FormatColorFill, "油漆桶 (F)"),
        ("eyedropper", MaterialIconKind.Eyedropper, "滴管 (I)"),
        ("text", MaterialIconKind.FormatText, "文字 (T)"),
        ("shape", MaterialIconKind.ShapeOutline, "形狀 (O)"),
    ];

    private readonly Dictionary<string, ToggleButton> _buttons = new();
    private readonly Panel _host;
    private readonly Border _indicator;
    private string? _activeKey;
    private Point _lastIndicatorPos = new(double.NaN, double.NaN);
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

        // 選取指示器：一塊半透明的主題色墊在按鈕底下，切換工具時滑到新按鈕（Motion.Move）。
        // 按鈕自己的選中底色是 160ms 淡入，兩者疊起來就是「高亮從舊工具流到新工具」。
        _indicator = new Border
        {
            Width = 34,
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = AppTheme.AccentBrush,
            Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        Controls.Motion.TrackTransform(_indicator);
        _host = new Panel { Children = { _indicator, grid } };
        _host.LayoutUpdated += (_, _) => PlaceIndicator(animate: false);
        Content = _host;
    }

    /// <summary>把指示器放到目前工具的按鈕底下；第一次（還沒顯示）直接就位不播動畫。</summary>
    private void PlaceIndicator(bool animate)
    {
        if (_activeKey == null || !_buttons.TryGetValue(_activeKey, out var button)) return;
        if (button.Bounds.Width <= 0) return; // 尚未排版
        if (button.TranslatePoint(new Point(0, 0), _host) is not { } p) return;
        if (_indicator.IsVisible && p == _lastIndicatorPos) return; // 每次 layout 都會來，位置沒變就別打斷進行中的滑動
        _lastIndicatorPos = p;
        var target = Controls.Motion.Translate(p.X, p.Y);
        if (!_indicator.IsVisible || !animate)
        {
            var saved = _indicator.Transitions;
            _indicator.Transitions = null;
            _indicator.RenderTransform = target;
            _indicator.Transitions = saved;
            _indicator.IsVisible = true;
            return;
        }
        _indicator.RenderTransform = target;
    }

    public void SetActive(string key)
    {
        _suppress = true;
        foreach (var (k, b) in _buttons)
            b.IsChecked = k == key;
        _suppress = false;
        var moved = _activeKey != key;
        _activeKey = key;
        PlaceIndicator(animate: moved);
    }
}
