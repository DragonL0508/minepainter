using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
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
    private readonly StackPanel _effectsPanel = new() { Spacing = 4 };
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
        Width = 380;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

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

    private void BuildEffectsSection()
    {
        _effectsPanel.Children.Clear();
        if (_node is not RasterLayer layer) return;
        var doc = _session.Document;
        IReadOnlyList<LayerEffect> effects;
        lock (doc.SyncRoot) effects = layer.Effects;

        var title = new TextBlock
        {
            Text = effects.Count == 0 ? "效果堆疊" : $"效果堆疊（{effects.Count}）",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var addButton = SmallButton("＋ 新增", "從效果／調整清單加一筆");
        addButton.Flyout = BuildAddFlyout(layer);
        var presetButton = SmallButton("預設集", "套用／儲存整個堆疊");
        presetButton.Flyout = BuildPresetFlyout(layer, effects);
        var bakeButton = SmallButton("烙印", "把堆疊結果寫進像素並清空堆疊（可復原）");
        bakeButton.IsEnabled = effects.Count > 0;
        bakeButton.Click += (_, _) =>
        {
            if (LayerEffectCommands.Bake(_session, layer)) StateChanged?.Invoke();
            SyncFromModel();
        };
        var header = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(addButton);
        buttons.Children.Add(presetButton);
        buttons.Children.Add(bakeButton);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        _effectsPanel.Children.Add(header);

        if (effects.Count == 0)
        {
            _effectsPanel.Children.Add(new TextBlock
            {
                Text = "尚無效果。從「調整」「效果」選單套用的會記錄在這裡，之後可以回頭改參數、排序或關掉。",
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // 由上而下 = 由後套到先套（最上面是最後一道，和圖層堆疊的閱讀方向一致）
        for (var i = effects.Count - 1; i >= 0; i--)
        {
            var fx = effects[i];
            var row = new DockPanel { Height = 26 };

            var enabled = new CheckBox
            {
                IsChecked = fx.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            ToolTip.SetTip(enabled, "啟用／停用");
            enabled.IsCheckedChanged += (_, _) =>
            {
                LayerEffectCommands.SetEnabled(doc, _session.History, layer, fx.Id, enabled.IsChecked == true);
                StateChanged?.Invoke();
            };
            DockPanel.SetDock(enabled, Dock.Left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var canEdit = fx.Effect.Parameters.Count > 0;
            var edit = SmallButton("編輯", "重新調整參數（即時預覽）");
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
            var up = SmallButton("▲", "往上（更晚套用）");
            up.IsEnabled = i < effects.Count - 1;
            up.Click += (_, _) => { LayerEffectCommands.Move(doc, _session.History, layer, fx.Id, +1); StateChanged?.Invoke(); SyncFromModel(); };
            var down = SmallButton("▼", "往下（更早套用）");
            down.IsEnabled = i > 0;
            down.Click += (_, _) => { LayerEffectCommands.Move(doc, _session.History, layer, fx.Id, -1); StateChanged?.Invoke(); SyncFromModel(); };
            var remove = SmallButton("✕", "移除");
            remove.Click += (_, _) => { LayerEffectCommands.Remove(doc, _session.History, layer, fx.Id); StateChanged?.Invoke(); SyncFromModel(); };
            actions.Children.Add(edit);
            actions.Children.Add(up);
            actions.Children.Add(down);
            actions.Children.Add(remove);
            DockPanel.SetDock(actions, Dock.Right);

            var name = new TextBlock
            {
                Text = (fx.Mask != null ? "◪ " : "") + fx.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = fx.Enabled ? AppTheme.TextBrush : AppTheme.TextMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(name, DescribeEffect(fx));
            name.DoubleTapped += (_, _) => { if (canEdit) edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); };

            row.Children.Add(enabled);
            row.Children.Add(actions);
            row.Children.Add(name);
            _effectsPanel.Children.Add(row);
        }
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

    private static Button SmallButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(6, 2),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private Controls.AnimatedMenuFlyout BuildAddFlyout(RasterLayer layer)
    {
        var flyout = new Controls.AnimatedMenuFlyout();
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

    private Controls.AnimatedMenuFlyout BuildPresetFlyout(RasterLayer layer, IReadOnlyList<LayerEffect> current)
    {
        var flyout = new Controls.AnimatedMenuFlyout();
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
