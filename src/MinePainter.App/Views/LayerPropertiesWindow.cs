using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using Material.Icons;
using Material.Icons.Avalonia;
using Avalonia.Controls.Primitives;

namespace MinePainter.App.Views;

/// <summary>
/// 圖層屬性視窗（paint.net 的 Layer Properties）：雙擊圖層列開啟。
/// 名稱／可見性／混合模式／不透明度即時套用（各自進 history），
/// 調整圖層的參數也在這裡編輯。下方附唯讀的詳細資訊。
/// </summary>
public sealed class LayerPropertiesWindow : Window
{
    internal static readonly (BlendMode Mode, string Label)[] BlendItems =
    [
        (BlendMode.Normal, "一般"), (BlendMode.Multiply, "色彩增值"), (BlendMode.Screen, "濾色"),
        (BlendMode.Overlay, "覆疊"), (BlendMode.Darken, "變暗"), (BlendMode.Lighten, "變亮"),
        (BlendMode.ColorDodge, "加亮顏色"), (BlendMode.ColorBurn, "加深顏色"),
        (BlendMode.HardLight, "實光"), (BlendMode.SoftLight, "柔光"),
        (BlendMode.Difference, "差異化"), (BlendMode.Exclusion, "排除"),
        (BlendMode.Hue, "色相"), (BlendMode.Saturation, "飽和度"),
        (BlendMode.Color, "顏色"), (BlendMode.Luminosity, "明度"), (BlendMode.Additive, "線性加亮"),
    ];

    private readonly EditorSession _session;
    private readonly LayerNode _node;

    /// <summary>此視窗編輯中的節點（面板判斷「同一層再雙擊」用）。</summary>
    public LayerNode Node => _node;
    private bool _suppress;

    private readonly Image _preview = new() { Width = 176, Height = 132 };
    private readonly TextBox _nameBox = new() { FontSize = 12 };
    private readonly ComboBox _blendCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly BarSlider _opacityBar = new() { Minimum = 0, Maximum = 100, Suffix = "%", Height = 26 };
    private readonly StackPanel _adjustmentParams = new() { Spacing = 4 };
    private readonly StackPanel _effectsPanel = new() { Spacing = 0 };
    private readonly StackPanel _detailRows = new() { Spacing = 3 };
    private Border _root = null!;

    private float _opacityDragStart = -1;
    private IAdjustment? _adjDragStart;
    private bool _closing;

    /// <summary>圖層屬性變更後發出（讓 MainWindow 刷新 undo 選單等）。</summary>
    public event Action? StateChanged;

