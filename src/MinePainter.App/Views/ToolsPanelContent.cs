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
    /// <summary>
    /// 工具依用途分組（組間畫一條分隔線），每組內成對排在同一列（面板是雙欄）：
    /// 選取與移動 → 手繪 → 填色取色 → 形狀與物件。
    /// 鋼筆排在移動旁邊（第一組的第六格）：它畫出來的路徑 Enter 就變成選取範圍，
    /// 用途比較靠選取那一組，也剛好把第一組原本空著的格子補滿。
    /// </summary>
    private static readonly (string Key, MaterialIconKind Icon, string Tip)[][] Groups =
    [
        [
            ("rectselect", MaterialIconKind.Select, "矩形選取 (S)：Shift 加選、Alt（或 Ctrl）減選、兩個一起按＝交集"),
            ("ellipseselect", MaterialIconKind.SelectionEllipse, "橢圓選取 (C)：拖出外接矩形；Shift 加選、Alt（或 Ctrl）減選"),
            ("lasso", MaterialIconKind.Lasso, "套索選取 (L)：Shift 加選、Alt（或 Ctrl）減選"),
            ("wand", MaterialIconKind.AutoFix, "魔術棒 (W)：Shift 加選、Alt（或 Ctrl）減選"),
            ("move", MaterialIconKind.CursorMove, "移動 (M)"),
            ("pen", MaterialIconKind.VectorBezier, "鋼筆 (P)：點一下加角點、按住拖曳拉出曲線、點回起點封閉；Enter 轉為選取、Backspace 退一點、Esc 清除"),
        ],
        [
            ("brush", MaterialIconKind.Brush, "筆刷 (B)：柔邊、抗鋸齒"),
            ("pencil", MaterialIconKind.Pencil, "鉛筆 (N)：硬邊、無抗鋸齒的方形筆尖（像素繪圖）"),
            ("eraser", MaterialIconKind.Eraser, "橡皮擦 (E)"),
            ("bgeraser", MaterialIconKind.EraserVariant, "去背筆 (Shift+E)：擦掉與筆刷中心相近的顏色，物件留下"),
        ],
        [
            ("fill", MaterialIconKind.FormatColorFill, "油漆桶 (F)"),
            ("eyedropper", MaterialIconKind.Eyedropper, "滴管 (I)"),
        ],
        [
            ("shape", MaterialIconKind.ShapeOutline, "形狀 (O)：矩形／橢圓"),
            ("line", MaterialIconKind.VectorLine, "直線 (U)：拖曳畫線，Shift 吸附 15°"),
            ("text", MaterialIconKind.FormatText, "文字 (T)"),
        ],
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
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        for (var g = 0; g < Groups.Length; g++)
        {
            if (g > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(4, 3),
                    Background = AppTheme.BorderBrush,
                    Opacity = 0.6,
                });
            }

            var grid = new UniformGrid { Columns = 2 };
            foreach (var (key, icon, tip) in Groups[g]) grid.Children.Add(BuildButton(key, icon, tip));
            stack.Children.Add(grid);
        }

        // 選取指示器：一塊半透明的主題色墊在按鈕底下，切換工具時滑到新按鈕（Motion.Move）。
        // 按鈕自己的選中底色是 160ms 淡入，兩者疊起來就是「高亮從舊工具流到新工具」。
        _indicator = new Border
        {
            Width = 34,
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = AppTheme.AccentBrush,
            BorderBrush = AppTheme.AccentBrush,
            BorderThickness = new Thickness(1),
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            IsVisible = false,
        };
        Controls.Motion.TrackTransform(_indicator);
        _host = new Panel { Children = { _indicator, stack } };
        _host.LayoutUpdated += (_, _) => PlaceIndicator(animate: false);
        Content = _host;
    }

    /// <summary>一顆工具鈕（icon-only；選中底色交給指示器）。</summary>
    private ToggleButton BuildButton(string key, MaterialIconKind icon, string tip)
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
        // 選中底色交給底下的指示器（Animations.axaml 把 .tool 的 checked 底色設成透明）——
        // 兩者疊在一起時，按鈕自己的不透明底色會把指示器整塊蓋住，只剩邊緣漏出一條藍線
        button.Classes.Add("tool");

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
            ToolSelected?.Invoke(key);
        };
        _buttons[key] = button;
        return button;
    }

    /// <summary>把指示器放到目前工具的按鈕底下；第一次（還沒顯示）直接就位不播動畫。</summary>
    private void PlaceIndicator(bool animate)
    {
        if (_activeKey == null || !_buttons.TryGetValue(_activeKey, out var button)) return;
        if (button.Bounds.Width <= 0) return; // 尚未排版
        if (button.TranslatePoint(new Point(0, 0), _host) is not { } raw) return;
        // 按鈕在 43px 寬的格子裡置中，位置是 x.5 的小數：按鈕本身會貼齊像素，
        // 指示器走 RenderTransform 平移不會 —— 不取整就會偏半格、邊緣糊成一條線
        var p = new Point(Math.Round(raw.X), Math.Round(raw.Y));
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
