using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;

public partial class MainWindow
{
    // ---- 文件分頁（paint.net 的多文件模式）----

    /// <summary>一個開啟中的文件：session + 檔案身分 + dirty 狀態 + 各自的視口與分頁 UI。</summary>
    private sealed class DocumentTab
    {
        public required EditorSession Session { get; init; }
        public string? FilePath;      // 目前的 .mpp 路徑（null = 尚未存過）
        public string? ImportedName;  // 匯入來源的檔名（.pdn／.psd／影像）；只用於標題與存檔預設名
        public bool IsDirty;
        public int ChangeCount;       // Interlocked 累計；背景存檔期間的編輯靠它保住 dirty 旗標
        public Action? DirtyHandler;  // 關分頁時解除訂閱用
        public Action? SizeHandler;
        public Rendering.ViewportTransform? Viewport; // 切到背景時保存，切回來還原
        public Border TabItem = null!;
        public TextBlock TabLabel = null!;
        public Image Thumb = null!;
        public int ThumbChangeCount = -1; // 上次畫縮圖時的 ChangeCount（-1 = 還沒畫過）

        public string Name => FilePath != null ? Path.GetFileName(FilePath) : ImportedName ?? "未命名";
    }

    private readonly List<DocumentTab> _tabs = new();
    private DocumentTab? _activeTab;

    // ---- 文件生命週期（分頁） ----

    /// <summary>開新文件 = 開新分頁並切換過去（多專案同時開啟）。</summary>
    private void SetDocument(
        MinePainter.Core.Documents.Document doc, string? mppPath = null, string? importedName = null)
    {
        var session = new EditorSession(doc);
        var tab = new DocumentTab { Session = session, FilePath = mppPath, ImportedName = importedName };
        tab.DirtyHandler = () => MarkTabDirty(tab);
        session.History.Changed += tab.DirtyHandler;
        // 畫布內的文字編輯是 UI 端狀態，Core 不知道它的存在；
        // 註冊成 IPendingEdit，undo/redo/指令/存檔就都會自動先落地它
        session.RegisterPendingEdit(new CanvasTextPendingEdit(this));
        // 畫布尺寸的唯一真相來源 —— 裁切/旋轉/調整大小「以及它們的 undo」都會走到這裡。
        // 只在各個 handler 裡更新標籤會漏掉 undo/redo。
        tab.SizeHandler = () => OnDocumentSizeChanged(tab);
        doc.SizeChanged += tab.SizeHandler;
        session.Notified += msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => Toasts.Show(msg));