    public LayerPropertiesWindow(EditorSession session, LayerNode node)
    {
        _session = session;
        _node = node;

        Title = "圖層屬性";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterOwner; // 跟著主視窗，不跳到別的螢幕

        _opacityBar.Label = node is AdjustmentLayer ? "強度" : "不透明度";
        foreach (var (_, label) in BlendItems)
            _blendCombo.Items.Add(label);

        Content = BuildContent(node);
        SyncFromModel();
        WireEvents();

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                CommitName();
                Close();
                e.Handled = true;
            }
        };
    }

    private Control BuildContent(LayerNode node)
    {
        var titleText = new TextBlock
        {
            Text = $"圖層屬性 — {node.Name}",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 24,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, _) => { CommitName(); Close(); };

        DockPanel.SetDock(closeButton, Dock.Right);
        var header = new Border
        {
            Background = AppTheme.HeaderBrush,
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Height = 26,
            Child = new DockPanel { Children = { closeButton, titleText } },
        };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        header.Cursor = new Cursor(StandardCursorType.SizeAll);
        DockPanel.SetDock(header, Dock.Top);

        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new Border
        {
            Background = AppTheme.InnerBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _preview,
        });

        body.Children.Add(LabeledRow("名稱", _nameBox));

        if (node is not AdjustmentLayer)
            body.Children.Add(LabeledRow("混合", _blendCombo));
        body.Children.Add(_opacityBar);

        if (node is AdjustmentLayer)
        {
            body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
            body.Children.Add(_adjustmentParams);
        }

        if (node is RasterLayer)
        {
            body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
            body.Children.Add(_effectsPanel);
        }

        body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
        body.Children.Add(_detailRows);

        _root = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new DockPanel
            {
                Children =
                {
                    header,
                    new Border { Padding = new Thickness(14, 12), Child = body },
                },
            },
        };
        WindowAnimator.Prepare(_root);
        return _root;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowAnimator.PlayIn(_root);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // 同 PanelWindow：只有使用者自己關這扇窗時才播退場，
        // 主視窗/應用程式關閉時直接放行，否則會中止整個關閉流程。
        if (_closing || WindowAnimator.IsShuttingDown ||
            e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        WindowAnimator.PlayOut(_root, Close);
    }

    private static Control LabeledRow(string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = 38,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(text, Dock.Left);
        return new DockPanel { Children = { text, control } };
    }

    // ---- 模型 → UI（開啟時與外部變更後） ----

    /// <summary>把目前模型狀態同步進 UI；節點已不在文件上（被刪/undo 掉）就自行關閉。</summary>
    /// <summary>只重畫預覽圖（效果快取算完時用；不重建效果堆疊卡片，拖曳中的卡片才不會被換掉）。</summary>
    public void RefreshPreview()
    {
        if (_node.Document == null) return;
        _preview.Source = Rendering.LayerThumbnail.Render(_session.Document, _node, 176, 132);
    }

    public void SyncFromModel()
    {
        if (_node.Document == null)
        {
            Close();
            return;
        }

        _suppress = true;
        if (!_nameBox.IsFocused) _nameBox.Text = _node.Name;
        _opacityBar.Value = _node.Opacity * 100;
        var idx = Array.FindIndex(BlendItems, x => x.Mode == _node.BlendMode);
        _blendCombo.SelectedIndex = Math.Max(0, idx);
        _suppress = false;

        _preview.Source = Rendering.LayerThumbnail.Render(_session.Document, _node, 176, 132);
        BuildAdjustmentEditor();
        BuildEffectsSection();
        BuildDetails();
    }

    private void WireEvents()
    {
        _nameBox.LostFocus += (_, _) => CommitName();
        _nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitName();
                e.Handled = true;
            }
        };

        _blendCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _blendCombo.SelectedIndex < 0) return;
            LayerCommands.SetBlendMode(_session.Document, _session.History, _node, BlendItems[_blendCombo.SelectedIndex].Mode);
            StateChanged?.Invoke();
        };

        _opacityBar.ValueChanged += value =>
        {
            if (_suppress) return;
            if (_opacityDragStart < 0) _opacityDragStart = _node.Opacity;

            // 拖曳期間即時預覽（不進 history），放開時一次 commit
            lock (_session.Document.SyncRoot)
            {
                _node.Opacity = (float)(value / 100);
            }
            _node.InvalidateAll();
        };
        _opacityBar.DragCompleted += value =>
        {
            if (_opacityDragStart < 0) return;
            var start = _opacityDragStart;
            _opacityDragStart = -1;

            var final = (float)(value / 100);
            if (Math.Abs(final - start) < 0.001f) return;

            var node = _node;
            _session.History.Push(new ActionHistoryEntry("圖層不透明度", _session.Document.Bounds,
                undo: _ => { node.Opacity = start; node.InvalidateAll(); },
                redo: _ => { node.Opacity = final; node.InvalidateAll(); }));
            StateChanged?.Invoke();
        };
    }

    private void CommitName()
    {
        var name = _nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || name == _node.Name) return;
        LayerCommands.Rename(_session.Document, _session.History, _node, name);
        StateChanged?.Invoke();
    }

    // ---- 調整圖層參數 ----

    private void BuildAdjustmentEditor()
    {
        _adjustmentParams.Children.Clear();
        _adjDragStart = null;
        if (_node is not AdjustmentLayer adj) return;

        var editor = new ParamEditor(adj.Adjustment, o => ((IAdjustment)o).Parameters);
        editor.Changed += current =>
        {
            _adjDragStart ??= adj.Adjustment;
            var updated = (IAdjustment)current;
            lock (_session.Document.SyncRoot)
            {
                adj.Adjustment = updated;
            }
            adj.InvalidateAll(); // 拖曳期間即時重合成（非破壞性核心體驗）
        };
        editor.Committed += _ =>
        {
            if (_adjDragStart == null) return;
            var start = _adjDragStart;
            _adjDragStart = null;
            LayerCommands.SetAdjustment(_session.Document, _session.History, adj, start, adj.Adjustment);
            StateChanged?.Invoke();
        };
        _adjustmentParams.Children.Add(editor);
    }

    // ---- 圖層效果堆疊（非破壞性；可重新調整／排序／開關／烙印／預設集） ----
    //
    // 這是核心功能，UI 走「管線卡片」：每一道效果一張卡（名稱＋參數摘要＋圖示動作），
    // 左側步驟編號用一條連線串起來 —— 由上而下＝最後套用 → 最先套用（與圖層堆疊同向）。

    private void BuildEffectsSection()
    {
        _effectsPanel.Children.Clear();
        if (_node is not RasterLayer layer) return;
        var doc = _session.Document;
        IReadOnlyList<LayerEffect> effects;
        lock (doc.SyncRoot) effects = layer.Effects;

        // 標題列：名稱＋數量膠囊；右側三顆主要動作
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new MaterialIcon { Kind = MaterialIconKind.AutoFix, Width = 15, Height = 15, Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "效果堆疊", FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        if (effects.Count > 0)
        {
            title.Children.Add(new Border
            {
                Background = AppTheme.HeaderBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = effects.Count.ToString(), FontSize = 10, Foreground = AppTheme.TextMutedBrush },
            });
        }

        var addButton = ActionButton(MaterialIconKind.Plus, "新增", "從效果／調整清單加一道（點分類展開）");
        addButton.Flyout = BuildAddFlyout(layer);
        var presetButton = ActionButton(MaterialIconKind.BookmarkOutline, "預設集", "套用／儲存整個堆疊");
        presetButton.Flyout = BuildPresetFlyout(layer, effects);
        var bakeButton = ActionButton(MaterialIconKind.Stamper, "烙印", "把堆疊結果寫進像素並清空堆疊（可復原）");
        bakeButton.IsEnabled = effects.Count > 0;
        bakeButton.Click += (_, _) =>
        {
            if (LayerEffectCommands.Bake(_session, layer)) StateChanged?.Invoke();
            SyncFromModel();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(addButton);
        buttons.Children.Add(presetButton);
        buttons.Children.Add(bakeButton);
        DockPanel.SetDock(buttons, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(buttons);
        header.Children.Add(title);
        _effectsPanel.Children.Add(header);

        if (effects.Count == 0)
        {
            // 空狀態：框＋引導，直接就地新增
            var emptyAdd = ActionButton(MaterialIconKind.Plus, "新增第一道效果", "從效果／調整清單加一道");
            emptyAdd.Flyout = BuildAddFlyout(layer);
            emptyAdd.HorizontalAlignment = HorizontalAlignment.Center;
            emptyAdd.Margin = new Thickness(0, 6, 0, 0);
            var empty = new StackPanel { Spacing = 4 };
            empty.Children.Add(new MaterialIcon
            {
                Kind = MaterialIconKind.LayersOutline, Width = 26, Height = 26,
                Foreground = AppTheme.TextMutedBrush, HorizontalAlignment = HorizontalAlignment.Center,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "這一層還沒有效果",
                FontSize = 12, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "從「調整」「效果」選單套用的會記錄在這裡，之後隨時可以回頭改參數、調順序或暫時關掉，像素不會被動到。",
                FontSize = 11, Foreground = AppTheme.TextMutedBrush,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            });
            empty.Children.Add(emptyAdd);
            _effectsPanel.Children.Add(new Border
            {
                BorderBrush = AppTheme.SeparatorBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 12),
                Child = empty,
            });
            return;
        }

        // 卡片清單（多道效果時內部捲動，視窗不無限長高）；順序用拖曳卡片調整
        var list = new StackPanel { Spacing = 0 };
        var drag = new ReorderDrag(this, layer, effects, list);
        for (var i = effects.Count - 1; i >= 0; i--)
        {
            var row = BuildEffectCard(layer, effects, i, drag);
            drag.Rows.Add(row);
            list.Children.Add(row);
        }
        _effectsPanel.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 270,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
    }

    /// <summary>
    /// 卡片拖曳排序：按住卡片空白處往上下拖，白色插入線指出會落在哪一格，放開才套用（一步 undo）。
    /// 插入位置用拖曳開始時各列中線的快照判定 —— 插入線本身會把下面的列推開，
    /// 即時量會在邊界來回抖。
    /// </summary>
    private sealed class ReorderDrag(LayerPropertiesWindow owner, RasterLayer layer, IReadOnlyList<LayerEffect> effects, StackPanel list)
    {
        public List<Control> Rows { get; } = new(); // 視覺順序（0 = 最上面 = 最後套用）

        private readonly Border _indicator = new()
        {
            Height = 2, Margin = new Thickness(28, 2, 0, 2), Background = Brushes.White, CornerRadius = new CornerRadius(1),
        };
        private Control? _row;
        private Point _start;         // 按下時在 list 座標的位置
        private double[] _midlines = [];
        private bool _dragging;
        private int _slot = -1;       // 插入位置（視覺順序，0..n）

        private const double Threshold = 4;

        public void Attach(Border card, Control row)
        {
            card.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
                if (e.Source is Visual src && src.FindAncestorOfType<Button>(true) != null) return;
                if (e.Source is Visual src2 && src2.FindAncestorOfType<CheckBox>(true) != null) return;
                _row = row;
                _start = e.GetPosition(list);
                _dragging = false;
                e.Pointer.Capture(card);
            };
            card.PointerMoved += (_, e) =>
            {
                if (_row != row) return;
                var pos = e.GetPosition(list);
                var dy = pos.Y - _start.Y;
                if (!_dragging)
                {
                    if (Math.Abs(dy) < Threshold) return;
                    _dragging = true;
                    _midlines = Rows.Select(r => r.Bounds.Y + r.Bounds.Height / 2).ToArray();
                    card.Opacity = 0.75;
                    row.ZIndex = 1;
                    card.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                }
                row.RenderTransform = new TranslateTransform(0, dy);
                UpdateSlot(pos.Y);
            };
            card.PointerReleased += (_, e) =>
            {
                if (_row != row) return;
                // 先把狀態讀出來：Capture(null) 會「同步」觸發 PointerCaptureLost → Reset，
                // 之後才讀 _dragging/_slot 就永遠是「沒在拖」，放開什麼都不會發生。
                var wasDragging = _dragging;
                var slot = _slot;
                Reset(card, row);
                e.Pointer.Capture(null);
                if (wasDragging && slot >= 0) Apply(row, slot);
            };
            card.PointerCaptureLost += (_, _) =>
            {
                if (_row == row) Reset(card, row);
            };
        }

        private void UpdateSlot(double y)
        {
            var from = Rows.IndexOf(_row!);
            // 不含被拖的那列：滑鼠在第幾條中線之下，就插在第幾格
            var slot = 0;
            for (var v = 0; v < Rows.Count; v++)
            {
                if (v == from) continue;
                if (y > _midlines[v]) slot++;
            }
            // 換算回「含自己」的視覺插入位置；落回原位（前後）就不顯示
            var visual = slot >= from ? slot + 1 : slot;
            if (visual == from || visual == from + 1) visual = -1;
            if (visual == _slot) return;
            _slot = visual;

            list.Children.Remove(_indicator);
            if (visual >= 0) list.Children.Insert(Math.Min(visual, list.Children.Count), _indicator);
        }

        private void Reset(Border card, Control row)
        {
            _row = null;
            _dragging = false;
            _slot = -1;
            list.Children.Remove(_indicator);
            row.RenderTransform = null;
            row.ZIndex = 0;
            card.Opacity = card.Tag is double o ? o : 1;
            card.Cursor = Cursor.Default;
        }

        private void Apply(Control row, int visualSlot)
        {
            var from = Rows.IndexOf(row);
            if (from < 0) return;
            var order = Rows.ToList();
            order.RemoveAt(from);
            var insertAt = visualSlot > from ? visualSlot - 1 : visualSlot;
            order.Insert(Math.Clamp(insertAt, 0, order.Count), row);

            // 視覺順序 → 堆疊順序（反向）
            var after = new List<LayerEffect>();
            for (var v = order.Count - 1; v >= 0; v--)
            {
                var i = effects.Count - 1 - Rows.IndexOf(order[v]);
                after.Add(effects[i]);
            }
            if (after.Select(e => e.Id).SequenceEqual(effects.Select(e => e.Id))) return;
            LayerEffectCommands.SetEffects(owner._session.Document, owner._session.History, layer, effects, after, "調整效果順序");
            owner.StateChanged?.Invoke();
            owner.SyncFromModel();
        }
    }

    /// <summary>一道效果的卡片：步驟編號（含上下連線）｜開關｜名稱＋參數摘要｜圖示動作。拖曳卡片可排序。</summary>
    private Control BuildEffectCard(RasterLayer layer, IReadOnlyList<LayerEffect> effects, int i, ReorderDrag drag)
    {
        var doc = _session.Document;
        var fx = effects[i];
        var isTop = i == effects.Count - 1;
        var isBottom = i == 0;
        var canEdit = fx.Effect.Parameters.Count > 0;

        // 左側 gutter：上下兩半各一段連線（首尾卡片只畫一半），編號圓點蓋在中間 —— 串成一條管線
        var gutter = new Grid { Width = 26, RowDefinitions = new RowDefinitions("*,*") };
        var lineBrush = new SolidColorBrush(AppTheme.TextMutedBrush.Color, 0.45);
        var lineUp = new Border
        {
            Width = 2, Background = lineBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = !isTop,
        };
        var lineDown = new Border
        {
            Width = 2, Background = lineBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = !isBottom,
        };
        Grid.SetRow(lineUp, 0);
        Grid.SetRow(lineDown, 1);
        var badge = new Border
        {
            Width = 20, Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = fx.Enabled ? Brushes.White : AppTheme.SeparatorBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10, FontWeight = FontWeight.Bold,
                Foreground = fx.Enabled ? Brushes.Black : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetRowSpan(badge, 2);
        gutter.Children.Add(lineUp);
        gutter.Children.Add(lineDown);
        gutter.Children.Add(badge);

        // 開關
        var enabled = new CheckBox
        {
            IsChecked = fx.Enabled,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
        };
        ToolTip.SetTip(enabled, fx.Enabled ? "暫時關掉這道效果" : "重新啟用");
        enabled.IsCheckedChanged += (_, _) =>
        {
            LayerEffectCommands.SetEnabled(doc, _session.History, layer, fx.Id, enabled.IsChecked == true);
            StateChanged?.Invoke();
            SyncFromModel();
        };

        // 名稱＋摘要
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        nameRow.Children.Add(new TextBlock
        {
            Text = fx.Name,
            FontSize = 12, FontWeight = FontWeight.Bold,
            Foreground = fx.Enabled ? AppTheme.TextBrush : AppTheme.TextMutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (fx.Mask != null)
        {
            var maskIcon = new MaterialIcon
            {
                Kind = MaterialIconKind.Selection, Width = 12, Height = 12,
                Foreground = AppTheme.TextMutedBrush, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(maskIcon, "只套用在當時的選取範圍內");
            nameRow.Children.Add(maskIcon);
        }
        if (!fx.Enabled)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = "已停用", FontSize = 10, Foreground = AppTheme.TextMutedBrush, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var summary = SummarizeParams(fx);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(nameRow);
        text.Children.Add(new TextBlock
        {
            Text = summary.Length > 0 ? summary : (canEdit ? "預設參數" : "沒有可調參數"),
            FontSize = 10.5,
            Foreground = AppTheme.TextMutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        ToolTip.SetTip(text, DescribeEffect(fx) + "\n拖曳卡片可調整順序；雙擊重新調整參數");

        // 圖示動作
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var edit = IconButton(MaterialIconKind.TuneVariant, "重新調整參數（即時預覽）");
        edit.IsEnabled = canEdit;
        edit.Click += async (_, _) =>
        {
            var main = Owner as MainWindow;
            if (main == null && Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow mw })
                main = mw;
            if (main == null) return;
            await main.EditLayerEffectAsync(layer, fx);
            SyncFromModel();
        };
        var remove = IconButton(MaterialIconKind.Close, "移除這道效果");
        remove.Click += (_, _) => { LayerEffectCommands.Remove(doc, _session.History, layer, fx.Id); StateChanged?.Invoke(); SyncFromModel(); };
        actions.Children.Add(edit);
        actions.Children.Add(remove);

        DockPanel.SetDock(enabled, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        var body = new DockPanel { Children = { enabled, actions, text } };
        var card = new Border
        {
            Background = AppTheme.InnerBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 5),
            Margin = new Thickness(0, 2),
            Opacity = fx.Enabled ? 1 : 0.6,
            Tag = fx.Enabled ? 1.0 : 0.6, // 拖曳結束還原用
            Child = body,
        };
        card.PointerEntered += (_, _) => card.Background = AppTheme.HeaderBrush;
        card.PointerExited += (_, _) => card.Background = AppTheme.InnerBrush;
        card.DoubleTapped += (_, _) =>
        {
            if (canEdit) edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };

        DockPanel.SetDock(gutter, Dock.Left);
        var row = new DockPanel { Children = { gutter, card } };
        drag.Attach(card, row);
        return row;
    }

    /// <summary>卡片第二行的參數摘要（只列參數，不重複名稱）。</summary>
    private static string SummarizeParams(LayerEffect fx)
    {
        var parts = new List<string>();
        foreach (var def in fx.Effect.Parameters)
        {
            switch (def)
            {
                case SliderParam s: parts.Add($"{s.Label} {s.Get(fx.Effect).ToString(s.Decimals > 0 ? "F" + s.Decimals : "0")}{s.Suffix}"); break;
                case AngleParam a: parts.Add($"{a.Label} {a.Get(fx.Effect):0}°"); break;
                case BoolParam b: if (b.Get(fx.Effect)) parts.Add(b.Label); break;
                case ChoiceParam c: parts.Add(c.Options[Math.Clamp(c.Get(fx.Effect), 0, c.Options.Length - 1)]); break;
            }
        }
        return string.Join(" · ", parts);
    }

    /// <summary>標題列的動作鈕：圖示＋文字。</summary>
    private static Button ActionButton(MaterialIconKind icon, string text, string tip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new MaterialIcon
        {
            Kind = icon, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.TextBrush,
        });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(7, 3),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>卡片上的小圖示鈕（透明底，hover 才有底色）。</summary>
    private static Button IconButton(MaterialIconKind icon, string tip)
    {
        var b = new Button
        {
            Content = new MaterialIcon { Kind = icon, Width = 15, Height = 15 },
            Width = 24, Height = 24,
            Padding = new Thickness(0),
            MinWidth = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static string DescribeEffect(LayerEffect fx)
    {
        var parts = new List<string>();
        foreach (var def in fx.Effect.Parameters)
        {
            switch (def)
            {
                case SliderParam s: parts.Add($"{s.Label} {s.Get(fx.Effect).ToString(s.Decimals > 0 ? "F" + s.Decimals : "0")}{s.Suffix}"); break;
                case AngleParam a: parts.Add($"{a.Label} {a.Get(fx.Effect):0}°"); break;
                case BoolParam b: parts.Add($"{b.Label} {(b.Get(fx.Effect) ? "開" : "關")}"); break;
                case ChoiceParam c: parts.Add($"{c.Label} {c.Options[Math.Clamp(c.Get(fx.Effect), 0, c.Options.Length - 1)]}"); break;
            }
        }
        var text = parts.Count == 0 ? fx.Name : $"{fx.Name}：{string.Join("、", parts)}";
        if (fx.Mask != null) text += "（限套用當時的選取範圍）";
        return text;
    }

    private Controls.ClickSubmenuMenuFlyout BuildAddFlyout(RasterLayer layer)
    {
        var flyout = new Controls.ClickSubmenuMenuFlyout();
        var adjust = new MenuItem { Header = "調整" };
        foreach (var entry in AdjustmentRegistry.All)
        {
            var e = entry;
            var item = new MenuItem { Header = e.DisplayName };
            item.Click += (_, _) => AddToStack(layer, new AdjustmentEffect(e.CreateDefault()), e.HasDialog);
            adjust.Items.Add(item);
        }
        flyout.Items.Add(adjust);
        foreach (var category in EffectRegistry.Categories)
        {
            var sub = new MenuItem { Header = category };
            foreach (var entry in EffectRegistry.InCategory(category))
            {
                var e = entry;
                var item = new MenuItem { Header = e.Name };
                item.Click += (_, _) => AddToStack(layer, e.Create(), true);
                sub.Items.Add(item);
            }
            flyout.Items.Add(sub);
        }
        return flyout;
    }

    private async void AddToStack(RasterLayer layer, IEffect effect, bool showDialog)
    {
        effect = EffectSerializer.WithPrimaryColor(effect, _session.Foreground);
        var entry = LayerEffect.Create(effect, _session.Selection?.Clone().Mask, _session.Foreground);
        if (!showDialog)
        {
            LayerEffectCommands.Add(_session.Document, _session.History, layer, entry);
            StateChanged?.Invoke();
            SyncFromModel();
            return;
        }
        var main = Owner as MainWindow;
        using var preview = new LayerEffectPreview(_session, layer, entry, isNew: true);
        var dialog = new EffectDialog(preview, effect, effect.Name);
        await dialog.ShowDialog(main ?? (Window)this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed) preview.Commit(dialog.Result);
        else preview.Cancel();
        StateChanged?.Invoke();
        SyncFromModel();
    }

    private Controls.ClickSubmenuMenuFlyout BuildPresetFlyout(RasterLayer layer, IReadOnlyList<LayerEffect> current)
    {
        var flyout = new Controls.ClickSubmenuMenuFlyout();
        var presets = EffectPresetStore.LoadAll();
        if (presets.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "（尚無預設集）", IsEnabled = false });
        }
        foreach (var preset in presets)
        {
            var p = preset;
            var item = new MenuItem { Header = p.Name };
            var apply = new MenuItem { Header = "套用（加在現有堆疊之後）" };
            apply.Click += (_, _) => ApplyPreset(layer, p, replace: false);
            var replaceItem = new MenuItem { Header = "取代目前堆疊" };
            replaceItem.Click += (_, _) => ApplyPreset(layer, p, replace: true);
            var delete = new MenuItem { Header = "刪除這個預設集" };
            delete.Click += (_, _) => { EffectPresetStore.Delete(p); SyncFromModel(); };
            item.Items.Add(apply);
            item.Items.Add(replaceItem);
            item.Items.Add(new Separator());
            item.Items.Add(delete);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var saveItem = new MenuItem { Header = "儲存目前堆疊為預設集…", IsEnabled = current.Count > 0 };
        saveItem.Click += async (_, _) =>
        {
            var prompt = new TextPromptDialog("儲存預設集", "名稱", _node.Name + " 效果");
            await prompt.ShowDialog(Owner as Window ?? this);
            if (!prompt.Confirmed) return;
            IReadOnlyList<LayerEffect> effects;
            lock (_session.Document.SyncRoot) effects = layer.Effects;
            EffectPresetStore.Save(prompt.Text, effects);
            SyncFromModel();
        };
        flyout.Items.Add(saveItem);
        var openFolder = new MenuItem { Header = "開啟預設集資料夾" };
        openFolder.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EffectPresetStore.FolderPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        };
        flyout.Items.Add(openFolder);
        return flyout;
    }

    private void ApplyPreset(RasterLayer layer, EffectPreset preset, bool replace)
    {
        var doc = _session.Document;
        var before = layer.Effects;
        var added = preset.Effects.Select(e =>
            LayerEffect.Create(e.Effect, null, _session.Foreground) with { Enabled = e.Enabled }).ToList();
        var after = replace ? added : before.Concat(added).ToList();
        LayerEffectCommands.SetEffects(doc, _session.History, layer, before, after, $"套用預設集：{preset.Name}");
        StateChanged?.Invoke();
        SyncFromModel();
    }

    // ---- 詳細資訊（唯讀） ----

    private void BuildDetails()
    {
        _detailRows.Children.Clear();

        var doc = _session.Document;

        AddDetail("類型", _node switch
        {
            GroupLayer => "群組",
            AdjustmentLayer a => $"調整圖層（{a.Adjustment.DisplayName}）",
            RasterLayer => "一般圖層",
            _ => "圖層",
        });

        switch (_node)
        {
            case RasterLayer raster:
            {
                // LayerNode.ContentBounds 是 tile 對齊的（256 倍數）保守值，拿來顯示會
                // 出現「比畫布還大」的怪數字；這裡掃精確邊界。
                SkiaSharp.SKRectI pixels;
                int tiles;
                lock (doc.SyncRoot)
                {
                    pixels = raster.Surface.ExactContentBounds();
                    tiles = raster.Surface.Tiles.Count;
                }
                if (!pixels.IsEmpty)
                {
                    pixels = new SkiaSharp.SKRectI(
                        pixels.Left + raster.Offset.X, pixels.Top + raster.Offset.Y,
                        pixels.Right + raster.Offset.X, pixels.Bottom + raster.Offset.Y);
                }

                AddDetail("像素範圍", pixels.IsEmpty
                    ? "（空）"
                    : $"{pixels.Width} × {pixels.Height} @ ({pixels.Left}, {pixels.Top})");
                if (raster.Offset != SkiaSharp.SKPointI.Empty)
                    AddDetail("圖層位移", $"({raster.Offset.X}, {raster.Offset.Y})");
                if (raster.HasElements)
                    AddDetail("文字物件", $"{raster.Elements.Count} 個");
                AddDetail("記憶體", $"{tiles} tiles（約 {tiles * Tile.BytesPerTile / (1024.0 * 1024.0):0.#} MB）");
                break;
            }

            case GroupLayer group:
            {
                int children;
                lock (doc.SyncRoot) children = group.Children.Count;
                AddDetail("子圖層", $"{children} 個");
                break;
            }

            case AdjustmentLayer:
                AddDetail("作用範圍", "同群組內下方的圖層");
                break;
        }

        AddDetail("畫布", $"{doc.Width} × {doc.Height}");
    }

    private void AddDetail(string label, string value)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Width = 62,
            Foreground = AppTheme.TextMutedBrush,
        };
        DockPanel.SetDock(labelText, Dock.Left);
        _detailRows.Children.Add(new DockPanel
        {
            Children =
            {
                labelText,
                new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap },
            },
        });
    }
}
