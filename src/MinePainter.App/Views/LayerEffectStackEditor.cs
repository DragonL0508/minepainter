using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using Material.Icons;
using Material.Icons.Avalonia;
using Avalonia.Controls.Primitives;

namespace MinePainter.App.Views;

/// <summary>管理圖層效果卡片、排序手勢與堆疊指令；屬性視窗保留模型同步與預覽。</summary>
internal sealed class LayerEffectStackEditor
{
    private readonly Window _owner;
    private readonly EditorSession _session;
    private readonly LayerNode _node;
    private readonly bool _presetMode;
    private readonly Action _stateChanged;
    private readonly Action _syncFromModel;
    private readonly StackPanel _effectsPanel = new() { Spacing = 0 };
    private readonly Dictionary<Guid, Control> _cards = new();

    public Control View => _effectsPanel;

    public LayerEffectStackEditor(Window owner, EditorSession session, LayerNode node,
        bool presetMode, Action stateChanged, Action syncFromModel)
    {
        _owner = owner;
        _session = session;
        _node = node;
        _presetMode = presetMode;
        _stateChanged = stateChanged;
        _syncFromModel = syncFromModel;
    }

    // ---- 圖層效果堆疊（非破壞性；可重新調整／排序／開關／烙印／預設集） ----
    //
    // 這是核心功能，UI 走「管線卡片」：每一道效果一張卡（名稱＋參數摘要＋圖示動作），
    // 左側步驟編號用一條連線串起來 —— 由上而下＝最後套用 → 最先套用（與圖層堆疊同向）。

