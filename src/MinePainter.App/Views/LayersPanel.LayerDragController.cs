using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;

namespace MinePainter.App.Views;

public partial class LayersPanel
{
    /// <summary>集中管理圖層清單手勢、按下時的選取快照，以及拖曳覆疊與快照資源。</summary>
    private sealed class LayerDragController(LayersPanel owner)
    {
        private readonly LayersPanel _owner = owner;
        public LayerNode? PressedNode => _pressNode;

        private enum DropKind { None, Above, Below, Into }

        private LayerNode? _pressNode;
        private Point _pressPoint;
        private bool _dragActive;
        private DropKind _dropKind;
        private Row? _dropRow;
        private Row? _pressRow;

        /// <summary>
        /// 按下時如果按的是多選裡的一列，先記下整份選取 —— ListBox 會在按下的瞬間把選取收成只剩這一列，
        /// 等真的拖起來再把它們選回來一起搬；沒拖（只是點）就照 ListBox 的意思只剩這一列。
        /// </summary>
        private List<LayerNode>? _pressSelection;

        /// <summary>正在拖的那幾個節點（面板由上到下的順序）；沒拖時為空。</summary>
        private readonly List<Row> _dragRows = new();

        // 空白處框選
        private bool _marqueePressed;
        private bool _marqueeActive;
        private Point _marqueeStart;
        private HashSet<LayerNode> _marqueeBase = new();

        private static readonly IBrush GroupDropBrush =
            new SolidColorBrush(Color.FromArgb(0x40, 0x2A, 0x9D, 0xF4));

        private static readonly IBrush GroupDropBorderBrush = AppTheme.AccentBrush;

        // 拉效果用紫色，跟搬圖層的藍色分開：一眼就知道現在拖的是哪一種東西。
        // 底色只是淡淡一層，真正指出落點的是外框 —— 一次只有一列有框，掃過去不會糊成一片
        private static readonly IBrush EffectDropBrush =
            new SolidColorBrush(Color.FromArgb(0x26, 0x9B, 0x59, 0xD0));

