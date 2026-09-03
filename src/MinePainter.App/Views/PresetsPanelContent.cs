using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Rendering;
using MinePainter.App.Services;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>
/// 浮動「預設集」面板（Premiere 的效果預設集那種）：左邊資料夾、右邊「Aa」預覽格。
/// 格子可拖到畫布（落在哪層就套到哪層）、拖到左邊的資料夾（搬移）；雙擊 = 套到目前圖層。
/// 庫本身是 %APPDATA%\MinePainter\EffectPresets 的資料夾結構（見 <see cref="EffectPresetStore"/>）。
/// </summary>
public sealed class PresetsPanelContent : UserControl
{
    /// <summary>拖曳資料的文字格式：<c>minepainter://effect-preset/</c> + 檔案路徑（跨視窗 OS 拖放也讀得到）。</summary>
    public const string DragPrefix = "minepainter://effect-preset/";

    /// <summary>正在被拖曳的預設集（同一個程式內，主視窗 Drop 時直接拿；null = 沒在拖）。</summary>
    public static EffectPreset? Dragging { get; private set; }

    private static PresetsPanelContent? _instance;

    /// <summary>
    /// 「現在該把新預設集存到哪」：面板看得到就是它選取的資料夾，面板沒開／沒選就是根目錄（""）。
    /// 圖層屬性視窗的「儲存預設集」用這個。
    /// </summary>
    public static string ActiveFolder =>
        _instance is { IsEffectivelyVisible: true } panel ? panel._currentFolder : "";

    private const int ThumbWidth = 84;
    private const int ThumbHeight = 60;

    private readonly ListBox _folderList;
    private readonly WrapPanel _grid;
    private readonly TextBox _search;

    private List<EffectPreset> _presets = new();
    private List<string> _folders = new();
    private string _currentFolder = "";
    private bool _suppress;
    private bool _dirty = true;

    /// <summary>縮圖快取：檔案路徑＋修改時間 → 點陣圖（改檔就重算）。</summary>
    private readonly Dictionary<string, WriteableBitmap> _thumbs = new();

    /// <summary>目前的編輯 session（套用／儲存要用）。</summary>
    public Func<EditorSession?>? SessionProvider { get; set; }

    public enum ApplyMode
    {
        /// <summary>沒堆疊直接套，有堆疊問覆蓋或疊加（雙擊走這條）。</summary>
        Ask,
        Append,
        Replace,
    }

    /// <summary>要求把預設集套到目前圖層。由主視窗執行並回報。</summary>
    public event Action<EffectPreset, ApplyMode>? ApplyRequested;

    /// <summary>給主視窗顯示 toast 的訊息。</summary>
    public event Action<string>? Notify;

    public PresetsPanelContent()
    {
        _instance = this;
        // ---- 工具列 ----
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        toolbar.Children.Add(ToolButton(MaterialIconKind.ContentSave, "把目前圖層的效果堆疊存成預設集（存進選取的資料夾）", (_, _) => _ = SaveCurrentAsync()));
        toolbar.Children.Add(ToolButton(MaterialIconKind.FolderPlusOutline, "在選取的資料夾底下新增資料夾", (_, _) => _ = NewFolderAsync()));
        toolbar.Children.Add(ToolButton(MaterialIconKind.FolderOpenOutline, "用檔案總管開啟預設集資料夾", (_, _) => OpenInExplorer()));
        toolbar.Children.Add(ToolButton(MaterialIconKind.Refresh, "重新整理", (_, _) => Reload()));

        _search = new TextBox
        {
            Watermark = "搜尋所有預設集…",
            FontSize = 11,
            MinHeight = 24,
            Height = 24,
            Padding = new Thickness(6, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        _search.TextChanged += (_, _) => RebuildGrid();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(toolbar, Dock.Left);
        top.Children.Add(toolbar);
        top.Children.Add(_search);

        // ---- 左：資料夾 ----
        _folderList = new ListBox
        {
            Background = AppTheme.InnerBrush,
            FontSize = 12,
            MinWidth = 96,
            Width = 118,
        };
        _folderList.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            _currentFolder = (_folderList.SelectedItem as ListBoxItem)?.Tag as string ?? "";
            RebuildGrid();
        };

        // ---- 右：預覽格 ----
        _grid = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        var right = new Border
        {
            Background = AppTheme.InnerBrush,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(6, 0, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _grid,
            },
        };
        // 拖到右邊空白處 = 搬進目前資料夾
        DragDrop.SetAllowDrop(right, true);
        right.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = PresetFrom(e.Data) is { } p && p.Folder != _currentFolder ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        right.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (PresetFrom(e.Data) is { } p) MoveTo(p, _currentFolder);
            e.Handled = true;
        });

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(_folderList, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(_folderList);
        body.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        Content = root;

        EffectPresetStore.Changed += () => Dispatcher.UIThread.Post(Reload);
        AttachedToVisualTree += (_, _) =>
        {
            if (_dirty) Reload();
        };
    }

