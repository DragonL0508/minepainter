using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;

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
        (BlendMode.LinearBurn, "線性加深"), (BlendMode.LinearLight, "線性光源"), (BlendMode.VividLight, "強烈光源"),
        (BlendMode.PinLight, "小光源"), (BlendMode.HardMix, "實色疊印混合"), (BlendMode.DarkerColor, "顏色變暗"),
        (BlendMode.LighterColor, "顏色變亮"), (BlendMode.Subtract, "減去"), (BlendMode.Divide, "分割"),
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
    private readonly LayerEffectStackEditor _effects;
    private readonly StackPanel _detailRows = new() { Spacing = 3 };
    private Border _root = null!;

    private float _opacityDragStart = -1;
    private IAdjustment? _adjDragStart;
    private bool _closing;
    private readonly CheckBox _passThrough = new() { Content = "群組直通混合", Name = "PassThrough" };
    private readonly CheckBox[] _channels = [new() { Content = "R" }, new() { Content = "G" }, new() { Content = "B" }];
    private readonly CheckBox _maskEnabled = new() { Content = "啟用遮色片", Name = "MaskEnabled" };
    private readonly CheckBox _maskInverted = new() { Content = "反轉", Name = "MaskInverted" };
    private readonly BarSlider _maskDensity = new() { Label = "遮色片濃度", Minimum = 0, Maximum = 100, DefaultValue = 100, Suffix = "%", Name = "MaskDensity" };
    private readonly BarSlider _maskFeather = new() { Label = "羽化", Minimum = 0, Maximum = 1000, DefaultValue = 0, Suffix = "px", Decimals = 1, Name = "MaskFeather" };
    private readonly StackPanel _maskControls = new() { Spacing = 4 };
    private LayerMask? _maskDragStart;

    /// <summary>圖層屬性變更後發出（讓 MainWindow 刷新 undo 選單等）。</summary>
    public event Action? StateChanged;

    /// <summary>預設集編輯模式：開在暫存文件的「Aa」文字層上，只留名稱與效果堆疊（見 PresetEditor）。</summary>
    private readonly bool _presetMode;

    public LayerPropertiesWindow(EditorSession session, LayerNode node, bool presetMode = false)
    {
        _session = session;
        _node = node;
        _presetMode = presetMode;
        _effects = new LayerEffectStackEditor(this, session, node, presetMode,
            () => StateChanged?.Invoke(), SyncFromModel);

        Title = presetMode ? "編輯預設集" : "圖層屬性";
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
            Text = _presetMode ? $"編輯預設集 — {node.Name}" : $"圖層屬性 — {node.Name}",
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
            // 標題列是「可拖曳」的 SizeAll 游標，會被子元素繼承；✕ 上面要蓋回一般游標，
            // 不然滑上去看起來還是在拖視窗，不像按得下去的鈕
            Cursor = new Cursor(StandardCursorType.Arrow),
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

        if (node is not AdjustmentLayer && !_presetMode)
            body.Children.Add(LabeledRow("混合", _blendCombo));
        if (!_presetMode) body.Children.Add(_opacityBar);
        if (!_presetMode) body.Children.Add(BuildAdvancedProperties());

        if (node is AdjustmentLayer)
        {
            body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
            body.Children.Add(_adjustmentParams);
        }

        // 效果堆疊：一般圖層與群組都有（群組＝整組合成後套一次；調整圖層沒有自己的像素）
        if (node.CanHaveEffects)
        {
            body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
            body.Children.Add(_effects.View);
        }

        if (!_presetMode)
        {
            body.Children.Add(new Separator { Margin = new Thickness(0, 3) });
            body.Children.Add(_detailRows);
        }

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
        CommitMaskDrag();
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
        _passThrough.IsChecked = _node is GroupLayer { IsPassThrough: true };
        for (var channel = 0; channel < 3; channel++)
            _channels[channel].IsChecked = (_node.RestrictedChannels & (1 << channel)) == 0;
        _maskControls.IsVisible = _node.Mask != null;
        if (_node.Mask is { } mask && _maskDragStart == null)
        {
            _maskEnabled.IsChecked = mask.Enabled;
            _maskInverted.IsChecked = mask.Inverted;
            _maskDensity.Value = mask.Density * 100;
            _maskFeather.Value = mask.Feather;
        }
        _suppress = false;

        _preview.Source = Rendering.LayerThumbnail.Render(_session.Document, _node, 176, 132);
        BuildAdjustmentEditor();
        _effects.Refresh();
        BuildDetails();
    }

    private void WireEvents()
    {
        var propertyNode = _node;
        _passThrough.IsCheckedChanged += (_, _) =>
        {
            if (_suppress || _node is not GroupLayer group) return;
            ChangeAdvancedProperty("群組直通混合", group.IsPassThrough, _passThrough.IsChecked == true,
                value => group.IsPassThrough = value);
        };
        for (var i = 0; i < _channels.Length; i++)
        {
            var channel = i;
            _channels[i].IsCheckedChanged += (_, _) =>
            {
                if (_suppress) return;
                var bit = 1 << channel;
                var updated = _channels[channel].IsChecked == true
                    ? _node.RestrictedChannels & ~bit : _node.RestrictedChannels | bit;
                ChangeAdvancedProperty("混合色版", _node.RestrictedChannels, updated, value => propertyNode.RestrictedChannels = value);
            };
        }
        _maskEnabled.IsCheckedChanged += (_, _) => ChangeMaskToggle(mask => mask with { Enabled = _maskEnabled.IsChecked == true });
        _maskInverted.IsCheckedChanged += (_, _) => ChangeMaskToggle(mask => mask with { Inverted = _maskInverted.IsChecked == true });
        _maskDensity.ValueChanged += value => PreviewMask(mask => mask with { Density = (float)(value / 100) });
        _maskFeather.ValueChanged += value => PreviewMask(mask => mask with { Feather = (float)value });
        _maskDensity.DragCompleted += _ => CommitMaskDrag();
        _maskFeather.DragCompleted += _ => CommitMaskDrag();
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

    private Control BuildAdvancedProperties()
    {
        var panel = new StackPanel { Spacing = 4 };
        if (_node is GroupLayer) panel.Children.Add(_passThrough);
        var channels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        channels.Children.Add(new TextBlock { Text = "混合色版", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        foreach (var channel in _channels) channels.Children.Add(channel);
        panel.Children.Add(channels);
        _maskControls.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, Children = { _maskEnabled, _maskInverted },
        });
        _maskControls.Children.Add(_maskDensity);
        _maskControls.Children.Add(_maskFeather);
        panel.Children.Add(_maskControls);
        return new Expander { Header = "進階混合與遮色片", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private void ChangeAdvancedProperty<T>(string label, T before, T after, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        var doc = _session.Document;
        var node = _node;
        lock (doc.SyncRoot) { apply(after); node.InvalidateAll(); }
        _session.History.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: _ => { apply(before); node.InvalidateAll(); },
            redo: _ => { apply(after); node.InvalidateAll(); }));
        StateChanged?.Invoke();
    }

    private void ChangeMaskToggle(Func<LayerMask, LayerMask> update)
    {
        if (_suppress || _node.Mask == null) return;
        CommitMaskDrag();
        var before = _node.Mask;
        var node = _node;
        ChangeAdvancedProperty("遮色片設定", before, update(before), value => node.Mask = value);
    }

    private void PreviewMask(Func<LayerMask, LayerMask> update)
    {
        if (_suppress || _node.Mask == null) return;
        _maskDragStart ??= _node.Mask;
        lock (_session.Document.SyncRoot)
        {
            _node.Mask = update(_node.Mask);
            _node.InvalidateAll();
        }
    }

    private void CommitMaskDrag()
    {
        if (_maskDragStart is not { } before) return;
        _maskDragStart = null;
        var after = _node.Mask;
        if (after == null || before.Density == after.Density && before.Feather == after.Feather) return;
        var node = _node;
        ChangeAdvancedProperty("遮色片設定", before, after, value => node.Mask = value);
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