        private static readonly IBrush EffectBadgeBrush =
            new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xD0));

        // ---- Alt 拖曳＝搬的是「效果」不是圖層（Alt 在這個 App 一律是「複製」；再加 Shift＝取代） ----
        // 判斷用的是「畫面上最後顯示的狀態」而不是放開滑鼠那一刻的按鍵：紫框與角標說會發生什麼，
        // 就發生什麼。放開 Alt 之後只要動一下滑鼠，指示就會變回搬圖層。
        private bool _effectDrag;
        private bool _effectReplace;
        private Row? _effectTarget;

        /// <summary>拖著的那幾層的效果（面板由上到下串起來）；拖起來的瞬間拍一份。</summary>
        private readonly List<LayerEffect> _dragEffects = new();

        public void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _pressNode = null;
            if (!e.GetCurrentPoint(_owner.LayerList).Properties.IsLeftButtonPressed) return;

            var source = e.Source as Visual;
            // 從勾選框/展開鈕按下不啟動拖曳（那是點擊切換可見性/收合）
            if (source?.FindAncestorOfType<CheckBox>(true) != null) return;
            if (source?.FindAncestorOfType<Button>(true) != null) return;

            var item = source?.FindAncestorOfType<ListBoxItem>(true);
            if (item?.Tag is not LayerNode node)
            {
                // 按在列以外的空白處：拖出框來選。Ctrl 按著＝加到現有選取
                if (source == null || !ReferenceEquals(source.FindAncestorOfType<ListBox>(true), _owner.LayerList)) return;
                _marqueePressed = true;
                _marqueeStart = e.GetPosition(_owner.LayerList);
                _marqueeBase = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    ? new HashSet<LayerNode>(_owner.SelectedNodes)
                    : new HashSet<LayerNode>();
                return;
            }

            _pressNode = node;
            _pressRow = _owner._rows.FirstOrDefault(r => ReferenceEquals(r.Item, item));
            _pressPoint = e.GetPosition(_owner.LayerList);

            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                // Alt 按下＝要拉效果，不是要選圖層：把事件吃掉，別讓 ListBox 動到選取。
                // 不吃掉的話 Alt+Shift（取代）會被 ListBox 當成 Shift 連選，把落點那一列也選進來
                // 一起拖，目標就變成「拖著的其中一列」而失效。
                _pressSelection = null;
                e.Handled = true;
                return;
            }

            var plain = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0;
            var selected = _owner.SelectedNodes;
            _pressSelection = plain && selected.Count > 1 && selected.Contains(node) ? selected.ToList() : null;
        }

        public void OnListPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_owner._session == null) return;
            var p = e.GetPosition(_owner.LayerList);

            if (_marqueePressed)
            {
                if (!e.GetCurrentPoint(_owner.LayerList).Properties.IsLeftButtonPressed)
                {
                    EndMarquee();
                    return;
                }
                var mdx = p.X - _marqueeStart.X;
                var mdy = p.Y - _marqueeStart.Y;
                if (!_marqueeActive && mdx * mdx + mdy * mdy < 4 * 4) return;
                if (!_marqueeActive)
                {
                    _marqueeActive = true;
                    e.Pointer.Capture(_owner.LayerList);
                }
                AutoScroll(p);
                UpdateMarquee(p);
                return;
            }

            if (_pressNode == null) return;

            if (!_dragActive)
            {
                if (!e.GetCurrentPoint(_owner.LayerList).Properties.IsLeftButtonPressed)
                {
                    _pressNode = null;
                    _pressSelection = null;
                    return;
                }
                var dx = p.X - _pressPoint.X;
                var dy = p.Y - _pressPoint.Y;
                if (dx * dx + dy * dy < 6 * 6) return;

                _dragActive = true;
                e.Pointer.Capture(_owner.LayerList);
                BeginDragRows();
                BeginGhost();
            }

            AutoScroll(p);
            MoveGhost(p);
            UpdateDragMode(e.KeyModifiers);
            UpdateDropTarget(p);
        }

        /// <summary>拖曳中隨時可以改主意：Alt＝拉效果、Alt+Shift＝取代目標的效果堆疊。</summary>
        private void UpdateDragMode(KeyModifiers modifiers)
        {
            // Alt 按著就一律進入拉效果模式（就算來源沒有效果）—— 不然使用者以為在拉效果，
            // 結果圖層被搬走了
            var effect = modifiers.HasFlag(KeyModifiers.Alt);
            var replace = modifiers.HasFlag(KeyModifiers.Shift);
            if (effect == _effectDrag && replace == _effectReplace) return;
            _effectDrag = effect;
            _effectReplace = replace;
            UpdateGhostBadge();
        }

        /// <summary>拖曳幽靈右上角的角標：搬圖層時是「×N」，拉效果時說清楚是疊加還是取代。</summary>
        private void UpdateGhostBadge()
        {
            if (_effectDrag)
            {
                _owner.DragGhostCount.Background = EffectBadgeBrush;
                _owner.DragGhostCountText.Text = _dragEffects.Count == 0
                    ? "沒有效果"
                    : $"效果 ×{_dragEffects.Count}・{(_effectReplace ? "取代" : "疊加")}";
                _owner.DragGhostCount.IsVisible = true;
                return;
            }
            _owner.DragGhostCount.Background = AppTheme.AccentBrush;
            _owner.DragGhostCountText.Text = $"×{_dragRows.Count}";
            _owner.DragGhostCount.IsVisible = _dragRows.Count > 1;
        }

        public void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_marqueePressed)
            {
                var wasActive = _marqueeActive;
                EndMarquee();
                if (wasActive) e.Handled = true;
                return;
            }
            if (_dragActive)
            {
                var nodes = _dragRows.Select(r => r.Node).ToList();
                var kind = _dropKind;
                var row = _dropRow;
                var effectDrag = _effectDrag;
                var effectReplace = _effectReplace;
                var effectTarget = _effectTarget;
                var effects = _dragEffects.ToList();
                CancelDrag();
                if (effectDrag) CommitEffectDrop(effects, effectTarget, effectReplace);
                else CommitDrop(nodes, kind, row);
                e.Handled = true;
            }
            _pressNode = null;
            _pressRow = null;
            _pressSelection = null;
        }

        /// <summary>拖起來的瞬間決定要搬哪幾列：按的是多選裡的一列就整批走，否則只搬按住的那一列。</summary>
        private void BeginDragRows()
        {
            _dragRows.Clear();
            if (_pressSelection != null)
            {
                // ListBox 在按下時把多選收成一列了，拖起來要把它們選回去
                _owner._suppressUiEvents = true;
                _owner.LayerList.SelectedItems?.Clear();
                foreach (var row in _owner._rows)
                    if (_pressSelection.Contains(row.Node)) _owner.LayerList.SelectedItems?.Add(row.Item);
                _owner._suppressUiEvents = false;
            }
            var selected = _owner.SelectedNodes;
            var dragging = _pressNode != null && selected.Contains(_pressNode) && selected.Count > 1
                ? selected
                : _pressNode != null ? [_pressNode] : [];
            foreach (var row in _owner._rows)
                if (dragging.Contains(row.Node)) _dragRows.Add(row);
            foreach (var row in _dragRows) row.Item.Opacity = 0.35;

            _dragEffects.Clear();
            foreach (var row in _dragRows) _dragEffects.AddRange(row.Node.Effects);
        }

        public void CancelDrag()
        {
            _dragActive = false;
            _dropKind = DropKind.None;
            _dropRow = null;
            _owner.DropIndicator.IsVisible = false;
            _owner.DropRowHighlight.IsVisible = false;
            foreach (var row in _dragRows) row.Item.Opacity = 1;
            if (_pressRow != null) _pressRow.Item.Opacity = 1;
            _dragRows.Clear();
            _dragEffects.Clear();
            _effectDrag = false;
            _effectReplace = false;
            _effectTarget = null;
            _owner.DragGhost.IsVisible = false;
            _owner.DragGhostImage.Source = null;
            _owner.DragGhostCount.IsVisible = false;
            _ghost?.Dispose();
            _ghost = null;
            EndMarquee();
        }

        // ---- 空白處框選 ----

        private void UpdateMarquee(Point p)
        {
            var left = Math.Min(_marqueeStart.X, p.X);
            var top = Math.Min(_marqueeStart.Y, p.Y);
            var rect = new Rect(left, top, Math.Abs(p.X - _marqueeStart.X), Math.Abs(p.Y - _marqueeStart.Y));
            Canvas.SetLeft(_owner.Marquee, rect.X);
            Canvas.SetTop(_owner.Marquee, rect.Y);
            _owner.Marquee.Width = rect.Width;
            _owner.Marquee.Height = rect.Height;
            _owner.Marquee.IsVisible = true;

            // 框碰到的列都選起來（虛擬化掉的列不在畫面上，本來就碰不到）
            var wanted = new HashSet<LayerNode>(_marqueeBase);
            foreach (var row in _owner._rows)
            {
                if (row.Item.TranslatePoint(default, _owner.LayerList) is not { } pt) continue;
                var bounds = new Rect(pt, row.Item.Bounds.Size);
                if (bounds.Intersects(rect)) wanted.Add(row.Node);
            }
            if (_owner.LayerList.SelectedItems is not { } items) return;
            var current = new HashSet<LayerNode>(_owner.SelectedNodes);
            if (current.SetEquals(wanted)) return;
            _owner._suppressUiEvents = true;
            items.Clear();
            foreach (var row in _owner._rows)
                if (wanted.Contains(row.Node)) items.Add(row.Item);
            _owner._suppressUiEvents = false;
        }

        private void EndMarquee()
        {
            if (!_marqueePressed) return;
            var wasActive = _marqueeActive;
            _marqueePressed = false;
            _marqueeActive = false;
            _owner.Marquee.IsVisible = false;
            if (!wasActive || _owner._session == null) return;
            // 框完：作用中圖層＝框裡最上面那一層（沒框到東西就維持原樣，ListBox 的同步會把作用中那層選回來）
            var selected = _owner.SelectedNodes;
            var active = _owner._session.Document.ActiveLayer;
            if (selected.Count > 0 && (active == null || !selected.Contains(active))) _owner.ActivateNode(selected[0]);
            if (selected.Count == 0) _owner.Refresh();
            _owner.RaiseStateChanged();
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

            _owner.DragGhostImage.Source = _ghost;
            _owner.DragGhost.Width = size.Width;
            _owner.DragGhost.Height = size.Height;
            UpdateGhostBadge();
            // 抓在指標按下的那一點：拖起來的位置不會跳
            _ghostGrabY = item.TranslatePoint(default, _owner.LayerList) is { } pt
                ? Math.Clamp(_pressPoint.Y - pt.Y, 0, size.Height)
                : size.Height / 2;
            _owner.DragGhost.IsVisible = true;
        }

        private void MoveGhost(Point p)
        {
            if (!_owner.DragGhost.IsVisible) return;
            Canvas.SetLeft(_owner.DragGhost, 4);
            Canvas.SetTop(_owner.DragGhost, Math.Clamp(p.Y - _ghostGrabY,
                -_owner.DragGhost.Height / 2, Math.Max(0, _owner.LayerList.Bounds.Height - _owner.DragGhost.Height / 2)));
        }

        private void AutoScroll(Point p)
        {
            if (_owner.LayerList.Scroll is not { } scroll) return;
            const double edge = 26, step = 12;
            if (p.Y < edge)
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - step));
            else if (p.Y > _owner.LayerList.Bounds.Height - edge)
                scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + step);
        }

        private void UpdateDropTarget(Point p)
        {
            _dropKind = DropKind.None;
            _dropRow = null;
            _effectTarget = null;

            // 只考慮實際在畫面上的列（虛擬化掉的 TranslatePoint 會是 null）
            var visible = new List<(Row Row, double Top, double Bottom)>();
            foreach (var row in _owner._rows)
            {
                if (row.Item.TranslatePoint(default, _owner.LayerList) is not { } pt) continue;
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

            if (_effectDrag)
            {
                // 拉效果：目標就是指標底下那一列（不分上下、群組也可以有效果），來源自己不算
                if (hit != null && _dragEffects.Count > 0 &&
                    !_dragRows.Any(r => ReferenceEquals(r.Node, hit.Value.Row.Node)))
                {
                    _effectTarget = hit.Value.Row;
                }
                ShowIndicator();
                return;
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

            if (_dropRow != null && !IsValidDrop(_dragRows, _dropKind, _dropRow))
            {
                _dropKind = DropKind.None;
                _dropRow = null;
            }

            ShowIndicator();
        }

        private static bool IsValidDrop(List<Row> dragging, DropKind kind, Row target)
        {
            var newParent = kind == DropKind.Into ? target.Node as GroupLayer : target.Node.Parent;
            if (newParent == null) return false;
            foreach (var row in dragging)
            {
                if (ReferenceEquals(row, target)) return false; // 放在自己身上沒有意義
                for (var g = newParent; g != null; g = g.Parent)
                {
                    if (ReferenceEquals(g, row.Node)) return false; // 不能放進自己或子孫
                }
            }
            return true;
        }

        private void ShowIndicator()
        {
            _owner.DropRowHighlight.IsVisible = false;
            _owner.DropIndicator.IsVisible = false;

            if (_effectDrag)
            {
                if (_effectTarget != null) HighlightRow(_effectTarget, EffectDropBrush, EffectBadgeBrush);
                return;
            }

            if (_dropRow == null || _dropKind == DropKind.None) return;

            if (_dropKind == DropKind.Into)
            {
                HighlightRow(_dropRow, GroupDropBrush, GroupDropBorderBrush);
                return;
            }

            if (_dropRow.Item.TranslatePoint(default, _owner.LayerList) is not { } pt) return;
            var y = _dropKind == DropKind.Above ? pt.Y : pt.Y + _dropRow.Item.Bounds.Height;
            var indent = _dropRow.Depth * 16 + 4;
            Canvas.SetLeft(_owner.DropIndicator, indent);
            Canvas.SetTop(_owner.DropIndicator, Math.Clamp(y - 1.5, 0, _owner.LayerList.Bounds.Height - 3));
            _owner.DropIndicator.Width = Math.Max(0, _owner.LayerList.Bounds.Width - indent - 8);
            _owner.DropIndicator.IsVisible = true;
        }

        /// <summary>
        /// 把「整列都是落點」的框畫在那一列上。用覆疊層而不是改 <c>ListBoxItem.Background</c> ——
        /// 那個屬性有 160ms 的漸變（Animations.axaml），掃過一串列會留下一整排還在褪色的殘影。
        /// </summary>
        private void HighlightRow(Row row, IBrush fill, IBrush border)
        {
            if (row.Item.TranslatePoint(default, _owner.LayerList) is not { } pt) return;
            _owner.DropRowHighlight.Background = fill;
            _owner.DropRowHighlight.BorderBrush = border;
            _owner.DropRowHighlight.Width = Math.Max(0, _owner.LayerList.Bounds.Width - 8);
            _owner.DropRowHighlight.Height = row.Item.Bounds.Height;
            Canvas.SetLeft(_owner.DropRowHighlight, 4);
            Canvas.SetTop(_owner.DropRowHighlight, pt.Y);
            _owner.DropRowHighlight.IsVisible = true;
        }

        private void CommitDrop(List<LayerNode> nodes, DropKind kind, Row? target)
        {
            if (_owner._session == null || nodes.Count == 0 || target == null || kind == DropKind.None) return;
            if (nodes.Count > 1)
            {
                GroupLayer parentForAll;
                int indexForAll;
                switch (kind)
                {
                    case DropKind.Into:
                        parentForAll = (GroupLayer)target.Node;
                        indexForAll = parentForAll.Children.Count;
                        _owner._collapsed.Remove(parentForAll);
                        break;
                    case DropKind.Above:
                        parentForAll = target.Node.Parent!;
                        indexForAll = parentForAll.IndexOf(target.Node) + 1;
                        break;
                    default:
                        parentForAll = target.Node.Parent!;
                        indexForAll = parentForAll.IndexOf(target.Node);
                        break;
                }
                if (!LayerCommands.MoveNodes(_owner._session.Document, _owner._session.History, nodes, parentForAll, indexForAll, "拖曳圖層")) return;
                _owner.Refresh();
                _owner.RaiseStateChanged();
                return;
            }

            var node = nodes[0];
            if (node.Parent == null) return;

            GroupLayer newParent;
            int newIndex;
            switch (kind)
            {
                case DropKind.Into:
                    newParent = (GroupLayer)target.Node;
                    newIndex = newParent.Children.Count; // 群組內最上層
                    _owner._collapsed.Remove(newParent); // 收起的群組自動展開，丟進去的東西才看得到
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

            LayerCommands.MoveNode(_owner._session.Document, _owner._session.History, node, newParent, newIndex, "拖曳圖層");
            _owner.Refresh();
            _owner.RaiseStateChanged();
        }

        /// <summary>把拖著的效果放到目標圖層（來源保留自己的那份）。</summary>
        private void CommitEffectDrop(List<LayerEffect> effects, Row? target, bool replace)
        {
            if (_owner._session == null || target == null) return;
            if (effects.Count == 0)
            {
                _owner._session.Notify("拖著的圖層沒有效果可以拉");
                return;
            }

            var mode = replace ? EffectDropMode.Replace : EffectDropMode.Append;
            if (!LayerEffectCommands.CopyEffectsTo(_owner._session.Document, _owner._session.History, effects, target.Node, mode))
                return;

            _owner.Refresh();
            _owner.RaiseStateChanged();
            _owner._session.Notify(replace
                ? $"「{target.Node.Name}」的效果換成這 {effects.Count} 道"
                : $"「{target.Node.Name}」加上 {effects.Count} 道效果");
        }
    }
}