        _tabs.Add(tab);
        BuildTabItem(tab);
        ActivateTab(tab);
    }

    /// <summary>
    /// 立即切換作用中分頁（無動畫）：保存舊視口、接上所有面板。
    /// 程式流程（關分頁、關窗詢問、開新文件）走這個，之後的邏輯才能同步依賴 _activeTab。
    /// </summary>
    private void ActivateTab(DocumentTab tab)
    {
        _pendingSwitch = null; // 蓋掉進行中的動畫切換
        SnapCanvasOpacity();

        if (ReferenceEquals(tab, _activeTab)) return;

        if (_activeTab != null)
        {
            // 畫布內文字編輯是跨分頁的 UI overlay，切走前先落地；
            // 浮動選取內容屬於 session（IPendingEdit），留在背景分頁沒有問題。
            CommitCanvasTextEdit();
            _activeTab.Viewport = Canvas.SaveViewport();
            RefreshTabThumbnail(_activeTab);
        }

        var previous = _activeTab;
        _activeTab = tab;
        var session = tab.Session;
        session.Compositor.Resume(); // 切回前景：重新排隊合成（切走時丟掉了）
        Canvas.SetSession(session, tab.Viewport);
        _layersContent.SetSession(session);
        _historyContent.SetSession(session);
        _paletteContent.SetColor(session.Foreground);
        ApplyBrushOptions();
        ApplyShapeOptions();
        ApplyTextOptions();
        ApplyMoveOptions();
        SelectTool(_currentToolKey);
        UpdateTitle();
        UpdateViewportStatus();
        DocSizeLabel.Text = DocSizeText(session.Document);
        RefreshUiState();
        UpdateTabVisuals();
        RefreshTabThumbnail(tab);
        Canvas.Focus();

        // 畫布已經切走，舊分頁的合成快取（整份文件，一格 256 KB）就是純浪費 —— 丟掉。
        // 要在 SetSession 之後才做，退役的影像也走全域佇列延後釋放，
        // 不然這一幀還在畫它的 render thread 會撞上。
        if (previous != null && !ReferenceEquals(previous, tab))
        {
            previous.Session.Compositor.Suspend();
            TilePool.Shared.Trim(64); // 剛還回來一大批，留一點週轉就好
        }
    }

    // ---- 分頁切換動畫（快速 fade out → 換內容 → fade in） ----
    // fade 由 CanvasView.ContentFade 在 draw op 裡自己套（外圍底色不動），
    // 不碰 Visual.Opacity —— Opacity=0 時 Avalonia 會剔除子樹，畫面會閃黑。

    private DocumentTab? _pendingSwitch;

    private void InitCanvasFade()
    {
        // ContentFade 路徑不需要 Transitions；保留方法讓建構流程不變
    }

    /// <summary>點分頁的切換：Quick 淡出 → 換 session → Base 淡入。</summary>
    private void ActivateTabAnimated(DocumentTab tab)
    {
        if (ReferenceEquals(tab, _activeTab) && _pendingSwitch == null) return;

        // 連點只記最後一個目標；淡出已經在跑就不重排
        var alreadyFading = _pendingSwitch != null;
        _pendingSwitch = tab;
        if (alreadyFading) return;

        Canvas.BeginContentFade(0, (int)Motion.Quick.TotalMilliseconds);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            var target = _pendingSwitch;
            if (target == null) return; // 期間被同步切換蓋掉了
            _pendingSwitch = null;

            if (!ReferenceEquals(target, _activeTab)) ActivateTab(target);

            // ActivateTab 會把 fade snap 回 1；這裡壓回 0 再淡入到 1
            Canvas.SnapContentFade(0);
            Canvas.BeginContentFade(1, (int)Motion.Base.TotalMilliseconds);
        }, Motion.Quick + TimeSpan.FromMilliseconds(5));
    }

    /// <summary>不經動畫直接把畫布內容透明度復位（同步切換路徑用）。</summary>
    private void SnapCanvasOpacity() => Canvas.SnapContentFade(1);

    /// <summary>關閉分頁（dirty 先問存檔）。回傳 false = 使用者取消。</summary>
    private async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (tab.IsDirty)
        {
            ActivateTab(tab); // 讓使用者看見要存的是哪一份
            var choice = await ShowUnsavedDialog(tab.Name);
            if (choice == UnsavedChoice.Cancel) return false;
            if (choice == UnsavedChoice.Save && !await SaveAsync(saveAs: false)) return false;
        }

        var index = _tabs.IndexOf(tab);
        if (index < 0) return true; // 已被關掉（連點）
        _tabs.RemoveAt(index);
        // 與新分頁的淡入對稱：縮小淡出後才從列上拿掉
        Motion.FadeOut(tab.TabItem, () => TabStrip.Children.Remove(tab.TabItem), "scale(0.9)");
        tab.Session.History.Changed -= tab.DirtyHandler;
        tab.Session.Document.SizeChanged -= tab.SizeHandler;

        if (ReferenceEquals(tab, _activeTab))
        {
            _activeTab = null;
            if (_tabs.Count > 0)
            {
                ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                // 最後一個分頁關掉：進入零文件狀態（畫布不渲染），直接跳「新增影像」讓使用者接著開新的
                CommitCanvasTextEdit(cancel: true);
                Canvas.ClearSession();
                _layersContent.SetSession(null);
                _historyContent.SetSession(null);
                UpdateTitle();
                DocSizeLabel.Text = "";
                CursorPosLabel.Text = "";
                UpdateTabVisuals();
                OnNewClicked(null, new RoutedEventArgs()); // 取消的話就停在零文件狀態
            }
        }
        else
        {
            UpdateTabVisuals();
        }

        tab.Session.Dispose(); // 畫布已切走，這裡才是唯一的釋放點
        TilePool.Shared.Trim(64); // 整份文件的 tile 剛還回池子，別讓它留著
        return true;
    }

    private void MarkTabDirty(DocumentTab tab)
    {
        Interlocked.Increment(ref tab.ChangeCount); // History.Changed 可能來自非 UI 執行緒
        if (tab.IsDirty) return;
        tab.IsDirty = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateTitle();
            UpdateTabVisuals();
        });
    }

    /// <summary>文件尺寸變了（含 undo/redo）：同步狀態列與捲動範圍。可能在非 UI 執行緒發出。</summary>
    private void OnDocumentSizeChanged(DocumentTab tab) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(tab, _activeTab)) return;
            DocSizeLabel.Text = DocSizeText(tab.Session.Document);
            UpdateViewportStatus();
            RefreshUiState();
        });

    private void UpdateTitle() =>
        Title = _activeTab is { } tab
            ? $"MinePainter — {tab.Name}{(tab.IsDirty ? " *" : "")}"
            : "MinePainter";

    // ---- 分頁條 UI ----

    private Button _newTabButton = null!;

    /// <summary>分頁條末端的「＋」（＝檔案 → 新增）。分頁項插在它前面。</summary>
    private void BuildTabStrip()
    {
        _newTabButton = new Button
        {
            Content = "＋",
            FontSize = 13,
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(_newTabButton, "新增影像");
        _newTabButton.Click += OnNewClicked;
        TabStrip.Children.Add(_newTabButton);
    }

    private void BuildTabItem(DocumentTab tab)
    {
        // 縮圖預覽（棋盤感不需要，襯個內凹底色就好）
        tab.Thumb = new Image
        {
            Width = 46,
            Height = 34,
            Stretch = Stretch.Uniform,
        };
        var thumbFrame = new Border
        {
            Background = AppTheme.InnerBrush,
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Width = 50,
            Height = 38,
            Child = tab.Thumb,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        tab.TabLabel = new TextBlock
        {
            FontSize = 12,
            MaxWidth = 130,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 9,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        closeButton.Click += async (_, _) => await CloseTabAsync(tab);

        tab.TabItem = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 4),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 7,
                Children = { thumbFrame, tab.TabLabel, closeButton },
            },
        };
        tab.TabItem.PointerPressed += (_, e) =>
        {
            // 按在 ✕ 上不要先切過去（不然關背景分頁會先閃一下）
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<Button>(true) != null) return;
            if (e.GetCurrentPoint(tab.TabItem).Properties.IsMiddleButtonPressed)
            {
                _ = CloseTabAsync(tab); // 中鍵關閉（瀏覽器慣例）
                return;
            }
            ActivateTabAnimated(tab);
        };

        // 選中／未選中的底色平滑切換；新分頁從下方微微浮上來
        Motion.BrushTransition(tab.TabItem, Border.BackgroundProperty);
        TabStrip.Children.Insert(TabStrip.Children.Count - 1, tab.TabItem); // 「＋」永遠在最後
        Motion.FadeSlideIn(tab.TabItem, "translateY(4px) scale(0.96)");
    }

    private void UpdateTabVisuals()
    {
        foreach (var tab in _tabs)
        {
            tab.TabLabel.Text = tab.IsDirty ? $"{tab.Name} •" : tab.Name;
            tab.TabItem.Background = ReferenceEquals(tab, _activeTab) ? AppTheme.HeaderBrush : Brushes.Transparent;
            ToolTip.SetTip(tab.TabItem, tab.FilePath ?? tab.Name);
        }
    }

    /// <summary>重畫分頁縮圖（有變更才畫；ChangeCount 沒動就直接跳過）。</summary>
    private void RefreshTabThumbnail(DocumentTab tab)
    {
        var changes = Volatile.Read(ref tab.ChangeCount);
        if (changes == tab.ThumbChangeCount) return;
        tab.ThumbChangeCount = changes;
        var doc = tab.Session.Document;
        tab.Thumb.Source = Rendering.LayerThumbnail.Render(doc, doc.Root, 46, 34);
    }

    private void UpdateViewportStatus()
    {
        _suppressZoomEvents = true;
        ZoomBar.Value = Canvas.ZoomPercent;
        _suppressZoomEvents = false;
    }

    private bool _suppressZoomEvents;

    /// <summary>狀態列的效能指示（取代先前蓋在畫布上的 debug overlay）。</summary>
    private long _perfLastFrame;

    private void StartPerfLabelTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            // 畫布改成有變才重繪：閒置時沒有幀，fps 只在真的連續重繪（合成中／動畫／拖曳）時才有意義
            var stats = Canvas.Stats;
            var frames = stats.FrameIndex - _perfLastFrame;
            _perfLastFrame = stats.FrameIndex;
            PerfLabel.Text = stats.PendingTiles > 0
                ? $"{stats.Fps:F0} fps・合成中 {stats.PendingTiles}"
                : frames >= 10 ? $"{stats.Fps:F0} fps" : "閒置";

            // 順便讓作用中分頁的縮圖跟上編輯（ChangeCount 沒變就是免費檢查）
            if (_activeTab is { } tab) RefreshTabThumbnail(tab);
            EnsurePanelsVisible(); // 開關亮著的面板一定看得到（自我修復）
        };
        timer.Start();
    }
}