    // ---- 資料載入 ----

    /// <summary>切到某個資料夾（相對路徑，"" = 根目錄）。</summary>
    public void ShowFolder(string folder)
    {
        _currentFolder = folder;
        Reload();
    }

    public void Reload()
    {
        _dirty = false;
        _presets = EffectPresetStore.LoadAll();
        _folders = EffectPresetStore.Folders();
        if (_currentFolder.Length > 0 && !_folders.Contains(_currentFolder)) _currentFolder = "";
        RebuildFolders();
        RebuildGrid();
    }

    private void RebuildFolders()
    {
        _suppress = true;
        _folderList.Items.Clear();
        ListBoxItem? selected = null;
        foreach (var folder in _folders.Prepend(""))
        {
            var item = FolderRow(folder);
            _folderList.Items.Add(item);
            if (folder == _currentFolder) selected = item;
        }
        _folderList.SelectedItem = selected ?? _folderList.Items[0];
        _suppress = false;
    }

    private ListBoxItem FolderRow(string folder)
    {
        var depth = folder.Length == 0 ? 0 : folder.Count(c => c == '/') + 1;
        var name = folder.Length == 0 ? "根目錄" : folder[(folder.LastIndexOf('/') + 1)..];
        var count = _presets.Count(p => p.Folder == folder);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new MaterialIcon
        {
            Kind = folder.Length == 0 ? MaterialIconKind.Home : MaterialIconKind.FolderOutline,
            Width = 13, Height = 13,
            Foreground = AppTheme.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (count > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = count.ToString(),
                FontSize = 10,
                Foreground = AppTheme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var item = new ListBoxItem
        {
            Content = row,
            Tag = folder,
            Padding = new Thickness(6 + depth * 10, 3, 6, 3),
            MinHeight = 0,
        };
        ToolTip.SetTip(item, folder.Length == 0 ? "根目錄（把預設集拖到這裡可搬回根目錄）" : folder + "（把預設集拖到這裡可搬進來）");

        // 資料夾列是拖放目標：預設集拖過來 = 搬進這個資料夾
        DragDrop.SetAllowDrop(item, true);
        item.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = PresetFrom(e.Data) is { } p && p.Folder != folder ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        item.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (PresetFrom(e.Data) is { } p) MoveTo(p, folder);
            e.Handled = true;
        });

        var menu = new ClickSubmenuMenuFlyout();
        var add = new MenuItem { Header = "新增子資料夾…" };
        add.Click += (_, _) => _ = NewFolderAsync(folder);
        menu.Items.Add(add);
        if (folder.Length > 0)
        {
            var rename = new MenuItem { Header = "重新命名…" };
            rename.Click += (_, _) => _ = RenameFolderAsync(folder);
            menu.Items.Add(rename);
            var delete = new MenuItem { Header = "刪除資料夾", IsEnabled = EffectPresetStore.FolderIsEmpty(folder) };
            ToolTip.SetTip(delete, "只能刪空的資料夾");
            delete.Click += (_, _) =>
            {
                if (!EffectPresetStore.DeleteFolder(folder)) Notify?.Invoke("資料夾不是空的，先把裡面的預設集搬走");
            };
            menu.Items.Add(delete);
        }
        item.ContextFlyout = menu;
        return item;
    }

    private void RebuildGrid()
    {
        _grid.Children.Clear();
        var query = _search.Text?.Trim() ?? "";
        var searching = query.Length > 0;
        IEnumerable<EffectPreset> shown = searching
            ? _presets.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                  p.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            : _presets.Where(p => p.Folder == _currentFolder);
        foreach (var preset in shown)
        {
            _grid.Children.Add(Tile(preset, showFolder: searching));
        }
    }

    // ---- 預設集格子 ----