    public void Refresh()
    {
        // FLIP：重建前記每張卡片的位置，重建後從舊位置滑到新位置；新卡片淡入、拖曳排序不再「跳」一下
        var oldCardPositions = new Dictionary<Guid, Point>();
        foreach (var (id, card) in _cards)
            if (card.IsVisible && card.TranslatePoint(default, _effectsPanel) is { } pt) oldCardPositions[id] = pt;
        _cards.Clear();
        _effectsPanel.Children.Clear();
        if (_node is not { CanHaveEffects: true } layer) return;
        var doc = _session.Document;
        IReadOnlyList<LayerEffect> effects;
        lock (doc.SyncRoot) effects = layer.Effects;

        // 標題列：名稱＋數量膠囊；右側三顆主要動作
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new MaterialIcon { Kind = MaterialIconKind.AutoFix, Width = 15, Height = 15, Foreground = AppTheme.TextBrush, VerticalAlignment = VerticalAlignment.Center });
        var stackTitle = new TextBlock
        {
            Text = "效果堆疊", FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(stackTitle,
            "拖曳卡片可以調順序。\n" +
            "在圖層面板按住 Alt 拖曳圖層＝把它的效果複製到另一層（疊加）；再加 Shift＝取代那一層原本的效果。");
        title.Children.Add(stackTitle);
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
        // 烙印是「把結果寫回這層的像素」—— 群組沒有自己的像素表面，做不了（要先合併群組）
        var bakeButton = ActionButton(MaterialIconKind.Stamper, "烙印",
            layer is RasterLayer
                ? "把堆疊結果寫進像素並清空堆疊（可復原）"
                : "群組沒有自己的像素，不能烙印（先合併群組再烙印）");
        bakeButton.IsEnabled = effects.Count > 0 && layer is RasterLayer;
        bakeButton.Click += async (_, _) =>
        {
            // 烙印要把全解析度的結果寫進像素：畫面上可能是降解析度的預覽，這裡會整層重算
            if (layer is RasterLayer raster)
            {
                var baked = false;
                await ProgressDialog.RunAsync(_owner, "烙印效果",
                    _ => baked = LayerEffectCommands.Bake(_session, raster));
                if (baked) _stateChanged();
            }
            _syncFromModel();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(addButton);
        if (!_presetMode)
        {
            // 預設集編輯模式：這裡本身就是在編預設集，再存一次或把堆疊烙進暫存圖層都沒意義
            buttons.Children.Add(presetButton);
            buttons.Children.Add(bakeButton);
        }
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
            _cards[effects[i].Id] = row;
        }
        if (oldCardPositions.Count > 0)
        {
            var snapshot = _cards.ToList();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (id, row) in snapshot)
                {
                    if (row.TranslatePoint(default, _effectsPanel) is not { } now) continue;
                    if (oldCardPositions.TryGetValue(id, out var old))
                    {
                        var dy = old.Y - now.Y;
                        if (Math.Abs(dy) > 0.5) Motion.Slide(row, 0, dy);
                    }
                    else Motion.FadeSlideIn(row, "translateY(-6px)");
                }
            }, DispatcherPriority.Loaded);
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
    private sealed class ReorderDrag(LayerEffectStackEditor owner, LayerNode layer, IReadOnlyList<LayerEffect> effects, StackPanel list)
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
            if (visual >= 0)
            {
                list.Children.Insert(Math.Min(visual, list.Children.Count), _indicator);
                Motion.FadeSlideIn(_indicator, "scaleX(0.6)", Motion.Quick, RelativePoint.Center);
            }
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
            owner._stateChanged();
            owner._syncFromModel();
        }
    }

    /// <summary>一道效果的卡片：步驟編號（含上下連線）｜開關｜名稱＋參數摘要｜圖示動作。拖曳卡片可排序。</summary>
    private Control BuildEffectCard(LayerNode layer, IReadOnlyList<LayerEffect> effects, int i, ReorderDrag drag)
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
            _stateChanged();
            _syncFromModel();
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
            var main = _owner.Owner as MainWindow;
            if (main == null && Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow mw })
                main = mw;
            if (main == null) return;
            await main.EditLayerEffectAsync(layer, fx);
            _syncFromModel();
        };
        var remove = IconButton(MaterialIconKind.Close, "移除這道效果");
        remove.Click += (_, _) => { LayerEffectCommands.Remove(doc, _session.History, layer, fx.Id); _stateChanged(); _syncFromModel(); };
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

    private Controls.ClickSubmenuMenuFlyout BuildAddFlyout(LayerNode layer)
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
                item.Click += (_, _) => AddToStack(layer, Services.EffectParamMemory.Recall(e.Create(), _session.Foreground), true);
                sub.Items.Add(item);
            }
            flyout.Items.Add(sub);
        }
        return flyout;
    }

    private async void AddToStack(LayerNode layer, IEffect effect, bool showDialog)
    {
        var entry = LayerEffect.Create(effect, _session.Selection?.Clone().Mask, _session.Foreground);
        if (!showDialog)
        {
            LayerEffectCommands.Add(_session.Document, _session.History, layer, entry);
            _stateChanged();
            _syncFromModel();
            return;
        }
        var main = _owner.Owner as MainWindow;
        using var preview = new LayerEffectPreview(_session, layer, entry, isNew: true);
        var dialog = new EffectDialog(preview, effect, effect.Name);
        await dialog.ShowDialog(main ?? _owner);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            preview.Commit(dialog.Result);
            Services.EffectParamMemory.Remember(dialog.Result);
        }
        else preview.Cancel();
        _stateChanged();
        _syncFromModel();
    }

    /// <summary>
    /// 預設集鈕：只做「儲存」（套用／管理都在預設集面板做——那邊有資料夾與預覽）。
    /// 存進預設集面板目前選取的資料夾；面板沒開就存根目錄。
    /// </summary>
    private Controls.ClickSubmenuMenuFlyout BuildPresetFlyout(LayerNode layer, IReadOnlyList<LayerEffect> current)
    {
        var flyout = new Controls.ClickSubmenuMenuFlyout();
        var folder = PresetsPanelContent.ActiveFolder;
        var saveItem = new MenuItem
        {
            Header = folder.Length == 0 ? "儲存目前堆疊為預設集…（根目錄）" : $"儲存目前堆疊為預設集…（{folder}）",
            IsEnabled = current.Count > 0,
        };
        saveItem.Click += async (_, _) =>
        {
            var prompt = new TextPromptDialog("儲存預設集", "名稱", _node.Name + " 效果");
            await prompt.ShowDialog(_owner.Owner as Window ?? _owner);
            if (!prompt.Confirmed) return;
            IReadOnlyList<LayerEffect> effects;
            lock (_session.Document.SyncRoot) effects = layer.Effects;
            EffectPresetStore.Save(prompt.Text, effects, PresetsPanelContent.ActiveFolder);
            _syncFromModel();
        };
        flyout.Items.Add(saveItem);
        var hint = new MenuItem { Header = "套用／整理請用「預設集」面板（右上角開關）", IsEnabled = false };
        flyout.Items.Add(hint);
        return flyout;
    }

}
