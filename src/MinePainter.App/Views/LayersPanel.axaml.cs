using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Rendering;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

public partial class LayersPanel : UserControl
{
    private const int ThumbWidth = 48;
    private const int ThumbHeight = 38;

    private EditorSession? _session;
    private bool _suppressUiEvents;
    private LayerPropertiesWindow? _propsWindow;

    /// <summary>圖層結構或選取變化後發出（讓 MainWindow 刷新 undo 選單等）。</summary>
    public event Action? StateChanged;

    /// <summary>一列 = 一個節點；in-place 更新用（可見性/縮圖變了不重建列表，捲動位置才不會跳掉）。</summary>
    private sealed class Row
    {
        public required LayerNode Node;
        public required int Depth;
        public required ListBoxItem Item;
        public required CheckBox Check;
        public required Image Thumb;
        public required TextBlock NameText;
        public required TextBlock Badge;
    }

    private readonly List<Row> _rows = new();
    private LayerNode? _lastActiveNode;
    private bool _skipNextAnimation;

    /// <summary>收起的群組（UI 狀態，不進 Core/History；節點經 undo 回來時收合狀態也還在）。</summary>
    private readonly HashSet<LayerNode> _collapsed = new();

    /// <summary>剛被點開/收起的群組：重建列時箭頭要從舊角度轉過去，而不是直接跳到位。</summary>
    private LayerNode? _justToggledGroup;