    private Control Tile(EffectPreset preset, bool showFolder)
    {
        var image = new Image
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            Stretch = Stretch.None,
        };
        var imageBox = new Border
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromUInt32(0xFF7C7C82)),
            Child = image,
        };
        LoadThumb(preset, image);

        var label = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ThumbWidth,
            Margin = new Thickness(0, 3, 0, 0),
        };
        var stack = new StackPanel { Children = { imageBox, label } };
        if (showFolder && preset.Folder.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = preset.Folder,
                FontSize = 9,
                Foreground = AppTheme.TextMutedBrush,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ThumbWidth,
            });
        }

        var tile = new Border
        {
            Child = stack,
            Padding = new Thickness(5),
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        var effects = preset.Effects.Count == 0
            ? "（空堆疊）"
            : string.Join("、", preset.Effects.Select(e => e.Enabled ? e.Effect.Name : e.Effect.Name + "（關）"));
        ToolTip.SetTip(tile, $"{preset.DisplayPath}\n{effects}");

        tile.PointerEntered += (_, _) => tile.Background = AppTheme.HeaderBrush;
        tile.PointerExited += (_, _) => tile.Background = Brushes.Transparent;

        Point? pressAt = null;
        tile.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(tile);
            if (!pt.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount >= 2)
            {
                pressAt = null;
                ApplyRequested?.Invoke(preset, ApplyMode.Ask);
                e.Handled = true;
                return;
            }
            pressAt = pt.Position;
        };
        tile.PointerReleased += (_, _) => pressAt = null;
        tile.PointerMoved += async (_, e) =>
        {
            if (pressAt is not { } start) return;
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed)
            {
                pressAt = null;
                return;
            }
            var pos = e.GetPosition(tile);
            if (Math.Abs(pos.X - start.X) < 5 && Math.Abs(pos.Y - start.Y) < 5) return;
            pressAt = null;
            await BeginDrag(preset, e, image.Source);
        };

        // 右鍵選單（編輯在最上面）
        var menu = new ClickSubmenuMenuFlyout();
        var edit = new MenuItem { Header = "編輯…" };
        edit.Click += (_, _) =>
        {
            if (Owner() is { } owner) PresetEditor.Edit(owner, preset, msg => Notify?.Invoke(msg));
        };
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        var apply = new MenuItem { Header = "套用到目前圖層（加在堆疊之後）" };
        apply.Click += (_, _) => ApplyRequested?.Invoke(preset, ApplyMode.Append);
        var replace = new MenuItem { Header = "取代目前圖層的堆疊" };
        replace.Click += (_, _) => ApplyRequested?.Invoke(preset, ApplyMode.Replace);
        var rename = new MenuItem { Header = "重新命名…" };
        rename.Click += (_, _) => _ = RenameAsync(preset);
        var move = new MenuItem { Header = "移到資料夾" };
        foreach (var folder in _folders.Prepend(""))
        {
            var target = folder;
            var mi = new MenuItem
            {
                Header = folder.Length == 0 ? "根目錄" : folder,
                IsEnabled = folder != preset.Folder,
            };
            mi.Click += (_, _) => MoveTo(preset, target);
            move.Items.Add(mi);
        }
        var duplicate = new MenuItem { Header = "建立複本" };
        duplicate.Click += (_, _) =>
        {
            var name = preset.Name + " 複本";
            var n = 2;
            while (_presets.Any(p => p.Folder == preset.Folder && p.Name == name)) name = $"{preset.Name} 複本 {n++}";
            EffectPresetStore.SaveEntries(name, preset.Effects, preset.Folder);
        };
        var delete = new MenuItem { Header = "刪除" };
        delete.Click += (_, _) =>
        {
            EffectPresetStore.Delete(preset);
            Notify?.Invoke($"已刪除預設集「{preset.Name}」");
        };
        menu.Items.Add(apply);
        menu.Items.Add(replace);
        menu.Items.Add(new Separator());
        menu.Items.Add(rename);
        menu.Items.Add(move);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        tile.ContextFlyout = menu;
        return tile;
    }

    private void LoadThumb(EffectPreset preset, Image image)
    {
        string key;
        try
        {
            key = preset.Path + "|" + File.GetLastWriteTimeUtc(preset.Path).Ticks;
        }
        catch (Exception)
        {
            key = preset.Path;
        }
        if (_thumbs.TryGetValue(key, out var cached))
        {
            image.Source = cached;
            return;
        }
        // 同一路徑的舊縮圖丟掉（改檔後重算）
        foreach (var old in _thumbs.Keys.Where(k => k.StartsWith(preset.Path + "|", StringComparison.Ordinal)).ToList())
            _thumbs.Remove(old);

        Task.Run(() =>
        {
            try
            {
                var pixels = PresetPreview.Compute(preset, ThumbWidth, ThumbHeight);
                Dispatcher.UIThread.Post(() =>
                {
                    var bmp = PresetPreview.ToBitmap(pixels, ThumbWidth, ThumbHeight);
                    _thumbs[key] = bmp;
                    image.Source = bmp;
                });
            }
            catch (Exception)
            {
                // 縮圖算壞了就留空底
            }
        });
    }

    // ---- 拖曳 ----

    private static async Task BeginDrag(EffectPreset preset, PointerEventArgs e, IImage? thumbnail)
    {
        var data = new DataObject();
        data.Set(DataFormats.Text, DragPrefix + preset.Path);
        Dragging = preset;
        // OS 拖放沒有拖曳影像：自己開一個跟著游標走的殘影小窗（縮圖＋名稱，平滑跟隨、放開時淡出）
        var ghost = DragGhostWindow.Start(thumbnail, preset.Name);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception)
        {
        }
        finally
        {
            Dragging = null;
            ghost?.Finish();
        }
    }

    /// <summary>從拖放資料認出預設集（同程式內直接拿 <see cref="Dragging"/>；否則解析文字格式）。</summary>
    public static EffectPreset? PresetFrom(IDataObject data)
    {
        if (Dragging != null) return Dragging;
        try
        {
            if (data.GetText() is { } text && text.StartsWith(DragPrefix, StringComparison.Ordinal))
            {
                var path = text[DragPrefix.Length..];
                if (File.Exists(path)) return EffectPresetStore.LoadFile(path);
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    // ---- 操作 ----

    private void MoveTo(EffectPreset preset, string folder)
    {
        if (preset.Folder == folder) return;
        if (!EffectPresetStore.Move(preset, folder))
            Notify?.Invoke("目標資料夾已有同名的預設集");
    }

    private async Task SaveCurrentAsync()
    {
        var session = SessionProvider?.Invoke();
        if (session == null)
        {
            Notify?.Invoke("先開一份文件");
            return;
        }
        RasterLayer? layer;
        IReadOnlyList<Core.Effects.LayerEffect> effects;
        lock (session.Document.SyncRoot)
        {
            layer = session.Document.ActiveLayer as RasterLayer;
            effects = layer?.Effects ?? Array.Empty<Core.Effects.LayerEffect>();
        }
        if (layer == null)
        {
            Notify?.Invoke("目前圖層不是點陣圖層");
            return;
        }
        if (effects.Count == 0)
        {
            Notify?.Invoke("目前圖層沒有效果堆疊可以存");
            return;
        }
        if (Owner() is not { } owner) return;
        var prompt = new TextPromptDialog("儲存預設集", "名稱", layer.Name + " 效果");
        await prompt.ShowDialog(owner);
        if (!prompt.Confirmed) return;
        EffectPresetStore.Save(prompt.Text, effects, _currentFolder);
        Notify?.Invoke($"已儲存預設集「{prompt.Text}」");
    }

    private async Task NewFolderAsync(string? parent = null)
    {
        if (Owner() is not { } owner) return;
        parent ??= _currentFolder;
        var prompt = new TextPromptDialog("新增資料夾", parent.Length == 0 ? "名稱（放在根目錄）" : $"名稱（放在 {parent} 底下）", "新資料夾");
        await prompt.ShowDialog(owner);
        if (!prompt.Confirmed) return;
        var rel = EffectPresetStore.CreateFolder(parent, prompt.Text);
        if (rel == null)
        {
            Notify?.Invoke("建立資料夾失敗");
            return;
        }
        _currentFolder = rel;
        Reload();
    }

    private async Task RenameFolderAsync(string folder)
    {
        if (Owner() is not { } owner) return;
        var prompt = new TextPromptDialog("重新命名資料夾", "名稱", folder[(folder.LastIndexOf('/') + 1)..]);
        await prompt.ShowDialog(owner);
        if (!prompt.Confirmed) return;
        var rel = EffectPresetStore.RenameFolder(folder, prompt.Text);
        if (rel == null)
        {
            Notify?.Invoke("改名失敗（同名資料夾已存在？）");
            return;
        }
        if (_currentFolder == folder || _currentFolder.StartsWith(folder + "/", StringComparison.Ordinal))
            _currentFolder = rel + _currentFolder[folder.Length..];
        Reload();
    }

    private async Task RenameAsync(EffectPreset preset)
    {
        if (Owner() is not { } owner) return;
        var prompt = new TextPromptDialog("重新命名預設集", "名稱", preset.Name);
        await prompt.ShowDialog(owner);
        if (!prompt.Confirmed || prompt.Text == preset.Name) return;
        if (!EffectPresetStore.Rename(preset, prompt.Text)) Notify?.Invoke("改名失敗（同名預設集已存在？）");
    }

    private void OpenInExplorer()
    {
        try
        {
            var dir = EffectPresetStore.AbsoluteFolder(_currentFolder);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    private static Button ToolButton(MaterialIconKind icon, string tip, EventHandler<RoutedEventArgs> onClick)
    {
        var b = new Button
        {
            Content = new MaterialIcon { Kind = icon, Width = 15, Height = 15 },
            Padding = new Thickness(5, 3),
            MinWidth = 0,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        b.Click += onClick;
        return b;
    }
}