    public LayersPanel()
    {
        InitializeComponent();
        BuildActionBar();

        // 效果堆疊在 worker 上算完 → 縮圖要跟著換（縮圖只在 history 變動時重畫，
        // 效果剛套上時快取多半還沒算好，不接這條會一直停在沒效果的樣子）
        LayerEffectRenderer.LayerRendered += OnLayerEffectsRendered;
        _thumbTimer.Tick += (_, _) =>
        {
            _thumbTimer.Stop();
            if (_session == null) return;
            LayerNode[] pending;
            lock (_thumbDirty)
            {
                pending = _thumbDirty.ToArray();
                _thumbDirty.Clear();
            }
            foreach (var node in pending)
            {
                var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
                if (row != null) row.Thumb.Source = LayerThumbnail.Render(_session.Document, node, ThumbWidth, ThumbHeight);
                // 祖先群組的縮圖也含這層
                for (var g = node.Parent; g != null; g = g.Parent)
                {
                    var groupRow = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, g));
                    if (groupRow != null) groupRow.Thumb.Source = LayerThumbnail.Render(_session.Document, g, ThumbWidth, ThumbHeight);
                }
                if (_propsWindow is { } win && ReferenceEquals(win.Node, node)) win.RefreshPreview();
            }
        };

        // 拖曳排序：tunnel 才收得到列上（含 ListBoxItem 內部）的指標事件
        LayerList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        LayerList.AddHandler(PointerCaptureLostEvent, (_, _) => CancelDrag(), RoutingStrategies.Tunnel);
    }

    public void SetSession(EditorSession? session)
    {
        if (_session != null) _session.History.Changed -= OnHistoryChanged;
        _session = session;
        if (_session != null) _session.History.Changed += OnHistoryChanged;
        _propsWindow?.Close();
        _skipNextAnimation = true; // 換文件是整份重建，不是圖層操作，不播動畫
        Refresh();
    }

    private void OnHistoryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private readonly HashSet<LayerNode> _thumbDirty = new();
    private readonly Avalonia.Threading.DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>worker 執行緒：記下哪層算完，UI 端節流 120ms 一次重畫縮圖（連續繪畫時每步都會觸發）。</summary>
    private void OnLayerEffectsRendered(RasterLayer layer)
    {
        if (_session == null || layer.Document != _session.Document) return;
        lock (_thumbDirty) _thumbDirty.Add(layer);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_thumbTimer.IsEnabled) _thumbTimer.Start();
        });
    }

    /// <summary>
    /// 刷新圖層列表（顯示順序：最上層在最前）。
    /// 結構沒變時 in-place 更新既有列（勾選/名稱/縮圖），不清空重建 —— 捲動位置不動。
    /// </summary>
    public void Refresh()
    {
        var session = _session;
        _suppressUiEvents = true;

        var target = new List<(LayerNode Node, int Depth)>();
        if (session != null)
        {
            void Walk(GroupLayer group, int depth)
            {
                for (var i = group.Children.Count - 1; i >= 0; i--)
                {
                    var child = group.Children[i];
                    target.Add((child, depth));
                    if (child is GroupLayer g && !_collapsed.Contains(g)) Walk(g, depth + 1);
                }
            }
            lock (session.Document.SyncRoot) Walk(session.Document.Root, 0);
        }

        var sameStructure = target.Count == _rows.Count;
        if (sameStructure)
        {
            for (var i = 0; i < target.Count; i++)
            {
                if (!ReferenceEquals(target[i].Node, _rows[i].Node) || target[i].Depth != _rows[i].Depth)
                {
                    sameStructure = false;
                    break;
                }
            }
        }

        if (!sameStructure)
        {
            // FLIP 動畫：重建前記下每個節點的列位置，重建排版後讓列從舊位置滑到新位置。
            // 移動/拖曳＝滑動、刪除＝下面的列滑上來補位、新增＝淡入；undo/redo 走同一條路。
            var oldPositions = new Dictionary<LayerNode, Point>();
            var animate = _rows.Count > 0 && !_skipNextAnimation;
            if (animate)
            {
                foreach (var row in _rows)
                {
                    if (row.Item.TranslatePoint(default, LayerList) is { } pt)
                        oldPositions[row.Node] = pt;
                }
            }

            var offset = LayerList.Scroll?.Offset;
            _rows.Clear();
            LayerList.Items.Clear();
            foreach (var (node, depth) in target)
            {
                var row = BuildRow(node, depth);
                _rows.Add(row);
                LayerList.Items.Add(row.Item);
            }
            if (offset is { } o)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (LayerList.Scroll is { } s) s.Offset = o;
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }

            if (animate)
            {
                // 排在捲動還原之後（同優先權 FIFO），位置才算對
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => AnimateStructureChange(oldPositions),
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }
        _skipNextAnimation = false;

        if (session != null)
        {
            foreach (var row in _rows) UpdateRow(row, session);

            // 同步選取到 ActiveLayer
            var active = session.Document.ActiveLayer;
            var activeRow = _rows.FirstOrDefault(r => ReferenceEquals(r.Node, active));
            if (!ReferenceEquals(LayerList.SelectedItem, activeRow?.Item))
                LayerList.SelectedItem = activeRow?.Item;

            if (activeRow != null && !ReferenceEquals(active, _lastActiveNode))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => LayerList.ScrollIntoView(activeRow.Item),
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
            _lastActiveNode = active;
        }
        else
        {
            _lastActiveNode = null;
        }

        _suppressUiEvents = false;
        _propsWindow?.SyncFromModel();
    }

    // ---- 結構變化動畫（FLIP） ----

    /// <summary>
    /// 重建後把每列從舊位置滑到新位置（Motion.Move）。
    /// 舊位置沒有的節點＝新出現（新增圖層、展開群組）→ 淡入；
    /// 被刪掉/收起的列直接消失，動畫由其他列滑動補位來呈現。
    /// </summary>
    private void AnimateStructureChange(Dictionary<LayerNode, Point> oldPositions)
    {
        foreach (var row in _rows)
        {
            // 虛擬化掉（不在畫面上）的列不用動畫
            if (row.Item.TranslatePoint(default, LayerList) is not { } now) continue;

            if (oldPositions.TryGetValue(row.Node, out var old))
            {
                var dx = old.X - now.X;
                var dy = old.Y - now.Y;
                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5) SlideIn(row.Item, dx, dy);
            }
            else
            {
                FadeIn(row.Item);
            }
        }
    }

    /// <summary>從 (dx,dy) 的偏移滑回原位（Motion.Move）。</summary>
    private static void SlideIn(ListBoxItem item, double dx, double dy) => Controls.Motion.Slide(item, dx, dy);

    /// <summary>新列：淡入 + 從上方 6px 滑入（Motion.Base）。</summary>
    private static void FadeIn(ListBoxItem item) => Controls.Motion.FadeSlideIn(item, "translateY(-6px)");

    /// <summary>底部操作列：icon-only 按鈕，尺寸與樣式比照工具面板。</summary>
    private void BuildActionBar()
    {
        ActionBar.Children.Add(IconButton(MaterialIconKind.PlusBoxOutline, "新增圖層", OnAddLayer));

        var adjustment = IconButton(MaterialIconKind.CircleHalfFull, "新增調整圖層", null);
        var menu = new Controls.AnimatedMenuFlyout();
        foreach (var entry in AdjustmentRegistry.All)
        {
            var e = entry;
            var item = new MenuItem { Header = e.DisplayName };
            item.Click += (_, _) => AddAdjustment(e.CreateDefault());
            menu.Items.Add(item);
        }
        adjustment.Flyout = menu;
        ActionBar.Children.Add(adjustment);

        ActionBar.Children.Add(IconButton(MaterialIconKind.ContentDuplicate, "複製圖層", OnDuplicateLayer));
        ActionBar.Children.Add(IconButton(MaterialIconKind.FolderPlusOutline, "群組化", OnGroupLayer));
        ActionBar.Children.Add(new Border { Width = 1, Margin = new Thickness(3, 5), Background = AppTheme.SeparatorBrush });
        ActionBar.Children.Add(IconButton(MaterialIconKind.ArrowUp, "上移", OnMoveUp));
        ActionBar.Children.Add(IconButton(MaterialIconKind.ArrowDown, "下移", OnMoveDown));
        ActionBar.Children.Add(new Border { Width = 1, Margin = new Thickness(3, 5), Background = AppTheme.SeparatorBrush });
        ActionBar.Children.Add(IconButton(MaterialIconKind.TrashCanOutline, "刪除", OnDeleteLayer));
    }

    private static Button IconButton(MaterialIconKind kind, string tip, EventHandler<RoutedEventArgs>? click)
    {
        var button = new Button
        {
            Content = new MaterialIcon { Kind = kind, Width = 18, Height = 18 },
            Width = 34,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (click != null) button.Click += click;
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>群組列的展開箭頭（其他列放等寬空位維持對齊）。點擊收合/展開，箭頭旋轉過去。</summary>
    private Control BuildExpander(LayerNode node)
    {
        const double slotWidth = 18;
        if (node is not GroupLayer group)
            return new Border { Width = slotWidth };

        var collapsed = _collapsed.Contains(group);
        var targetAngle = collapsed ? -90 : 0; // ▾ 展開、▸ 收起

        var rotate = new RotateTransform(targetAngle);
        rotate.Transitions =
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = RotateTransform.AngleProperty,
                Duration = Controls.Motion.Base,
                Easing = Controls.Motion.Enter,
            },
        ];
        var chevron = new MaterialIcon
        {
            Kind = MaterialIconKind.ChevronDown,
            Width = 16,
            Height = 16,
            RenderTransform = rotate,
        };

        // 剛被切換的群組：列是重建的，箭頭從舊角度轉到新角度才看得到旋轉
        if (ReferenceEquals(group, _justToggledGroup))
        {
            _justToggledGroup = null;
            rotate.Angle = collapsed ? 0 : -90;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => rotate.Angle = targetAngle,
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        var button = new Button
        {
            Content = chevron,
            Width = slotWidth,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, _collapsed.Contains(group) ? "展開群組" : "收起群組");
        button.Click += (_, e) =>
        {
            if (!_collapsed.Remove(group)) _collapsed.Add(group);
            _justToggledGroup = group;
            Refresh();
            e.Handled = true;
        };
        return button;
    }

    private Row BuildRow(LayerNode node, int depth)
    {
        var check = new CheckBox
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_suppressUiEvents || _session == null) return;
            LayerCommands.SetVisible(_session.Document, _session.History, node, check.IsChecked == true);
            StateChanged?.Invoke();
        };

        var thumb = new Image
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var name = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 物件屬於圖層 —— 標出哪個圖層上有物件
        var badge = new TextBlock
        {
            FontSize = 11,
            Foreground = AppTheme.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            IsVisible = false,
        };

        var expander = BuildExpander(node);
        DockPanel.SetDock(expander, Dock.Left);
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(thumb, Dock.Left);
        DockPanel.SetDock(badge, Dock.Right);
        var content = new DockPanel
        {
            Margin = new Thickness(depth * 16, 0, 0, 0),
            Children = { expander, check, thumb, badge, name },
        };

        var item = new ListBoxItem
        {
            Content = content,
            Tag = node,
            Padding = new Thickness(6, 4),
        };
        ToolTip.SetTip(item, "雙擊開啟圖層屬性");
        item.DoubleTapped += (_, e) =>
        {
            // 連點可見性勾選框/展開鈕只是快速切兩次，不該順便開屬性視窗
            if ((e.Source as Visual)?.FindAncestorOfType<CheckBox>(true) != null) return;
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) != null) return;
            OpenProperties(node);
            e.Handled = true;
        };

        return new Row
        {
            Node = node,
            Depth = depth,
            Item = item,
            Check = check,
            Thumb = thumb,
            NameText = name,
            Badge = badge,
        };
    }

    /// <summary>把列的顯示內容同步到節點目前狀態（呼叫端已設 _suppressUiEvents）。</summary>
    private void UpdateRow(Row row, EditorSession session)
    {
        var node = row.Node;
        row.Check.IsChecked = node.IsVisible;
        row.NameText.Text = node switch
        {
            // 群組的箭頭改由展開鈕呈現，名稱不再帶「▸」前綴
            GroupLayer g => _collapsed.Contains(g) ? $"{node.Name}（{g.Children.Count}）" : node.Name,
            AdjustmentLayer => $"◐ {node.Name}",
            RasterLayer { IsTextLayer: true, HasEffects: true } => $"T  {node.Name}  ✦fx",
            RasterLayer { IsTextLayer: true } => $"T  {node.Name}",
            RasterLayer { HasEffects: true } => $"{node.Name}  ✦fx",
            _ => node.Name,
        };
        row.NameText.FontWeight = node is GroupLayer ? FontWeight.Bold : FontWeight.Normal;

        if (node is RasterLayer { HasElements: true } withElements)
        {
            row.Badge.Text = $"T×{withElements.Elements.Count}";
            row.Badge.IsVisible = true;
        }
        else
        {
            row.Badge.IsVisible = false;
        }

        row.Thumb.Source = LayerThumbnail.Render(session.Document, node, ThumbWidth, ThumbHeight);
    }

    private LayerNode? SelectedNode => (LayerList.SelectedItem as ListBoxItem)?.Tag as LayerNode;

    // ---- 圖層屬性視窗（雙擊開啟；混合模式/不透明度/調整參數都在裡面） ----

    /// <summary>屬性視窗開著時同步（效果堆疊從選單改了要看得到）。</summary>
    public void SyncPropertiesWindow() => _propsWindow?.SyncFromModel();

    public void OpenProperties(LayerNode node)
    {
        if (_session == null) return;
        if (_propsWindow is { } open && ReferenceEquals(open.Node, node))
        {
            open.Activate();
            return;
        }
        _propsWindow?.Close();

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var win = new LayerPropertiesWindow(_session, node);
        win.StateChanged += () => StateChanged?.Invoke();
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_propsWindow, win)) _propsWindow = null;
        };
        _propsWindow = win;
        win.Show(owner);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _session == null) return;
        var node = SelectedNode;
        if (node != null)
        {
            lock (_session.Document.SyncRoot)
            {
                _session.Document.ActiveLayer = node;
            }
            _lastActiveNode = node;
            // 物件屬於圖層：換圖層就放掉前一層的物件選取（把手框會自動跟上）
            _session.SelectedElement = null;
        }
        StateChanged?.Invoke();
    }

    // ---- 滑鼠拖曳排序 ----

    private enum DropKind { None, Above, Below, Into }

    private LayerNode? _pressNode;
    private Point _pressPoint;
    private bool _dragActive;
    private DropKind _dropKind;
    private Row? _dropRow;
    private Row? _pressRow;
    private ListBoxItem? _highlightItem;

    private static readonly IBrush GroupDropBrush =
        new SolidColorBrush(Color.FromArgb(0x40, 0x2A, 0x9D, 0xF4));

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressNode = null;
        if (!e.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed) return;

        var source = e.Source as Visual;
        // 從勾選框/展開鈕按下不啟動拖曳（那是點擊切換可見性/收合）
        if (source?.FindAncestorOfType<CheckBox>(true) != null) return;
        if (source?.FindAncestorOfType<Button>(true) != null) return;

        var item = source?.FindAncestorOfType<ListBoxItem>(true);
        if (item?.Tag is not LayerNode node) return;

        _pressNode = node;
        _pressRow = _rows.FirstOrDefault(r => ReferenceEquals(r.Item, item));
        _pressPoint = e.GetPosition(LayerList);
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressNode == null || _session == null) return;
        var p = e.GetPosition(LayerList);

        if (!_dragActive)
        {
            if (!e.GetCurrentPoint(LayerList).Properties.IsLeftButtonPressed)
            {
                _pressNode = null;
                return;
            }
            var dx = p.X - _pressPoint.X;
            var dy = p.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < 6 * 6) return;

            _dragActive = true;
            e.Pointer.Capture(LayerList);
            if (_pressRow != null) _pressRow.Item.Opacity = 0.35;
            BeginGhost();
        }

        AutoScroll(p);
        MoveGhost(p);
        UpdateDropTarget(p);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragActive)
        {
            var node = _pressNode;
            var kind = _dropKind;
            var row = _dropRow;
            CancelDrag();
            CommitDrop(node, kind, row);
            e.Handled = true;
        }
        _pressNode = null;
        _pressRow = null;
    }

    private void CancelDrag()
    {
        _dragActive = false;
        _dropKind = DropKind.None;
        _dropRow = null;
        DropIndicator.IsVisible = false;
        _highlightItem?.ClearValue(BackgroundProperty);
        _highlightItem = null;
        if (_pressRow != null) _pressRow.Item.Opacity = 1;
        DragGhost.IsVisible = false;
        DragGhostImage.Source = null;
        _ghost?.Dispose();
        _ghost = null;
    }

    // ---- 拖曳中跟著指標走的那一列（列本身的快照）----

    private RenderTargetBitmap? _ghost;
    private double _ghostGrabY;

    /// <summary>
    /// 把被拖的那一列畫成點陣圖當「抓在手上的東西」。
    /// 直接把 ListBoxItem 搬到 overlay 上不行 —— 它還在 ListBox 的排版裡，
    /// 抽走會讓清單當場少一列、放開又要塞回去；快照最省事也最穩。
    /// </summary>
    private void BeginGhost()
    {
        if (_pressRow == null) return;
        var item = _pressRow.Item;
        var size = item.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;

        var scale = (item.GetVisualRoot() as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        var pixels = new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));
        try
        {
            _ghost = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            _ghost.Render(item);
        }
        catch
        {
            _ghost?.Dispose();
            _ghost = null;
            return; // 畫不出來就退回原本的「只有插入線」行為
        }

        DragGhostImage.Source = _ghost;
        DragGhost.Width = size.Width;
        DragGhost.Height = size.Height;
        // 抓在指標按下的那一點：拖起來的位置不會跳
        _ghostGrabY = item.TranslatePoint(default, LayerList) is { } pt
            ? Math.Clamp(_pressPoint.Y - pt.Y, 0, size.Height)
            : size.Height / 2;
        DragGhost.IsVisible = true;
    }

    private void MoveGhost(Point p)
    {
        if (!DragGhost.IsVisible) return;
        Canvas.SetLeft(DragGhost, 4);
        Canvas.SetTop(DragGhost, Math.Clamp(p.Y - _ghostGrabY,
            -DragGhost.Height / 2, Math.Max(0, LayerList.Bounds.Height - DragGhost.Height / 2)));
    }

    private void AutoScroll(Point p)
    {
        if (LayerList.Scroll is not { } scroll) return;
        const double edge = 26, step = 12;
        if (p.Y < edge)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - step));
        else if (p.Y > LayerList.Bounds.Height - edge)
            scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + step);
    }

    private void UpdateDropTarget(Point p)
    {
        _dropKind = DropKind.None;
        _dropRow = null;

        // 只考慮實際在畫面上的列（虛擬化掉的 TranslatePoint 會是 null）
        var visible = new List<(Row Row, double Top, double Bottom)>();
        foreach (var row in _rows)
        {
            if (row.Item.TranslatePoint(default, LayerList) is not { } pt) continue;
            visible.Add((row, pt.Y, pt.Y + row.Item.Bounds.Height));
        }
        visible.Sort((a, b) => a.Top.CompareTo(b.Top));

        if (visible.Count == 0)
        {
            ShowIndicator();
            return;
        }

        (Row Row, double Top, double Bottom)? hit = null;
        foreach (var v in visible)
        {
            if (p.Y >= v.Top && p.Y <= v.Bottom)
            {
                hit = v;
                break;
            }
        }

        if (hit == null)
        {
            if (p.Y < visible[0].Top) (_dropRow, _dropKind) = (visible[0].Row, DropKind.Above);
            else (_dropRow, _dropKind) = (visible[^1].Row, DropKind.Below);
        }
        else
        {
            var (row, top, bottom) = hit.Value;
            var rel = (p.Y - top) / Math.Max(1, bottom - top);
            if (row.Node is GroupLayer && rel is > 0.3 and < 0.7)
                _dropKind = DropKind.Into;
            else
                _dropKind = rel < 0.5 ? DropKind.Above : DropKind.Below;

            // 群組列的下緣間隙（介於群組標題與其子項之間）＝放進群組最上層
            if (_dropKind == DropKind.Below && row.Node is GroupLayer)
                _dropKind = DropKind.Into;
            _dropRow = row;
        }

        if (_dropRow != null && !IsValidDrop(_pressNode!, _dropKind, _dropRow))
        {
            _dropKind = DropKind.None;
            _dropRow = null;
        }

        ShowIndicator();
    }

    private static bool IsValidDrop(LayerNode node, DropKind kind, Row target)
    {
        var newParent = kind == DropKind.Into ? target.Node as GroupLayer : target.Node.Parent;
        for (var g = newParent; g != null; g = g.Parent)
        {
            if (ReferenceEquals(g, node)) return false; // 不能放進自己或子孫
        }
        return newParent != null;
    }

    private void ShowIndicator()
    {
        _highlightItem?.ClearValue(BackgroundProperty);
        _highlightItem = null;
        DropIndicator.IsVisible = false;

        if (_dropRow == null || _dropKind == DropKind.None) return;

        if (_dropKind == DropKind.Into)
        {
            _highlightItem = _dropRow.Item;
            _highlightItem.Background = GroupDropBrush;
            return;
        }

        if (_dropRow.Item.TranslatePoint(default, LayerList) is not { } pt) return;
        var y = _dropKind == DropKind.Above ? pt.Y : pt.Y + _dropRow.Item.Bounds.Height;
        var indent = _dropRow.Depth * 16 + 4;
        Canvas.SetLeft(DropIndicator, indent);
        Canvas.SetTop(DropIndicator, Math.Clamp(y - 1.5, 0, LayerList.Bounds.Height - 3));
        DropIndicator.Width = Math.Max(0, LayerList.Bounds.Width - indent - 8);
        DropIndicator.IsVisible = true;
    }

    private void CommitDrop(LayerNode? node, DropKind kind, Row? target)
    {
        if (_session == null || node?.Parent == null || target == null || kind == DropKind.None) return;

        GroupLayer newParent;
        int newIndex;
        switch (kind)
        {
            case DropKind.Into:
                newParent = (GroupLayer)target.Node;
                newIndex = newParent.Children.Count; // 群組內最上層
                _collapsed.Remove(newParent); // 收起的群組自動展開，丟進去的東西才看得到
                break;
            case DropKind.Above:
                newParent = target.Node.Parent!;
                newIndex = newParent.IndexOf(target.Node) + 1; // 視覺上方 = children index 較大
                break;
            default: // Below
                newParent = target.Node.Parent!;
                newIndex = newParent.IndexOf(target.Node);
                break;
        }

        for (var g = (GroupLayer?)newParent; g != null; g = g.Parent)
        {
            if (ReferenceEquals(g, node)) return;
        }

        var oldParent = node.Parent;
        if (ReferenceEquals(newParent, oldParent))
        {
            var oldIndex = oldParent.IndexOf(node);
            if (newIndex > oldIndex) newIndex--; // 先移除再插入，位置往前補一格
            if (newIndex == oldIndex) return;    // 沒動
        }

        LayerCommands.MoveNode(_session.Document, _session.History, node, newParent, newIndex, "拖曳圖層");
        Refresh();
        StateChanged?.Invoke();
    }

    // ---- 結構操作 ----

    public void AddAdjustment(IAdjustment adjustment)
    {
        if (_session == null) return;
        var doc = _session.Document;
        var active = doc.ActiveLayer;

        var parent = active?.Parent ?? doc.Root;
        var index = active != null && active.Parent != null
            ? parent.IndexOf(active) + 1
            : parent.Children.Count;

        var layer = new AdjustmentLayer(adjustment);
        LayerCommands.InsertLayer(doc, _session.History, parent, index, layer, $"新增{adjustment.DisplayName}");
        lock (doc.SyncRoot) doc.ActiveLayer = layer;
        Refresh();
        StateChanged?.Invoke();
        OpenProperties(layer); // 調整參數在屬性視窗裡，直接開
    }

    private void OnAddLayer(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var doc = _session.Document;
        var active = doc.ActiveLayer;

        var parent = active?.Parent ?? doc.Root;
        var index = active != null && active.Parent != null
            ? parent.IndexOf(active) + 1
            : parent.Children.Count;

        var layer = new RasterLayer { Name = $"圖層 {CountLayers(doc.Root) + 1}" };
        LayerCommands.InsertLayer(doc, _session.History, parent, index, layer);
        lock (doc.SyncRoot) doc.ActiveLayer = layer;
        Refresh();
        StateChanged?.Invoke();
    }

    private void OnDuplicateLayer(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (SelectedNode is not RasterLayer layer)
        {
            _session.Notify("請先選擇一個圖層（群組／調整圖層不能複製）");
            return;
        }
        LayerCommands.DuplicateLayer(_session.Document, _session.History, layer);
        Refresh();
        StateChanged?.Invoke();
    }

    private void OnGroupLayer(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var node = SelectedNode;
        if (node == null || node.Parent == null) return;

        LayerCommands.WrapInGroup(_session.Document, _session.History, node);
        Refresh();
        StateChanged?.Invoke();
    }

    private void OnDeleteLayer(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var node = SelectedNode;
        if (node?.Parent == null) return;

        var doc = _session.Document;
        var parent = node.Parent;
        var index = parent.IndexOf(node);
        LayerCommands.RemoveLayer(doc, _session.History, node);

        // 選鄰近節點
        lock (doc.SyncRoot)
        {
            doc.ActiveLayer = parent.Children.Count > 0
                ? parent.Children[Math.Min(index, parent.Children.Count - 1)]
                : (parent == doc.Root ? null : parent);
        }
        Refresh();
        StateChanged?.Invoke();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(+1);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    /// <summary>direction：+1 = 視覺上移（children index +1），-1 = 下移。</summary>
    private void MoveSelected(int direction)
    {
        if (_session == null) return;
        var node = SelectedNode;
        var parent = node?.Parent;
        if (node == null || parent == null) return;

        var index = parent.IndexOf(node);
        var newIndex = index + direction;
        if (newIndex < 0 || newIndex >= parent.Children.Count) return;

        LayerCommands.MoveNode(_session.Document, _session.History, node, parent, newIndex,
            direction > 0 ? "圖層上移" : "圖層下移");
        Refresh();
        StateChanged?.Invoke();
    }

    private static int CountLayers(GroupLayer group)
    {
        var count = 0;
        foreach (var child in group.Children)
        {
            count++;
            if (child is GroupLayer g) count += CountLayers(g);
        }
        return count;
    }
}
