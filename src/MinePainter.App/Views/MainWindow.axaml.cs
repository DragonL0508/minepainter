using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.Core.Adjustments;
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

public partial class MainWindow : Window
{
    // 浮動面板（真正的 OS 子視窗，可拖出主視窗）
    private readonly LayersPanel _layersContent = new();
    private readonly ToolsPanelContent _toolsContent = new();
    private readonly HistoryPanelContent _historyContent = new();
    private readonly PalettePanelContent _paletteContent = new();
    private PanelWindow _toolsPanel = null!;
    private PanelWindow _historyPanel = null!;
    private PanelWindow _layersPanel = null!;
    private PanelWindow _palettePanel = null!;
    private bool _panelsPlaced;

    private string _currentToolKey = "brush";
    private bool _forceClose;

    // ---- 文件分頁（paint.net 的多文件模式）----

    /// <summary>一個開啟中的文件：session + 檔案身分 + dirty 狀態 + 各自的視口與分頁 UI。</summary>
    private sealed class DocumentTab
    {
        public required EditorSession Session { get; init; }
        public string? FilePath;      // 目前的 .mpp 路徑（null = 尚未存過）
        public string? ImportedName;  // 匯入來源的檔名（.pdn／影像）；只用於標題與存檔預設名
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

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialFile)
    {
        InitializeComponent();

        // 影像檔拖進視窗：問要「開啟」還是「加入圖層」
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        InitVectorOptions();
        BuildFloatingPanels();
        WireToolOptionBars();
        BuildShortcutActions();
        BuildTabStrip();
        InitCanvasFade();
        BuildEffectMenus();
        SyncMenuGestures();
        Services.ShortcutMap.Changed += SyncMenuGestures; // 重新綁定後選單顯示跟著換

        Canvas.StateChanged += RefreshUiState;
        Canvas.TextEditRequested += StartCanvasTextEdit;
        BuildFrameActions();
        Canvas.FrameTick += UpdateFrameActions;
        Canvas.ViewportChanged += () =>
        {
            UpdateViewportStatus();
            RepositionCanvasTextEdit();
        };
        Canvas.PointerDocMoved += p =>
            CursorPosLabel.Text = $"{(int)p.X}, {(int)p.Y} 像素";
        _layersContent.StateChanged += () =>
        {
            // 換圖層/改結構前，先收掉畫布內編輯與浮動中的選取內容
            CommitCanvasTextEdit();
            Canvas.Session?.CommitFloating();
            RefreshUiState();
        };
        _historyContent.StateChanged += RefreshUiState;
        _toolsContent.ToolSelected += SelectTool;
        _paletteContent.ColorSelected += color =>
        {
            if (Canvas.Session is { } s)
            {
                s.Foreground = color;
                ApplyTextColor(color); // 正在編輯／選著文字 → 同步改該文字的顏色
                RefreshUiState();
            }
        };
        // 放開滑鼠才落地成一步 undo（拖色輪／滑桿途中的連續變化只做即時預覽）
        _paletteContent.ColorCommitted += _ => CommitTextEdit();

        // 浮動面板黏著主視窗：移動、改變大小、最大化都跟著走
        PositionChanged += (_, _) => RepositionPanels();
        SizeChanged += (_, _) => RepositionPanels();
        Activated += (_, _) => EnsurePanelsVisible(); // 從最小化／別的程式切回來時對齊一次

        _initialFile = initialFile;
        Opened += (_, _) =>
        {
            PrepareBeforeShow(); // 正常流程 App 已先呼叫過（啟動畫面期間）；這裡是保險
            ShowPanels();
            StartPerfLabelTimer();
            Canvas.Focus();

            // 開發驗證用（GUI 驗證不得注入輸入，這是看到那些畫面的正規途徑）：
            // MINEPAINTER_DEBUG_TEXTFX=1 啟動即開進階文字設定；
            // =2 另外先放一段旋轉過、含兩層外框＋陰影的文字並選取它（看得到多層外框 UI 與選取框旁的按鈕）
            // MINEPAINTER_DEBUG_OFFSCREEN=1：整個 app（含浮窗、啟動畫面）擺到主螢幕右側之外 ——
            // 開發驗證用 PrintWindow 截圖時不會跳到使用者面前干擾他們；=main 只移主視窗（啟動畫面留在原位供截圖）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OFFSCREEN") is "1" or "main" &&
                Screens.Primary is { } primary)
            {
                Position = new PixelPoint(primary.Bounds.Right + 40, primary.Bounds.Y + 40);
            }

            // MINEPAINTER_DEBUG_EFFECT=<效果或調整名稱>：先鋪一張漸層＋幾何測試圖，畫幾筆筆刷與形狀，
            // 再直接開該效果的對話框（驗證預覽與對話框佈局）；=stroke 只鋪測試圖並放大到 400%
            var debugEffect = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_EFFECT");
            if (!string.IsNullOrEmpty(debugEffect)) SeedDebugEffect(debugEffect);

            var debugTextFx = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_TEXTFX");
            if (debugTextFx is "1" or "2") SeedDebugText();
            if (debugTextFx == "3")
            {
                // =3：切到文字工具、1.5 秒後把工具列的字型下拉打開（驗證下拉清單首次開啟的渲染）
                SelectTool("text");
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    // 視窗沒有焦點時 light-dismiss 會立刻把下拉關掉（截不到）；驗證模式先關掉它
                    foreach (var popup in FontFamilyCombo.GetTemplateChildren().OfType<Popup>())
                        popup.IsLightDismissEnabled = false;
                    FontFamilyCombo.IsDropDownOpen = true;
                }, TimeSpan.FromMilliseconds(1500));
            }
        };

        // Tab 在主視窗一律無效化（不做焦點跳轉），改作「按住＝對齊模式」（快捷鍵設定可改鍵）。
        // 焦點導航掛在 bubble 階段，要用 tunnel 才攔得在它前面。
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGlobalKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => SetAlignMode(false); // 按住時切走視窗，KeyUp 收不到
    }

    private readonly string? _initialFile;
    private bool _prepared;

    /// <summary>
    /// Show() 之前能先做掉的重活，趁啟動畫面還在時呼叫，主視窗才不會在啟動畫面退場後又空白半秒：
    /// 載入初始文件（建 session、接上各面板，實測 160ms+）、把主視窗與四個浮動面板的模板套用與排版先跑一遍
    /// （實測 260ms+）。畫布視口的 fit 本來就延到第一幀，面板位置要等視窗真的擺好才算，所以那些留在 Opened。
    /// </summary>
    public void PrepareBeforeShow()
    {
        if (_prepared) return;
        _prepared = true;

        Measure(new Size(Width, Height));
        Arrange(new Rect(0, 0, Width, Height));
        foreach (var (panel, _) in PanelPairs())
        {
            var h = double.IsNaN(panel.Height) ? double.PositiveInfinity : panel.Height;
            panel.Measure(new Size(panel.Width, h));
            panel.Arrange(new Rect(panel.DesiredSize));
        }

        if (_initialFile != null && File.Exists(_initialFile))
            OpenFile(_initialFile);
        else
            SetDocument(ImageCodec.CreateBlankDocument(1920, 1080, SKColors.White));
    }

    /// <summary>開發驗證用：鋪測試圖（漸層 + 筆刷 + 形狀），再開指定效果（見 Opened 裡的說明）。</summary>
    private void SeedDebugEffect(string name)
    {
        // 筆刷／形狀驗證用小畫布：400% 時整張看得到
        if (name == "stroke") SetDocument(ImageCodec.CreateBlankDocument(250, 160, SKColors.White));

        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is not RasterLayer layer) return;
        var doc = session.Document;

        lock (doc.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(doc.Bounds))
            {
                var tile = layer.Surface.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
                var c = surface.Canvas;
                var r = idx.ToPixelRect();
                c.Translate(-r.Left, -r.Top);
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(doc.Width, doc.Height),
                    [new SKColor(0x2A, 0x9D, 0xF4), new SKColor(0xF4, 0xC2, 0x2A), new SKColor(0xE0, 0x40, 0x60)],
                    null, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader };
                c.DrawRect(SKRect.Create(0, 0, doc.Width, doc.Height), paint);
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                c.DrawCircle(doc.Width * 0.3f, doc.Height * 0.5f, doc.Height * 0.18f, white);
                using var dark = new SKPaint { Color = new SKColor(0x20, 0x20, 0x30), IsAntialias = true };
                c.DrawRect(SKRect.Create(doc.Width * 0.55f, doc.Height * 0.3f, doc.Width * 0.25f, doc.Height * 0.4f), dark);
                c.Flush();
            }
        }
        layer.InvalidateAll();

        // 筆刷：一條斜線 + 一條曲線（走工具 API，不注入輸入）
        session.Brush.Settings.Radius = 6;
        session.Brush.Settings.Hardness = 1f;
        session.Foreground = SKColors.Black;
        var ev = (float x, float y) => new ToolPointerEvent(new SKPoint(x, y), 1f, ToolModifiers.None, 1);
        session.Brush.OnPointerDown(ev(doc.Width * 0.1f, doc.Height * 0.85f), session);
        for (var i = 1; i <= 60; i++)
        {
            var t = i / 60f;
            session.Brush.OnPointerMove(ev(doc.Width * (0.1f + 0.35f * t), doc.Height * (0.85f - 0.25f * t) + MathF.Sin(t * 12) * 6), session);
        }
        session.Brush.OnPointerUp(ev(doc.Width * 0.45f, doc.Height * 0.6f), session);

        session.Shape.Kind = Core.Vectors.ShapeKind.Ellipse;
        session.Shape.Filled = false;
        session.Shape.StrokeWidth = 3;
        session.Shape.OnPointerDown(ev(doc.Width * 0.6f, doc.Height * 0.72f), session);
        session.Shape.OnPointerMove(ev(doc.Width * 0.9f, doc.Height * 0.95f), session);
        session.Shape.OnPointerUp(ev(doc.Width * 0.9f, doc.Height * 0.95f), session);

        if (name == "stroke")
        {
            Canvas.SetZoomPercent(400);
            return;
        }

        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (name.StartsWith("layer:"))
            {
                var key = name[6..];
                var entry = AdjustmentRegistry.All.FirstOrDefault(a => a.DisplayName == key || a.TypeId == key);
                if (entry != null) _layersContent.AddAdjustment(entry.CreateDefault());
                return;
            }
            if (name == "stack")
            {
                // 兩筆效果進堆疊（一筆限左半選取），開圖層屬性看堆疊 UI
                using var half = new SKPath();
                half.AddRect(SKRect.Create(0, 0, doc.Width / 2f, doc.Height));
                var mask = Core.Selections.SelectionMask.FromPath(half, doc.Bounds).Mask;
                LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new GaussianBlurEffect { Radius = 12 }, mask));
                LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new AdjustmentEffect(new HueSaturationAdjustment(Hue: 120)), null, session.Foreground));
                _layersContent.Refresh();
                _layersContent.OpenProperties(layer);
                return;
            }
            if (name == "dialog:resize") { OnResizeImageClicked(null, new RoutedEventArgs()); return; }
            if (name == "dialog:canvas") { OnCanvasSizeClicked(null, new RoutedEventArgs()); return; }

            var adj = AdjustmentRegistry.All.FirstOrDefault(a => a.DisplayName == name || a.TypeId == name);
            if (adj != null)
            {
                _ = ApplyAdjustmentAsync(adj);
                return;
            }
            var fx = EffectRegistry.All.FirstOrDefault(e => e.Name == name);
            if (fx != null) _ = ApplyEffectAsync(fx.Create(), fx.Name, showDialog: true);
        }, TimeSpan.FromMilliseconds(800));
    }

    /// <summary>開發驗證用：放一段有多層外框／陰影、旋轉過的文字並選取（見 Opened 裡的說明）。</summary>
    private void SeedDebugText()
    {
        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is not RasterLayer layer) return;
        _ = layer;
        var text = new TextElement
        {
            Text = "多層外框 Sample",
            FontFamily = Services.FontCatalog.Families.FirstOrDefault(f => f.Contains("JhengHei") || f.Contains("正黑"))
                         ?? Services.FontCatalog.Families.FirstOrDefault() ?? "Microsoft JhengHei",
            FontSize = 96,
            Bold = true,
            Color = new SKColor(0xFF, 0xD8, 0x38),
            Position = new SKPoint(400, 380),
            Rotation = -12f,
        };
        // 文字一定自己一層；外框／陰影走圖層效果堆疊
        layer = VectorCommands.CreateTextLayerSilently(session.Document);
        lock (session.Document.SyncRoot)
        {
            layer.AddElement(text);
            layer.Name = VectorCommands.TextLayerNameFor(text.Text);
            layer.SetEffects([
                LayerEffect.Create(new ObjectOutlineEffect { Width = 4, Color = new SKColor(0xB3, 0x1E, 0x24) }),
                LayerEffect.Create(new ObjectOutlineEffect { Width = 5, Color = SKColors.White }),
                LayerEffect.Create(new ObjectShadowEffect { OffsetX = 4, OffsetY = 7, Blur = 8, Opacity = 55 }),
            ]);
        }
        SelectTool("text");
        session.SelectedElement = (layer.Id, text.Id);
        RefreshUiState();
    }

    // ---- 浮動面板 ----

    private void BuildFloatingPanels()
    {
        CreatePanelWindows();

        // 開關按鈕只綁一次；面板實例可能被重建，所以透過 getter 取當下的那一個
        Wire(() => _toolsPanel, ToolsToggle);
        Wire(() => _historyPanel, HistoryToggle);
        Wire(() => _layersPanel, LayersToggle);
        Wire(() => _palettePanel, PaletteToggle);

        void Wire(Func<PanelWindow> panel, ToggleButton toggle)
        {
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (toggle.IsChecked == true)
                {
                    panel().EnsureShown(this);
                    Activate(); // 焦點留在主視窗
                }
                else
                {
                    panel().HideAnimated();
                }
            };
        }
    }

    private void CreatePanelWindows()
    {
        _toolsPanel = Create(new PanelWindow("工具", _toolsContent, 86), ToolsToggle);
        // 圖層／歷史記錄的清單會長，給固定起手尺寸並允許拉大小（工具／調色盤維持隨內容）
        _historyPanel = Create(new PanelWindow("歷史記錄", _historyContent, 216, resizableHeight: 292), HistoryToggle);
        _layersPanel = Create(new PanelWindow("圖層", _layersContent, 330, resizableHeight: 436), LayersToggle);
        _palettePanel = Create(new PanelWindow("調色盤", _paletteContent, 330), PaletteToggle);

        PanelWindow Create(PanelWindow panel, ToggleButton toggle)
        {
            panel.CloseRequested += () => toggle.IsChecked = false;
            panel.PositionChanged += (_, _) => OnPanelMoved(panel);
            return panel;
        }
    }

    /// <summary>
    /// 關閉被取消後（未存檔提示按了取消）把浮窗接回來。
    /// Avalonia 在主視窗的 Closing 之前就把子視窗真的關掉了，關掉的 Window 不能再 Show，
    /// 只能以原位置重建 —— 面板內容是常駐控制項，重建的只有外框視窗。
    /// </summary>
    private void RecreateFloatingPanels()
    {
        if (PanelPairs().All(p => !p.Item1.IsClosed)) return;

        var state = PanelPairs().Select(p => (p.Item1.LastPosition, p.Item1.Anchor)).ToList();
        foreach (var (panel, _) in PanelPairs()) panel.AllowClose(); // 還沒關掉的先收乾淨
        CreatePanelWindows();

        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var (position, anchor) = state[i++];
            panel.Anchor = anchor;
            panel.Position = position;
            if (toggle.IsChecked == true) panel.Show(this);
        }
        Activate();
    }

    private void ShowPanels()
    {
        if (!_panelsPlaced)
        {
            _panelsPlaced = true;
            var frame = MainWorkArea();
            Place(_toolsPanel, new PanelAnchor(false, false, Px(18), Px(96)));
            Place(_palettePanel, new PanelAnchor(false, true, Px(18), Px(470)));
            Place(_layersPanel, new PanelAnchor(true, false, Px(348), Px(96)));
            Place(_historyPanel, new PanelAnchor(true, true, Px(286), Px(380)));

            void Place(PanelWindow panel, PanelAnchor anchor)
            {
                panel.Anchor = anchor;
                panel.Position = AnchoredPosition(anchor, frame);
                _defaultAnchors[PanelPairs().First(p => p.Item1 == panel).Item2] = anchor;
            }
        }

        foreach (var (panel, toggle) in PanelPairs())
        {
            if (toggle.IsChecked == true) panel.EnsureShown(this);
        }
        Activate();
    }

    private readonly Dictionary<ToggleButton, PanelAnchor> _defaultAnchors = new();

    /// <summary>
    /// 自我修復（每 500ms 與主視窗取得焦點時）：開關亮著的面板就必須看得到。
    /// 面板會不見的路徑不只一條（退場動畫中途又被 Show、被框架連帶關掉、位置跑到所有螢幕之外…），
    /// 與其逐一追，不如以開關為真相來源定期對齊 —— 使用者不該需要「點兩下」才把面板叫回來。
    /// </summary>
    private bool _closingPrompt;

    private void EnsurePanelsVisible()
    {
        if (!_panelsPlaced || !IsVisible || _closingPrompt || _forceClose ||
            WindowState == WindowState.Minimized)
        {
            return;
        }
        if (PanelPairs().Any(p => p.Item1.IsClosed))
        {
            RecreateFloatingPanels();
            return;
        }

        var area = MainWorkArea();
        if (IsBogusArea(area)) return;
        var screens = Screens.All;
        foreach (var (panel, toggle) in PanelPairs())
        {
            if (toggle.IsChecked != true) continue;

            // 整個面板落在所有螢幕之外（例如換了螢幕配置、主視窗曾被縮到很小）→ 回到預設錨點
            var w = Px(panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width);
            var h = Px(panel.Bounds.Height > 0 ? panel.Bounds.Height : panel.Height);
            var rect = new PixelRect(panel.Position, new PixelSize(Math.Max(w, 1), Math.Max(h, 1)));
            if (screens.Count > 0 && !screens.Any(s => s.Bounds.Intersects(rect)) &&
                _defaultAnchors.TryGetValue(toggle, out var anchor))
            {
                panel.Anchor = anchor;
                panel.Position = AnchoredPosition(anchor, area);
            }

            panel.EnsureShown(this);
        }
    }

    /// <summary>最小化時 Windows 會把視窗搬到 (-32000, -32000)，那個座標不能拿來當基準。</summary>
    private static bool IsBogusArea(PixelRect area) => area.X <= -30000 || area.Y <= -30000;

    // ---- 面板錨定（跟著主視窗移動／最大化）----

    /// <summary>主視窗工作區在螢幕上的矩形（像素）。面板的相對位置都以它為基準。</summary>
    private PixelRect MainWorkArea() =>
        new(this.PointToScreen(default), PixelSize.FromSize(ClientSize, RenderScaling));

    private int Px(double dip) => (int)Math.Round(dip * RenderScaling);

    private static PixelPoint AnchoredPosition(PanelAnchor a, PixelRect area) => new(
        a.Right ? area.Right - a.OffsetX : area.X + a.OffsetX,
        a.Bottom ? area.Bottom - a.OffsetY : area.Y + a.OffsetY);

    /// <summary>
    /// 主視窗移動或改變大小（含最大化）時，浮動面板照相對位置跟著走 ——
    /// paint.net 的浮動面板也是黏在主視窗四角，不然每次最大化都要重拉一次。
    /// 靠左/上的維持與該邊的距離，靠右/下的維持與右/下緣的距離。
    /// </summary>
    private void RepositionPanels()
    {
        if (!_panelsPlaced || WindowState == WindowState.Minimized) return;

        var area = MainWorkArea();
        if (IsBogusArea(area)) return; // 最小化途中的 PositionChanged 可能先於 WindowState 更新
        foreach (var (panel, _) in PanelPairs())
        {
            if (panel.Anchor is { } anchor) panel.Position = AnchoredPosition(anchor, area);
        }
    }

    /// <summary>
    /// 面板位置變了。若剛好等於錨點算出來的位置，就是上面自己搬的，不動錨點；
    /// 否則是使用者拖動的，重新記下它現在貼近哪一組邊。
    /// </summary>
    private void OnPanelMoved(PanelWindow panel)
    {
        if (!_panelsPlaced) return;
        var area = MainWorkArea();
        if (IsBogusArea(area) || panel.Position.X <= -30000 || panel.Position.Y <= -30000) return;
        if (panel.Anchor is { } a && AnchoredPosition(a, area) == panel.Position) return;

        var pos = panel.Position;
        var w = Px(panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width);
        var h = Px(panel.Bounds.Height);
        var right = pos.X + w / 2 > area.X + area.Width / 2;
        var bottom = pos.Y + h / 2 > area.Y + area.Height / 2;
        panel.Anchor = new PanelAnchor(right, bottom,
            right ? area.Right - pos.X : pos.X - area.X,
            bottom ? area.Bottom - pos.Y : pos.Y - area.Y);
    }

    private IEnumerable<(PanelWindow, ToggleButton)> PanelPairs()
    {
        yield return (_toolsPanel, ToolsToggle);
        yield return (_historyPanel, HistoryToggle);
        yield return (_layersPanel, LayersToggle);
        yield return (_palettePanel, PaletteToggle);
    }

    // ---- 工具切換 ----

    private void SelectTool(string key)
    {
        var session = Canvas.Session;
        if (session == null) return;

        // 切走移動工具前，先把浮動中的選取內容/變形框烙回圖層
        if (_currentToolKey == "move" && key != "move")
        {
            session.CommitTransform();
            session.CommitFloating();
        }

        var changed = _currentToolKey != key;
        _currentToolKey = key;
        session.ActiveTool = key switch
        {
            "eraser" => session.Eraser,
            "bgeraser" => session.BackgroundEraser,
            "eyedropper" => session.Eyedropper,
            "move" => session.Move,
            "rectselect" => session.RectSelect,
            "lasso" => session.Lasso,
            "wand" => session.Wand,
            "fill" => session.Fill,
            "text" => session.Text,
            "shape" => session.Shape,
            _ => session.Brush,
        };

        _toolsContent.SetActive(key);
        ActiveToolLabel.Text = session.ActiveTool.Name;
        UpdateToolOptions(key);
        if (changed) Toasts.Show($"工具：{session.ActiveTool.Name}");
        if (key != "text") Canvas.Focus();
    }

    /// <summary>依工具顯示對應的選項群組（單行內切換，不改變工具列高度）。</summary>
    private void UpdateToolOptions(string key)
    {
        SizeGroup.IsVisible = key is "brush" or "eraser" or "bgeraser" or "shape";
        HardnessGroup.IsVisible = key is "brush" or "eraser" or "bgeraser";
        SmoothingGroup.IsVisible = key is "brush" or "eraser";
        OpacityGroup.IsVisible = key is "brush" or "eraser" or "fill";
        ToleranceGroup.IsVisible = key is "fill" or "wand" or "bgeraser";
        BgEraserGroup.IsVisible = key == "bgeraser";
        TextGroup.IsVisible = key == "text";
        ShapeGroup.IsVisible = key == "shape";
    }

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

        _activeTab = tab;
        var session = tab.Session;
        Canvas.SetSession(session, tab.Viewport);
        _layersContent.SetSession(session);
        _historyContent.SetSession(session);
        _paletteContent.SetColor(session.Foreground);
        ApplyBrushOptions();
        ApplyShapeOptions();
        ApplyTextOptions();
        SelectTool(_currentToolKey);
        UpdateTitle();
        UpdateViewportStatus();
        DocSizeLabel.Text = $"{session.Document.Width} × {session.Document.Height}";
        RefreshUiState();
        UpdateTabVisuals();
        RefreshTabThumbnail(tab);
        Canvas.Focus();
    }

    // ---- 分頁切換動畫（快速 fade out → 換內容 → fade in） ----
    // fade 由 CanvasView.ContentFade 在 draw op 裡自己套（外圍底色不動），
    // 不碰 Visual.Opacity —— Opacity=0 時 Avalonia 會剔除子樹，畫面會閃黑。

    private DocumentTab? _pendingSwitch;

    private void InitCanvasFade()
    {
        // ContentFade 路徑不需要 Transitions；保留方法讓建構流程不變
    }

    /// <summary>點分頁的切換：110ms 淡出 → 換 session → 180ms 淡入。</summary>
    private void ActivateTabAnimated(DocumentTab tab)
    {
        if (ReferenceEquals(tab, _activeTab) && _pendingSwitch == null) return;

        // 連點只記最後一個目標；淡出已經在跑就不重排
        var alreadyFading = _pendingSwitch != null;
        _pendingSwitch = tab;
        if (alreadyFading) return;

        Canvas.BeginContentFade(0, 110);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            var target = _pendingSwitch;
            if (target == null) return; // 期間被同步切換蓋掉了
            _pendingSwitch = null;

            if (!ReferenceEquals(target, _activeTab)) ActivateTab(target);

            // ActivateTab 會把 fade snap 回 1；這裡壓回 0 再淡入到 1
            Canvas.SnapContentFade(0);
            Canvas.BeginContentFade(1, 180);
        }, TimeSpan.FromMilliseconds(115));
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
        TabStrip.Children.Remove(tab.TabItem);
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
            DocSizeLabel.Text = $"{tab.Session.Document.Width} × {tab.Session.Document.Height}";
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

        // 新分頁淡入
        tab.TabItem.Opacity = 0;
        tab.TabItem.Transitions =
        [
            new Avalonia.Animation.DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(160),
                Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            },
        ];
        TabStrip.Children.Insert(TabStrip.Children.Count - 1, tab.TabItem); // 「＋」永遠在最後
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => tab.TabItem.Opacity = 1, Avalonia.Threading.DispatcherPriority.Loaded);
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
    private void StartPerfLabelTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            var stats = Canvas.Stats;
            PerfLabel.Text = stats.PendingTiles > 0
                ? $"{stats.Fps:F0} fps・合成中 {stats.PendingTiles}"
                : $"{stats.Fps:F0} fps";

            // 順便讓作用中分頁的縮圖跟上編輯（ChangeCount 沒變就是免費檢查）
            if (_activeTab is { } tab) RefreshTabThumbnail(tab);
            EnsurePanelsVisible(); // 開關亮著的面板一定看得到（自我修復）
        };
        timer.Start();
    }

    // ---- 檔案 ----

    private async void OnNewClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewDocumentWindow();
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        SetDocument(ImageCodec.CreateBlankDocument(dialog.DocWidth, dialog.DocHeight, dialog.DocBackground));
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "開啟",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("支援的檔案") { Patterns = ["*.mpp", "*.pdn", "*.png", "*.jpg", "*.jpeg", "*.bmp"] },
                new FilePickerFileType("MinePainter 專案") { Patterns = ["*.mpp"] },
                new FilePickerFileType("paint.net 專案") { Patterns = ["*.pdn"] },
                new FilePickerFileType("影像檔") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] },
            ],
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenFile(path);
    }

    // ---- 拖放檔案 ----

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private static readonly string[] OpenableExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".mpp", ".pdn"];

    private static bool HasExtension(string path, string[] list) =>
        list.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static List<string> DroppedPaths(DragEventArgs e)
    {
        var result = new List<string>();
        if (e.Data.GetFiles() is not { } items) return result;
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (path != null && File.Exists(path) && HasExtension(path, OpenableExtensions)) result.Add(path);
        }
        return result;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = DroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;

        var session = Canvas.Session;
        var canAddLayers = session != null && paths.Any(p => HasExtension(p, ImageExtensions));
        var dialog = new DropFilesDialog(paths.Select(Path.GetFileName).ToList()!, canAddLayers);
        await dialog.ShowDialog(this);

        switch (dialog.Result)
        {
            case DropFilesDialog.Choice.Open:
                foreach (var path in paths) OpenFile(path);
                break;

            case DropFilesDialog.Choice.AddLayers:
                session = CommitPending();
                if (session == null) return;
                var skipped = 0;
                foreach (var path in paths)
                {
                    if (!HasExtension(path, ImageExtensions))
                    {
                        skipped++; // .mpp/.pdn 是整份文件，不能當一層
                        continue;
                    }
                    ImportLayerFromFile(session, path);
                }
                if (skipped > 0) Toasts.Show($"{skipped} 個檔案是文件格式（.mpp/.pdn），只能用「開啟」");
                _layersContent.Refresh();
                RefreshUiState();
                break;
        }
    }

    /// <summary>把影像檔匯入成目前文件的一層（插在作用中圖層上方）。失敗以 toast 回報，回傳是否成功。</summary>
    private bool ImportLayerFromFile(EditorSession session, string path)
    {
        try
        {
            using var bitmap = ImageCodec.LoadBitmap(path);
            ImageCommands.ImportImageLayer(session, bitmap, Path.GetFileNameWithoutExtension(path));
            return true;
        }
        catch (Exception ex)
        {
            Toasts.Show($"匯入失敗：{Path.GetFileName(path)}（{ex.Message}）");
            return false;
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".mpp", StringComparison.OrdinalIgnoreCase))
            {
                SetDocument(MppFormat.Load(path), path);
            }
            else if (PdnFormat.IsPdnFile(path))
            {
                OpenPaintDotNetFile(path);
            }
            else
            {
                SetDocument(ImageCodec.LoadAsDocument(path), importedName: Path.GetFileName(path));
            }
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 開啟失敗：{ex.Message}";
        }
    }

    /// <summary>.pdn 只能讀不能寫，所以當成匯入：不記成目前檔案，之後存檔會走「另存為 .mpp」。</summary>
    private void OpenPaintDotNetFile(string path)
    {
        var doc = PdnFormat.Load(path, out var warnings);
        SetDocument(doc, importedName: Path.GetFileName(path));

        Toasts.Show("已匯入 paint.net 專案（儲存時會存成 .mpp）");
        foreach (var warning in warnings.Take(2)) Toasts.Show(warning);
    }

    /// <summary>存檔／匯出對話框的預設檔名：沿用目前檔案，或匯入來源（.pdn／影像）的名字。</summary>
    private string SuggestedName(string fallback) =>
        Path.GetFileNameWithoutExtension(_activeTab?.FilePath ?? _activeTab?.ImportedName) is { Length: > 0 } name
            ? name
            : fallback;

    private async void OnSaveClicked(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void OnSaveAsClicked(object? sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    /// <summary>存目前作用中的分頁。回傳是否真的存了檔（false = 使用者取消或失敗）。</summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        // 以分頁為單位：背景存檔期間就算切到別的分頁，寫檔與旗標更新仍作用在原本那份
        var tab = _activeTab;
        if (tab == null) return false;
        var session = tab.Session;

        CommitCanvasTextEdit();   // 存檔前先把進行中的編輯落地
        session.CommitFloating();

        var path = tab.FilePath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "儲存專案",
                DefaultExtension = "mpp",
                SuggestedFileName = SuggestedName("未命名"),
                FileTypeChoices = [new FilePickerFileType("MinePainter 專案") { Patterns = ["*.mpp"] }],
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
        }

        try
        {
            // 寫檔丟背景執行緒（快照階段在 Save 內部鎖住文件，之後只讀不可變資料）。
            // 存檔期間使用者可能又畫了東西：完成時不能直接清 dirty，
            // 要看「按下儲存之後」有沒有新變更（快照一定在那之後才拍，此判斷偏保守但安全）。
            var doc = session.Document;
            var changesAtStart = Volatile.Read(ref tab.ChangeCount);
            await ProgressDialog.RunAsync(this, "儲存專案", p => MppFormat.Save(doc, path, p));

            tab.FilePath = path;
            tab.IsDirty = Volatile.Read(ref tab.ChangeCount) != changesAtStart;
            UpdateTitle();
            UpdateTabVisuals();
            return true;
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 儲存失敗：{ex.Message}";
            Toasts.Show($"儲存失敗：{ex.Message}");
            LogError("儲存", ex);
            return false;
        }
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session == null) return;

        CommitCanvasTextEdit();       // 匯出的是合成結果，先把進行中的編輯落地
        session.CommitPendingEdits(); // 浮動內容、變形框等所有進行中編輯一次涵蓋

        var dialog = new ExportWindow(session.Document.Width, session.Document.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        // 檔案類型跟著對話框選的格式走，避免「選了 JPEG 卻存成 .png」
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出影像",
            DefaultExtension = dialog.IsJpeg ? "jpg" : "png",
            SuggestedFileName = SuggestedName("匯出"),
            FileTypeChoices = dialog.IsJpeg
                ? [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }]
                : [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
        });
        if (file == null) return; // 使用者取消
        var path = file.TryGetLocalPath();
        if (path == null)
        {
            Toasts.Show("匯出失敗：無法取得檔案路徑");
            return;
        }

        try
        {
            var doc = session.Document;
            await ProgressDialog.RunAsync(this, "匯出影像",
                p => MppFormat.Export(doc, path, dialog.Quality, dialog.OutWidth, dialog.OutHeight, p));
            Toasts.Show($"已匯出 {Path.GetFileName(path)}（{dialog.OutWidth} × {dialog.OutHeight}）");
        }
        catch (Exception ex)
        {
            Title = $"MinePainter — 匯出失敗：{ex.Message}";
            Toasts.Show($"匯出失敗：{ex.Message}");
            LogError("匯出", ex);
        }
    }

    /// <summary>把例外完整寫進 %APPDATA%\MinePainter\error.log（回報問題用）。</summary>
    private static void LogError(string operation, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MinePainter");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {operation} 失敗{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void OnCloseTabClicked(object? sender, RoutedEventArgs e)
    {
        if (_activeTab is { } tab) _ = CloseTabAsync(tab);
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    // 未存檔提示
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose || _tabs.All(t => !t.IsDirty))
        {
            // 先掛上關閉旗標：子視窗的退場動畫會 Cancel 掉一次 Closing，
            // 那會連帶中止整個關閉流程（症狀＝要按兩次才關得掉）。
            Controls.WindowAnimator.IsShuttingDown = true;
            foreach (var (panel, _) in PanelPairs()) panel.AllowClose();
            foreach (var owned in OwnedWindows.ToList()) owned.Close(); // 圖層屬性等臨時視窗
            return;
        }

        // 逐一問每個未儲存的分頁（先切過去讓使用者看見在問哪份）；任何一步取消就中止關閉。
        // 期間框架已經把浮窗真的關掉了 —— 自我修復（EnsurePanelsVisible）要先暫停，
        // 不然它會在對話框還開著時就把面板重建回來、搶走焦點。
        e.Cancel = true;
        _closingPrompt = true;
        try
        {
            foreach (var tab in _tabs.Where(t => t.IsDirty).ToList())
            {
                ActivateTab(tab);
                var choice = await ShowUnsavedDialog(tab.Name);
                if (choice == UnsavedChoice.Cancel ||
                    (choice == UnsavedChoice.Save && !await SaveAsync(saveAs: false)))
                {
                    _closingPrompt = false;
                    RecreateFloatingPanels(); // 取消關閉：把框架已經關掉的浮窗接回來
                    return;
                }
                // Discard：略過這份，繼續問下一份
            }
        }
        finally
        {
            _closingPrompt = false;
        }
        _forceClose = true;
        Close();
    }

    private enum UnsavedChoice
    {
        Save,
        Discard,
        Cancel,
    }

    private async Task<UnsavedChoice> ShowUnsavedDialog(string? docName = null)
    {
        var result = UnsavedChoice.Cancel;
        var dialog = new Window
        {
            Title = "未儲存的變更",
            Width = 380,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        Button Make(string text, UnsavedChoice choice, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6) };
            if (primary) b.Classes.Add("accent");
            b.Click += (_, _) => { result = choice; dialog.Close(); };
            return b;
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = docName != null ? $"「{docName}」有未儲存的變更，要儲存嗎？" : "文件有未儲存的變更，要儲存嗎？",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { Make("儲存", UnsavedChoice.Save, primary: true), Make("不儲存", UnsavedChoice.Discard), Make("取消", UnsavedChoice.Cancel) },
                },
            },
        };

        await dialog.ShowDialog(this);
        return result;
    }

    // ---- 編輯 ----

    private void OnUndoClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.Session?.Undo();
        RefreshUiState();
    }

    private void OnRedoClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.Session?.Redo();
        RefreshUiState();
    }

    private void OnDeselectClicked(object? sender, RoutedEventArgs e)
    {
        if (Canvas.Session is { Selection: not null } s)
        {
            s.CommitFloating();
            SelectionCommands.SetSelection(s, null, "取消選取");
        }
        RefreshUiState();
    }

    /// <summary>
    /// 所有選單指令的共同前置：把進行中的編輯落地。
    /// Pinta 每一個 handler 開頭都做這件事，漏掉是這類功能最常見的 bug 來源。
    /// （undo/redo/歷史跳轉走 EditorSession.Undo/Redo/JumpTo，它們內部會做同一件事。）
    /// </summary>
    private EditorSession? CommitPending()
    {
        var session = Canvas.Session;
        if (session == null) return null;
        session.CommitPendingEdits();
        return session;
    }

    private void RunCommand(Action<EditorSession> command)
    {
        var session = CommitPending();
        if (session == null) return;
        command(session);
        _layersContent.Refresh();
        RefreshUiState();
    }

    // ---- 編輯：剪貼簿 ----

    private void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        using var image = session.CopyToImage();
        if (image == null)
        {
            Toasts.Show("沒有可複製的內容");
            return;
        }
        Toasts.Show(Platform.ClipboardImage.TrySetImage(image)
            ? $"已複製 {image.Width} × {image.Height}"
            : "複製失敗：無法存取剪貼簿");
    }

    private void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        using var image = session.CopyToImage();
        if (image == null)
        {
            Toasts.Show("沒有可剪下的內容");
            return;
        }
        if (!Platform.ClipboardImage.TrySetImage(image))
        {
            Toasts.Show("剪下失敗：無法存取剪貼簿");
            return;
        }

        var hadSelection = session.Selection is { IsEmpty: false };
        RunCommand(EditCommands.EraseSelection);
        Toasts.Show(hadSelection ? "已剪下選取範圍" : "已剪下整個圖層");
    }

    private async void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        var image = Platform.ClipboardImage.TryGetImage();
        if (image == null)
        {
            // 剪貼簿裡是檔案（檔案總管 Ctrl+C）：一個影像檔＝貼成浮動內容；多個＝各自成一層
            var files = Platform.ClipboardImage.TryGetFilePaths()
                .Where(p => File.Exists(p) && HasExtension(p, ImageExtensions)).ToList();
            if (files.Count == 0)
            {
                Toasts.Show("剪貼簿裡沒有影像或影像檔");
                return;
            }
            if (files.Count > 1)
            {
                var imported = files.Count(f => ImportLayerFromFile(session, f));
                if (imported > 0) Toasts.Show($"已把 {imported} 個影像檔各自貼成一層");
                _layersContent.Refresh();
                RefreshUiState();
                return;
            }
            try
            {
                using var bitmap = ImageCodec.LoadBitmap(files[0]);
                image = SKImage.FromBitmap(bitmap);
            }
            catch (Exception ex)
            {
                Toasts.Show($"無法讀取 {Path.GetFileName(files[0])}（{ex.Message}）");
                return;
            }
            if (image == null) return;
        }

        // 超出畫布：問要延展還是維持（paint.net 的行為）
        var doc = session.Document;
        if (image.Width > doc.Width || image.Height > doc.Height)
        {
            var dialog = new PasteSizeDialog(
                new SKSizeI(image.Width, image.Height), new SKSizeI(doc.Width, doc.Height));
            await dialog.ShowDialog(this);

            switch (dialog.Result)
            {
                case PasteSizeDialog.Choice.Cancel:
                    image.Dispose();
                    return;
                case PasteSizeDialog.Choice.ExpandCanvas:
                    DocumentCommands.ResizeCanvas(session,
                        Math.Max(doc.Width, image.Width), Math.Max(doc.Height, image.Height), "延展畫布（貼上）");
                    Canvas.ZoomToFit();
                    break;
            }
        }

        if (session.PasteImage(image, PastePosition(session, image.Width, image.Height)))
        {
            SelectTool("move"); // 貼上後直接可拖曳（paint.net 行為）
            Toasts.Show("已貼上（可拖曳移動，Enter 套用、Esc 取消）");
        }
        _layersContent.Refresh();
        RefreshUiState();
    }

    /// <summary>貼上位置：目前可視範圍的左上角，並夾到「整張影像盡量放得進畫布」的範圍。</summary>
    private SKPointI PastePosition(EditorSession session, int width, int height)
    {
        var doc = session.Document;
        var topLeft = Canvas.ViewToDoc(new Point(0, 0));
        var x = Math.Clamp((int)Math.Round(topLeft.X), 0, Math.Max(0, doc.Width - width));
        var y = Math.Clamp((int)Math.Round(topLeft.Y), 0, Math.Max(0, doc.Height - height));
        return new SKPointI(x, y);
    }

    // ---- 編輯：選取 ----

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.SelectAll);

    private void OnInvertSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.InvertSelection);

    private void OnEraseSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.EraseSelection);

    private void OnFillSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(EditCommands.FillSelection);

    // ---- 影像 ----

    private void OnCropToSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            DocumentCommands.CropToSelection(s);
            AfterDocumentResized(s);
        });

    private void OnFlipHorizontalClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.FlipHorizontal, "水平翻轉");

    private void OnFlipVerticalClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.FlipVertical, "垂直翻轉");

    private void OnRotateCwClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate90CW, "順時針旋轉 90°");

    private void OnRotateCcwClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate90CCW, "逆時針旋轉 90°");

    private void OnRotate180Clicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate180, "旋轉 180°");

    private void RunGeometry(GeometryOp op, string label) =>
        RunCommand(s =>
        {
            DocumentCommands.ApplyGeometry(s, op, label);
            AfterDocumentResized(s);
            Toasts.Show(label);
        });

    private void OnFlattenClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (LayerCommands.Flatten(s.Document, s.History))
                Toasts.Show("已平面化");
        });

    // ---- 影像大小／畫布大小／圖層幾何（paint.net 的 Image / Layers 選單補齊） ----

    private async void OnResizeImageClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        var dialog = new ResizeImageDialog(doc.Width, doc.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        var w = dialog.NewWidth;
        var h = dialog.NewHeight;
        try
        {
            await ProgressDialog.RunAsync(this, "調整影像大小", _ => ImageCommands.ResizeImage(session, w, h));
        }
        catch (Exception ex)
        {
            Toasts.Show($"調整影像大小失敗：{ex.Message}");
            return;
        }
        AfterDocumentResized(session);
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show($"影像大小：{w} × {h}");
    }

    private async void OnCanvasSizeClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        var dialog = new CanvasSizeDialog(doc.Width, doc.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        ImageCommands.ResizeCanvas(session, dialog.NewWidth, dialog.NewHeight, dialog.AnchorX, dialog.AnchorY);
        AfterDocumentResized(session);
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show($"畫布大小：{dialog.NewWidth} × {dialog.NewHeight}");
    }

    private void OnFlipLayerHorizontalClicked(object? sender, RoutedEventArgs e) =>
        FlipActiveLayer(GeometryOp.FlipHorizontal, "水平翻轉圖層");

    private void OnFlipLayerVerticalClicked(object? sender, RoutedEventArgs e) =>
        FlipActiveLayer(GeometryOp.FlipVertical, "垂直翻轉圖層");

    private void FlipActiveLayer(GeometryOp op, string label) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is not RasterLayer layer)
            {
                Toasts.Show("請先選擇一個點陣圖層");
                return;
            }
            ImageCommands.FlipLayer(s, layer, op, label);
            Toasts.Show(label);
        });

    private async void OnImportLayerClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "從檔案匯入圖層",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("影像檔") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }],
        });
        var imported = 0;
        var oversized = false;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path == null) continue;
            try
            {
                using var bitmap = ImageCodec.LoadBitmap(path);
                ImageCommands.ImportImageLayer(session, bitmap, Path.GetFileNameWithoutExtension(path));
                oversized |= bitmap.Width > session.Document.Width || bitmap.Height > session.Document.Height;
                imported++;
            }
            catch (Exception ex)
            {
                Toasts.Show($"匯入失敗：{Path.GetFileName(path)}（{ex.Message}）");
            }
        }
        if (imported == 0) return;
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show(oversized
            ? $"已匯入 {imported} 個圖層（影像比畫布大，超出部分看不到，可用「調整畫布大小」展開）"
            : $"已匯入 {imported} 個圖層");
    }

    private void OnLayerPropertiesClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is { } layer) _layersContent.OpenProperties(layer);
    }

    // ---- 調整／效果（paint.net 的 Adjustments / Effects 選單） ----

    /// <summary>「調整」選單順序照 paint.net。</summary>
    private static readonly string[] AdjustmentMenuOrder =
        ["blackWhite", "brightnessContrast", "curves", "hueSaturation", "invert", "levels", "posterize", "sepia"];

    private IEffect? _lastEffect;
    private MenuItem? _repeatEffectItem;

    private void BuildEffectMenus()
    {
        var auto = new MenuItem { Header = "自動色階", Tag = "adjust.autoLevel" };
        auto.Click += (_, _) => _ = ApplyAutoLevelAsync();
        AdjustmentsMenu.Items.Add(auto);

        foreach (var typeId in AdjustmentMenuOrder)
        {
            var entry = AdjustmentRegistry.Find(typeId);
            if (entry == null) continue;
            var item = new MenuItem
            {
                Header = entry.HasDialog ? entry.DisplayName + "…" : entry.DisplayName,
                Tag = "adjust." + entry.TypeId,
            };
            item.Click += (_, _) => _ = ApplyAdjustmentAsync(entry);
            AdjustmentsMenu.Items.Add(item);
        }

        _repeatEffectItem = new MenuItem { Header = "重複上次效果", Tag = "effect.repeat", IsEnabled = false };
        _repeatEffectItem.Click += (_, _) => OnRepeatEffect();
        EffectsMenu.Items.Add(_repeatEffectItem);

        // 非破壞性：效果／調整記錄在圖層的效果堆疊（圖層屬性可回頭改、排序、存預設集）
        var nonDestructive = new MenuItem
        {
            Header = "記錄在圖層（非破壞性）",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Services.AppSettings.Instance.NonDestructiveEffects,
        };
        nonDestructive.Click += (_, _) =>
        {
            Services.AppSettings.Instance.NonDestructiveEffects = nonDestructive.IsChecked;
            Services.AppSettings.Instance.Save();
            Toasts.Show(nonDestructive.IsChecked
                ? "效果將記錄在圖層效果堆疊（可在圖層屬性重新調整）"
                : "效果將直接寫入像素");
        };
        EffectsMenu.Items.Add(nonDestructive);

        var fxWhileDrag = new MenuItem
        {
            Header = "拖曳時即時顯示效果",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Services.AppSettings.Instance.RenderEffectsWhileDragging,
        };
        Core.Tools.EditorSession.RenderEffectsWhileDragging = fxWhileDrag.IsChecked;
        fxWhileDrag.Click += (_, _) =>
        {
            Services.AppSettings.Instance.RenderEffectsWhileDragging = fxWhileDrag.IsChecked;
            Services.AppSettings.Instance.Save();
            Core.Tools.EditorSession.RenderEffectsWhileDragging = fxWhileDrag.IsChecked;
            Toasts.Show(fxWhileDrag.IsChecked
                ? "移動物件／圖層時會連同外框、陰影等效果一起顯示"
                : "移動時只顯示基底像素，放開後才套用效果（較省效能）");
        };
        EffectsMenu.Items.Add(fxWhileDrag);
        EffectsMenu.Items.Add(new Separator());

        foreach (var category in EffectRegistry.Categories)
        {
            var sub = new MenuItem { Header = category };
            foreach (var entry in EffectRegistry.InCategory(category))
            {
                var e = entry;
                var item = new MenuItem { Header = e.Name + "…" };
                item.Click += (_, _) => _ = ApplyEffectAsync(e.Create(), e.Name, showDialog: true);
                sub.Items.Add(item);
            }
            EffectsMenu.Items.Add(sub);
        }
    }

    private Task ApplyAdjustmentAsync(AdjustmentRegistry.Entry entry) =>
        ApplyEffectAsync(new AdjustmentEffect(entry.CreateDefault()), entry.DisplayName, entry.HasDialog);

    private async Task ApplyAutoLevelAsync()
    {
        var session = CommitPending();
        if (session == null) return;
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個圖層");
            return;
        }
        using var fx = new EffectSession(session, layer);
        if (fx.IsEmpty) return;
        var levels = LevelsAdjustment.FromHistogram(fx.Histogram());
        if (Services.AppSettings.Instance.NonDestructiveEffects || layer.IsTextLayer)
        {
            var effect = new AdjustmentEffect(levels);
            LayerEffectCommands.Add(session.Document, session.History, layer,
                LayerEffect.Create(effect, session.Selection?.Clone().Mask, session.Foreground));
            _lastEffect = effect;
            Toasts.Show("自動色階（已記錄在圖層）");
            AfterEffect();
            return;
        }
        await ApplyImmediateAsync(fx, new AdjustmentEffect(levels), "自動色階");
        AfterEffect();
    }

    /// <summary>
    /// 套用效果到作用中圖層（受選取範圍限制）。有對話框時即時預覽、確定才進 history；
    /// 沒有對話框（負片／黑白／懷舊／重複上次）直接套用。
    /// </summary>
    private async Task ApplyEffectAsync(IEffect effect, string name, bool showDialog)
    {
        var session = CommitPending();
        if (session == null) return;
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層");
            return;
        }
        if (Services.AppSettings.Instance.NonDestructiveEffects || layer.IsTextLayer)
        {
            // 文字圖層永遠不含像素：效果一律記錄在堆疊（破壞性套用會把效果寫成像素）
            if (!Services.AppSettings.Instance.NonDestructiveEffects) Toasts.Show("文字圖層的效果一律記錄在圖層效果堆疊");
            await ApplyToLayerStackAsync(session, layer, effect, name, showDialog);
            return;
        }

        using var fx = new EffectSession(session, layer);
        if (fx.IsEmpty)
        {
            Toasts.Show("沒有可套用的範圍");
            return;
        }

        if (!showDialog)
        {
            await ApplyImmediateAsync(fx, effect, name);
            AfterEffect();
            return;
        }

        var dialog = new EffectDialog(fx, effect, name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            if (fx.Commit(name))
            {
                _lastEffect = dialog.Result;
                Toasts.Show(name);
            }
        }
        else
        {
            fx.Cancel();
        }
        AfterEffect();
    }

    /// <summary>非破壞性：效果進圖層效果堆疊（有選取就帶遮罩），對話框即時預覽由合成器背景重算。</summary>
    private async Task ApplyToLayerStackAsync(EditorSession session, RasterLayer layer, IEffect effect, string name, bool showDialog)
    {
        effect = EffectSerializer.WithPrimaryColor(effect, session.Foreground);
        var entry = LayerEffect.Create(effect, session.Selection?.Clone().Mask, session.Foreground);
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: true);
        if (!showDialog)
        {
            preview.Commit(effect);
            _lastEffect = effect;
            Toasts.Show($"{name}（已記錄在圖層）");
            AfterEffect();
            return;
        }

        var dialog = new EffectDialog(preview, effect, name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            preview.Commit(dialog.Result);
            _lastEffect = dialog.Result;
            Toasts.Show($"{name}（已記錄在圖層）");
        }
        else
        {
            preview.Cancel();
        }
        AfterEffect();
    }

    /// <summary>圖層屬性視窗要求重新編輯堆疊裡的某一筆。</summary>
    public async Task EditLayerEffectAsync(RasterLayer layer, LayerEffect entry)
    {
        var session = CommitPending();
        if (session == null) return;
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: false);
        var dialog = new EffectDialog(preview, entry.Effect, entry.Name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed) preview.Commit(dialog.Result);
        else preview.Cancel();
        AfterEffect();
    }

    private async Task ApplyImmediateAsync(EffectSession fx, IEffect effect, string name)
    {
        try
        {
            await ProgressDialog.RunAsync(this, name, _ => fx.RenderAndApply(effect));
        }
        catch (Exception ex)
        {
            fx.Cancel();
            Toasts.Show($"{name} 失敗：{ex.Message}");
            return;
        }
        if (fx.Commit(name))
        {
            _lastEffect = effect;
            Toasts.Show(name);
        }
    }

    private void AfterEffect()
    {
        _layersContent.Refresh();
        _layersContent.SyncPropertiesWindow();
        RefreshUiState();
        if (_repeatEffectItem != null)
        {
            // paint.net 式：選單直接寫出上次是哪個效果（「重複 高斯模糊」）
            _repeatEffectItem.IsEnabled = _lastEffect != null;
            _repeatEffectItem.Header = _lastEffect is { } last ? $"重複「{last.Name}」" : "重複上次效果";
        }
    }

    private void OnRepeatEffect()
    {
        if (_lastEffect is { } effect) _ = ApplyEffectAsync(effect, effect.Name, showDialog: false);
    }

    private void AfterDocumentResized(EditorSession session)
    {
        // 標籤由 Document.SizeChanged 統一更新（undo/redo 也才會同步）；這裡只處理縮放
        Canvas.ZoomToFit();
    }

    // ---- 圖層 ----

    private void OnAddLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            var doc = s.Document;
            var active = doc.ActiveLayer;
            var parent = active?.Parent ?? doc.Root;
            var index = active?.Parent != null ? parent.IndexOf(active) + 1 : parent.Children.Count;
            var layer = new RasterLayer { Name = $"圖層 {parent.Children.Count + 1}" };
            LayerCommands.InsertLayer(doc, s.History, parent, index, layer);
            lock (doc.SyncRoot) doc.ActiveLayer = layer;
        });

    private void OnDuplicateLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is RasterLayer layer)
            {
                LayerCommands.DuplicateLayer(s.Document, s.History, layer);
                Toasts.Show("已複製圖層");
            }
        });

    private void OnDeleteLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is { Parent: not null } layer)
            {
                LayerCommands.RemoveLayer(s.Document, s.History, layer);
                lock (s.Document.SyncRoot) s.Document.ActiveLayer = s.Document.Root.Children.LastOrDefault();
            }
        });

    private void OnMergeLayerDownClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is RasterLayer layer &&
                LayerCommands.MergeLayerDown(s.Document, s.History, layer))
                Toasts.Show("已向下合併");
            else
                Toasts.Show("下方沒有可合併的圖層");
        });

    /// <summary>圖層文字平面化：本層文字物件烙成像素（單一步 undo）。</summary>
    private void OnFlattenTextClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is not RasterLayer layer)
            {
                Toasts.Show("請先選一個圖層（群組不能平面化文字）");
                return;
            }
            if (!layer.HasElements)
            {
                Toasts.Show("這個圖層沒有文字物件");
                return;
            }
            s.SelectedElement = null; // 物件已不存在，把手框不能還指著它
            if (LayerCommands.FlattenText(s.Document, s.History, layer))
                Toasts.Show("已將文字平面化為像素");
        });

    // ---- 檢視 ----

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.25);

    private void OnActualSizeClicked(object? sender, RoutedEventArgs e) => Canvas.SetZoomPercent(100);

    private void OnBestFitClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomToFit();

    private void OnTogglePixelGridClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.ShowPixelGrid = PixelGridMenuItem.IsChecked;
        Toasts.Show(Canvas.ShowPixelGrid ? "像素格線：開（放大 500% 以上顯示）" : "像素格線：關");
    }

    // ---- 快捷鍵 ----

    /// <summary>指令 id → 動作（快捷鍵表在 <see cref="Services.ShortcutMap"/>，可在設定裡改綁）。</summary>
    private Dictionary<string, Action> _shortcutActions = null!;

    private void BuildShortcutActions()
    {
        _shortcutActions = new Dictionary<string, Action>
        {
            ["file.new"] = () => OnNewClicked(null, new RoutedEventArgs()),
            ["file.open"] = () => OnOpenClicked(null, new RoutedEventArgs()),
            ["file.save"] = () => _ = SaveAsync(saveAs: false),
            ["file.saveAs"] = () => _ = SaveAsync(saveAs: true),
            ["file.export"] = () => OnExportClicked(null, new RoutedEventArgs()),
            ["file.closeTab"] = () => OnCloseTabClicked(null, new RoutedEventArgs()),

            ["edit.undo"] = () => OnUndoClicked(null, new RoutedEventArgs()),
            ["edit.redo"] = () => OnRedoClicked(null, new RoutedEventArgs()),
            ["edit.cut"] = () => OnCutClicked(null, new RoutedEventArgs()),
            ["edit.copy"] = () => OnCopyClicked(null, new RoutedEventArgs()),
            ["edit.paste"] = () => OnPasteClicked(null, new RoutedEventArgs()),
            ["edit.selectAll"] = () => RunCommand(EditCommands.SelectAll),
            ["edit.deselect"] = () => OnDeselectClicked(null, new RoutedEventArgs()),
            ["edit.invertSelection"] = () => RunCommand(EditCommands.InvertSelection),
            ["edit.erase"] = EraseSelectionWithToast,
            ["edit.fill"] = () => OnFillSelectionClicked(null, new RoutedEventArgs()),

            ["image.crop"] = () => OnCropToSelectionClicked(null, new RoutedEventArgs()),
            ["image.rotateCw"] = () => OnRotateCwClicked(null, new RoutedEventArgs()),
            ["image.rotateCcw"] = () => OnRotateCcwClicked(null, new RoutedEventArgs()),
            ["image.rotate180"] = () => OnRotate180Clicked(null, new RoutedEventArgs()),
            ["image.flatten"] = () => OnFlattenClicked(null, new RoutedEventArgs()),

            ["layer.add"] = () => OnAddLayerClicked(null, new RoutedEventArgs()),
            ["layer.duplicate"] = () => OnDuplicateLayerClicked(null, new RoutedEventArgs()),
            ["layer.mergeDown"] = () => OnMergeLayerDownClicked(null, new RoutedEventArgs()),
            ["layer.flattenText"] = () => OnFlattenTextClicked(null, new RoutedEventArgs()),

            ["view.zoomIn"] = () => Canvas.ZoomBy(1.25),
            ["view.zoomOut"] = () => Canvas.ZoomBy(1 / 1.25),
            ["view.actualSize"] = () => Canvas.SetZoomPercent(100),
            ["view.bestFit"] = () => Canvas.ZoomToFit(),
        };
        foreach (var key in new[] { "brush", "eraser", "bgeraser", "eyedropper", "move", "rectselect", "lasso", "wand", "fill", "text", "shape" })
        {
            var toolKey = key;
            _shortcutActions[$"tool.{key}"] = () => SelectTool(toolKey);
        }

        _shortcutActions["image.resize"] = () => OnResizeImageClicked(null, new RoutedEventArgs());
        _shortcutActions["image.canvasSize"] = () => OnCanvasSizeClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.import"] = () => OnImportLayerClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipH"] = () => OnFlipLayerHorizontalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipV"] = () => OnFlipLayerVerticalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.properties"] = () => OnLayerPropertiesClicked(null, new RoutedEventArgs());

        _shortcutActions["adjust.autoLevel"] = () => _ = ApplyAutoLevelAsync();
        foreach (var entry in AdjustmentRegistry.All)
        {
            var e = entry;
            _shortcutActions[$"adjust.{e.TypeId}"] = () => _ = ApplyAdjustmentAsync(e);
        }
        _shortcutActions["effect.repeat"] = OnRepeatEffect;
    }

    private void EraseSelectionWithToast()
    {
        var hadSelection = Canvas.Session?.Selection is { IsEmpty: false };
        RunCommand(EditCommands.EraseSelection);
        // 沒有選取時會清空整層（paint.net 的行為），講清楚免得嚇到人
        Toasts.Show(hadSelection == true ? "已清除選取範圍" : "已清空整個圖層");
    }

    /// <summary>把選單的快捷鍵顯示文字同步到目前的綁定（InputGesture 只是顯示，不註冊按鍵）。</summary>
    private void SyncMenuGestures()
    {
        Walk(MainMenu);

        static void Walk(ItemsControl items)
        {
            foreach (var child in items.Items)
            {
                if (child is not MenuItem mi) continue;
                if (mi.Tag is string id) mi.InputGesture = Services.ShortcutMap.GetGesture(id);
                Walk(mi);
            }
        }
    }

    // ---- 對齊模式（按住 Tab，移動框時吸附畫布四邊與中線；按鍵可自訂） ----

    private void OnGlobalKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Services.ShortcutMap.Matches("tool.alignHold", e.Key, e.KeyModifiers))
        {
            SetAlignMode(true);
            e.Handled = true;
            return;
        }
        // Tab 無效化：就算對齊模式改綁別的鍵，Tab 也不做焦點跳轉（使用者明示）
        if (e.Key == Avalonia.Input.Key.Tab) e.Handled = true;
    }

    private void OnGlobalKeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // 放開只看鍵本身：按住期間修飾鍵可能已變，比對完整手勢會漏掉 KeyUp
        if (Services.ShortcutMap.GetGesture("tool.alignHold") is { } gesture &&
            Services.ShortcutMap.NormalizeKey(e.Key) == gesture.Key)
        {
            SetAlignMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Avalonia.Input.Key.Tab) e.Handled = true;
    }

    private void SetAlignMode(bool on)
    {
        var session = Canvas.Session;
        if (session == null || session.SnapToCanvas == on) return;
        session.SnapToCanvas = on;
        if (!on) session.SnapGuides = null; // 導線跟著模式收掉
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var session = Canvas.Session;
        if (session == null) return;

        // 打字時不攔截（畫布內編輯框、任何取得焦點的輸入框；
        // 下拉選單聚焦/展開時打字是在搜尋項目，不能被單鍵快捷鍵搶走）
        if (_canvasEditBox?.IsFocused == true) return;
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox or ComboBoxItem) return;

        // 固定別名：Ctrl+Shift+Z = 重做（與 paint.net 一致；不參與自訂）
        if (e.Key == Avalonia.Input.Key.Z &&
            e.KeyModifiers == (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift))
        {
            session.Redo();
            RefreshUiState();
            e.Handled = true;
            return;
        }

        // 變形框：Enter 落地、Esc 無損還原（情境鍵，不參與自訂）
        if (session.Transform != null && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                session.CommitTransform();
                Toasts.Show("已套用變形");
                RefreshUiState();
                e.Handled = true;
                return;
            }
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                session.CancelTransform();
                Toasts.Show("已還原變形");
                RefreshUiState();
                e.Handled = true;
                return;
            }
        }

        // 浮動選取內容：Enter 提交、Esc 還原（情境鍵，不參與自訂）
        if (session.Floating != null && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                session.CommitFloating();
                Toasts.Show("已套用移動的選取內容");
                RefreshUiState();
                e.Handled = true;
                return;
            }
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                session.CancelFloating();
                Toasts.Show("已取消移動");
                RefreshUiState();
                e.Handled = true;
                return;
            }
        }

        // 其餘一律查快捷鍵表（設定 → 快捷鍵 可改綁；CanvasView 也查同一張表）。
        // （選中向量物件時的 Delete 由 CanvasView 先攔下來刪除物件，不會走到這裡）
        var id = Services.ShortcutMap.Match(e.Key, e.KeyModifiers);
        if (id != null && _shortcutActions.TryGetValue(id, out var action))
        {
            action();
            e.Handled = true;
        }
    }

    // ---- 設定 ----

    private async void OnShortcutsClicked(object? sender, RoutedEventArgs e)
    {
        await new ShortcutsWindow().ShowDialog(this);
        Services.AppSettings.Instance.Save();
    }

    private async void OnThemeClicked(object? sender, RoutedEventArgs e)
    {
        await new ThemeWindow().ShowDialog(this);
        Services.AppSettings.Instance.Save();
    }

    // ---- 工具選項 ----

    private void WireToolOptionBars()
    {
        SizeBox.Value = 8;
        SizeBox.ValueChanged += _ => ApplyBrushOptions();
        HardnessBar.ValueChanged += _ => ApplyBrushOptions();
        SmoothingBar.ValueChanged += _ => ApplyBrushOptions();
        OpacityBar.ValueChanged += _ => ApplyBrushOptions();
        ToleranceBar.ValueChanged += _ => ApplyBrushOptions();
        SoftnessBar.ValueChanged += _ => ApplyBrushOptions();
        foreach (var k in new[] { "連續", "一次" }) BgSamplingCombo.Items.Add(k);
        foreach (var k in new[] { "連續", "不連續" }) BgLimitCombo.Items.Add(k);
        BgSamplingCombo.SelectedIndex = 0;
        BgLimitCombo.SelectedIndex = 0;
        BgSamplingCombo.SelectionChanged += (_, _) => ApplyBrushOptions();
        BgLimitCombo.SelectionChanged += (_, _) => ApplyBrushOptions();
        ProtectFgCheck.IsCheckedChanged += (_, _) => ApplyBrushOptions();

        ZoomBar.ValueChanged += v =>
        {
            if (!_suppressZoomEvents) Canvas.SetZoomPercent(v);
        };
    }

    private void ApplyBrushOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;

        var radius = (float)(SizeBox.Value / 2);
        var hardness = (float)(HardnessBar.Value / 100);
        var opacity = (float)(OpacityBar.Value / 100);
        var smoothing = (float)SmoothingBar.Value;

        foreach (var settings in new[] { session.Brush.Settings, session.Eraser.Settings })
        {
            settings.Radius = radius;
            settings.Hardness = hardness;
            settings.Opacity = opacity;
            settings.Smoothing = smoothing;
        }

        session.Shape.StrokeWidth = Math.Max(1f, (float)SizeBox.Value / 4);
        session.Tolerance = (byte)ToleranceBar.Value;

        var bg = session.BackgroundEraser.Settings;
        bg.Radius = radius;
        bg.Hardness = hardness;
        bg.Tolerance = session.Tolerance;
        bg.Softness = (float)(SoftnessBar.Value / 100);
        bg.Sampling = BgSamplingCombo.SelectedIndex == 1 ? BackgroundSampling.Once : BackgroundSampling.Continuous;
        bg.Contiguous = BgLimitCombo.SelectedIndex != 1;
        bg.ProtectForeground = ProtectFgCheck.IsChecked == true;
    }

    private void ApplyShapeOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.Shape.Kind = ShapeKindCombo.SelectedIndex switch
        {
            1 => ShapeKind.Ellipse,
            2 => ShapeKind.Line,
            _ => ShapeKind.Rectangle,
        };
        session.Shape.Filled = ShapeFilledCheck.IsChecked == true;
    }

    /// <summary>
    /// 把工具列上的文字選項寫進 session 的文字工具（新建文字的預設樣式）。
    /// 每份文件是各自的 session／工具實例，工具列才是這些預設值的真相來源 ——
    /// 少了這一步，開檔／新分頁後新建的文字會退回 TextTool 的硬編碼預設（曾經連第一份文件都是）。
    /// </summary>
    private void ApplyTextOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (FontFamilyCombo.SelectedItem is string family) session.Text.FontFamily = family;
        session.Text.FontWeight = SelectedFontWeight();
        session.Text.FontSize = (float)FontSizeBox.Value;
        session.Text.Bold = BoldToggle.IsChecked == true;
        session.Text.Italic = ItalicToggle.IsChecked == true;
        session.Text.Underline = UnderlineToggle.IsChecked == true;
        session.Text.Strikethrough = StrikeToggle.IsChecked == true;
        session.Text.Alignment =
            AlignCenterToggle.IsChecked == true ? Core.Vectors.TextAlign.Center :
            AlignRightToggle.IsChecked == true ? Core.Vectors.TextAlign.Right :
            Core.Vectors.TextAlign.Left;
    }

    // ---- 向量（文字）工具選項 ----

    private string[] _fontFamilies = [];
    private bool _suppressVectorEvents;
    private VectorElement? _textEditStart;

    private void InitVectorOptions()
    {
        // 讀取本機安裝字體；下拉清單以各字型自己的字面顯示（paint.net 式預覽）
        _fontFamilies = Services.FontCatalog.Families;
        FontFamilyCombo.ItemTemplate = Services.FontCatalog.FamilyItemTemplate(150);
        FontFamilyCombo.SelectionBoxItemTemplate = Services.FontCatalog.SelectionBoxTemplate();
        foreach (var f in _fontFamilies) FontFamilyCombo.Items.Add(f);

        var defaultIdx = Array.IndexOf(_fontFamilies, "Microsoft JhengHei");
        FontFamilyCombo.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;
        RepopulateFontStyles(FontFamilyCombo.SelectedItem as string ?? "", 400);

        foreach (var k in new[] { "矩形", "橢圓", "直線" }) ShapeKindCombo.Items.Add(k);
        ShapeKindCombo.SelectedIndex = 0;

        FontSizeBox.Value = 48;
        FontSizeBox.ValueChanged += v =>
        {
            if (_suppressVectorEvents) return;
            if (Canvas.Session is { } s) s.Text.FontSize = (float)v;
            ApplyTextEdit(el => el.WithFontSize((float)v));
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };
        FontFamilyCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            var family = FontFamilyCombo.SelectedItem as string;
            if (family == null) return;
            // 換家族時重列可用字重，並落在最接近目前字重的一檔
            var currentWeight = SelectedText?.Element.FontWeight ?? Canvas.Session?.Text.FontWeight ?? 400;
            RepopulateFontStyles(family, currentWeight);
            var weight = SelectedFontWeight();
            if (Canvas.Session is { } s)
            {
                s.Text.FontFamily = family;
                s.Text.FontWeight = weight;
            }
            ApplyTextEdit(el => el with { FontFamily = family, FontWeight = weight });
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };
        FontStyleCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            var weight = SelectedFontWeight();
            if (Canvas.Session is { } s) s.Text.FontWeight = weight;
            ApplyTextEdit(el => el with { FontWeight = weight });
            CommitTextEdit();
            UpdateCanvasEditBoxStyle();
        };

        BoldToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        ItalicToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        UnderlineToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        StrikeToggle.IsCheckedChanged += (_, _) => OnTextStyleToggled();
        WireAlignToggle(AlignLeftToggle, TextAlign.Left);
        WireAlignToggle(AlignCenterToggle, TextAlign.Center);
        WireAlignToggle(AlignRightToggle, TextAlign.Right);

        ShapeKindCombo.SelectionChanged += (_, _) => ApplyShapeOptions();
        ShapeFilledCheck.IsCheckedChanged += (_, _) => ApplyShapeOptions();
    }

    // ---- 字重／變種（Noto Sans TC 的 Light/Black 這類命名字重）----

    private Services.FontStyleOption[] _fontStyleOptions = [];
    private string? _fontStylesFamily; // 目前清單對應的家族（同家族只移選取，不重建）

    /// <summary>目前字重下拉選中的字重值（清單保證至少一項）。</summary>
    private int SelectedFontWeight()
    {
        var idx = FontStyleCombo.SelectedIndex;
        return idx >= 0 && idx < _fontStyleOptions.Length ? _fontStyleOptions[idx].Weight : 400;
    }

    /// <summary>
    /// 依家族列舉可用的直立字重（斜體交給 I 鈕），並選中最接近 preferredWeight 的一檔。
    /// 只動下拉內容，不觸發套用（呼叫端自行決定要不要套用）。
    /// </summary>
    private void RepopulateFontStyles(string family, int preferredWeight)
    {
        // 同家族：清單內容不變，只把選取移到最接近的字重。
        // 這不只是省事 —— 選字重時 CommitTextEdit → RefreshUiState → Sync 會繞回這裡，
        // 在 FontStyleCombo 自己的 SelectionChanged 裡 Items.Clear() 重建會直接 crash（重入）。
        if (family == _fontStylesFamily && _fontStyleOptions.Length > 0)
        {
            SelectClosestFontStyle(preferredWeight);
            return;
        }
        _fontStylesFamily = family;
        _fontStyleOptions = Services.FontCatalog.StylesFor(family);

        var wasSuppressed = _suppressVectorEvents;
        _suppressVectorEvents = true;
        FontStyleCombo.Items.Clear();
        foreach (var o in _fontStyleOptions) FontStyleCombo.Items.Add(o.Name);
        _suppressVectorEvents = wasSuppressed;
        SelectClosestFontStyle(preferredWeight);
    }

    /// <summary>把字重下拉的選取移到最接近的一檔（不觸發套用）。</summary>
    private void SelectClosestFontStyle(int preferredWeight)
    {
        var best = 0;
        for (var i = 1; i < _fontStyleOptions.Length; i++)
        {
            if (Math.Abs(_fontStyleOptions[i].Weight - preferredWeight) <
                Math.Abs(_fontStyleOptions[best].Weight - preferredWeight))
            {
                best = i;
            }
        }
        if (FontStyleCombo.SelectedIndex == best) return; // 同值不觸碰（重入時是 no-op）
        var wasSuppressed = _suppressVectorEvents;
        _suppressVectorEvents = true;
        FontStyleCombo.SelectedIndex = best;
        _suppressVectorEvents = wasSuppressed;
    }

    /// <summary>
    /// 某些字型 Avalonia 建不出 GlyphTypeface（變數字型集合、名稱含 # 等），直接指定
    /// FontFamily 會在排版時 crash —— 先探測，失敗就退回預設字面（Skia 渲染端自己會 fallback）。
    /// </summary>
    private static FontFamily SafeFontFamily(string name) => Services.FontCatalog.SafeFontFamily(name);

    /// <summary>B/I/U/S 任一顆變動：同步工具預設值 + 套到選中元素。</summary>
    private void OnTextStyleToggled()
    {
        if (_suppressVectorEvents) return;
        var bold = BoldToggle.IsChecked == true;
        var italic = ItalicToggle.IsChecked == true;
        var underline = UnderlineToggle.IsChecked == true;
        var strike = StrikeToggle.IsChecked == true;
        if (Canvas.Session is { } s)
        {
            s.Text.Bold = bold;
            s.Text.Italic = italic;
            s.Text.Underline = underline;
            s.Text.Strikethrough = strike;
        }
        ApplyTextEdit(el => el with
        {
            Bold = bold, Italic = italic, Underline = underline, Strikethrough = strike,
        });
        CommitTextEdit();
        UpdateCanvasEditBoxStyle();
    }

    /// <summary>對齊三顆是單選群：點下切換過去，點已選中的維持不變。</summary>
    private void WireAlignToggle(ToggleButton button, TextAlign align)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (_suppressVectorEvents) return;
            if (button.IsChecked == true)
            {
                SetAlignment(align);
            }
            else if (AlignLeftToggle.IsChecked != true &&
                     AlignCenterToggle.IsChecked != true &&
                     AlignRightToggle.IsChecked != true)
            {
                _suppressVectorEvents = true;
                button.IsChecked = true;
                _suppressVectorEvents = false;
            }
        };
    }

    private void SetAlignment(TextAlign align)
    {
        _suppressVectorEvents = true;
        AlignLeftToggle.IsChecked = align == TextAlign.Left;
        AlignCenterToggle.IsChecked = align == TextAlign.Center;
        AlignRightToggle.IsChecked = align == TextAlign.Right;
        _suppressVectorEvents = false;

        if (Canvas.Session is { } s) s.Text.Alignment = align;
        ApplyTextEdit(el => el with { Alignment = align });
        CommitTextEdit();
        UpdateCanvasEditBoxStyle();
    }

    /// <summary>取得目前選中的文字元素（layer, element）。</summary>
    private (RasterLayer Layer, TextElement Element)? SelectedText
    {
        get
        {
            var session = Canvas.Session;
            if (session?.SelectedElement is not { } sel) return null;
            if (session.Document.FindLayer(sel.LayerId) is not RasterLayer layer) return null;
            if (layer.FindElement(sel.ElementId) is not TextElement text) return null;
            return (layer, text);
        }
    }

    /// <summary>即時套用文字編輯（不進 history；CommitTextEdit 時一次補）。</summary>
    private void ApplyTextEdit(Func<TextElement, TextElement> transform)
    {
        if (_suppressVectorEvents) return;
        var session = Canvas.Session;
        if (session == null || SelectedText is not { } sel) return;

        // 畫布內編輯期間（新建或既有都一樣）：所有改動由 CommitCanvasTextEdit 一次落地成一步，
        // 不另記步驟 —— 文字內容現在是逐鍵即時寫進圖層的，中途插一步樣式 undo 會把半打好的字捲進去
        var editingCanvas = _canvasEditBox != null && _canvasEditElement?.Id == sel.Element.Id;
        if (!editingCanvas) _textEditStart ??= sel.Element;
        var updated = transform(sel.Element);
        if (Equals(updated, sel.Element)) return;

        lock (session.Document.SyncRoot)
        {
            sel.Layer.ReplaceElement(updated);
        }
        session.RefreshSelectionHandles(); // 物件的邊界變了，框要重算
    }

    /// <summary>
    /// 選色時若正在編輯或選著文字，就把顏色套到那段文字上（paint.net 式：文字工具跟著主色走）。
    /// 沒選著文字時什麼都不做 —— 主色照常更新，供之後新建的文字使用。
    /// </summary>
    private void ApplyTextColor(SKColor color)
    {
        if (SelectedText is not { } sel || sel.Element.Color == color) return;
        // 漸層的起點色跟著主色走（進階視窗的規則也是「起點＝填色」），選色才看得到變化
        ApplyTextEdit(el => el with
        {
            Color = color,
            Gradient = el.Gradient is { } g ? g with { Start = color } : null,
        }); // 落地成 undo 步驟的時機在 ColorCommitted
        UpdateCanvasEditBoxStyle(); // 畫布內編輯框的字色跟著換
    }

    /// <summary>
    /// 工具列字型下拉顯示指定家族。沒裝的字型（開別人的檔）清單裡選不到 ——
    /// 清掉選取、用 placeholder 顯示名字，別讓它停在上一個不相干的字型上。
    /// </summary>
    private void ShowFontFamily(string family)
    {
        var fi = Array.IndexOf(_fontFamilies, family);
        if (FontFamilyCombo.SelectedIndex != fi) FontFamilyCombo.SelectedIndex = fi;
        FontFamilyCombo.PlaceholderText = family;
        FontFamilyCombo.PlaceholderForeground = AppTheme.TextBrush;
    }

    private void CommitTextEdit()
    {
        var session = Canvas.Session;
        if (session == null || _textEditStart == null) return;
        var start = _textEditStart;
        _textEditStart = null;
        if (SelectedText is not { } sel || sel.Element.Id != start.Id) return;

        VectorCommands.ReplaceElement(session.Document, session.History, sel.Layer, start, sel.Element, "編輯文字");
        RefreshUiState();
    }

    // ---- 畫布內文字編輯（雙擊文字或文字工具建立後） ----

    private TextBox? _canvasEditBox;
    private RasterLayer? _canvasEditLayer;
    private TextElement? _canvasEditElement;
    private bool _canvasEditIsNew; // 單擊新建、尚未進 history（空內容落地 = 無事發生）
    private bool _canvasEditComposing; // IME 組字中（注音/拼音）：編輯框文字暫時可見

    private void StartCanvasTextEdit(RasterLayer layer, TextElement element, bool isNew)
    {
        CommitCanvasTextEdit();
        var session = Canvas.Session;
        if (session == null) return;

        // 進入文字編輯就切到文字工具（使用者明示）：工具列跟著換成字型／字級那一組，
        // 編輯完也直接停在文字工具上
        if (_currentToolKey != "text") SelectTool("text");

        _canvasEditLayer = layer;
        _canvasEditElement = element;
        _canvasEditIsNew = isNew;

        // 編輯期間不再隱藏原件：文字逐鍵即時寫回圖層、由 Skia 照常算繪（外框/陰影/漸層
        // 打字當下就看得到）；編輯框自己的字改畫透明，只留游標與選取高亮，避免重影。
        var box = new TextBox
        {
            Text = element.Text,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0), // 無外框（paint.net 式，只有游標）
            Padding = new Thickness(2),
            MinWidth = 60,
        };
        SyncCanvasEditBoxTransform(box, element);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                CommitCanvasTextEdit(cancel: true);
                Canvas.Focus();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitCanvasTextEdit();
        box.TextChanged += (_, _) => LiveApplyCanvasEditText();
        box.Loaded += (_, _) => HookImeComposition(box); // Loaded 後 TextPresenter 才一定在視覺樹裡

        _canvasEditBox = box;
        EditHost.Children.Add(box);
        UpdateCanvasEditBoxStyle(); // 字型/粗斜體/對齊/行高/顏色 + 定位
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0; // 新建為空字串（游標即起點）；既有文字接在最後
    }

    /// <summary>
    /// IME 組字（注音/拼音）期間的可見性處理：組字串是 Avalonia 直接畫在編輯框裡的
    /// （不進 Text、不發 TextChanged），而編輯框前景平常是透明的 —— 不處理就會「打注音看不到」。
    /// 監聽 TextPresenter.PreeditText：組字中 → 編輯框整段文字切回可見、Skia 那份暫時隱藏
    /// （HiddenElementId，避免重影）；選字落地 → 換回透明前景＋即時算繪。
    /// </summary>
    private void HookImeComposition(TextBox box)
    {
        if (box.FindDescendantOfType<TextPresenter>() is not { } presenter) return;
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextPresenter.PreeditTextProperty || _canvasEditBox != box) return;
            SetCanvasEditComposing(!string.IsNullOrEmpty(presenter.PreeditText));
        };
    }

    private void SetCanvasEditComposing(bool composing)
    {
        if (_canvasEditComposing == composing) return;
        _canvasEditComposing = composing;
        var session = Canvas.Session;
        if (session == null || _canvasEditLayer is not { } layer ||
            _canvasEditElement is not { } element)
        {
            return;
        }

        lock (session.Document.SyncRoot)
        {
            layer.HiddenElementId = composing ? element.Id : null;
        }
        UpdateCanvasEditBoxStyle(); // 前景可見性跟著切
    }

    /// <summary>
    /// 逐鍵把編輯框的內容寫回圖層裡的元素（不進 history —— CommitCanvasTextEdit 一次落地）。
    /// 這就是「編輯文字即時渲染」：畫布上看到的是 Skia 算繪的最終樣子（含外框/陰影/漸層）。
    /// </summary>
    private void LiveApplyCanvasEditText()
    {
        var session = Canvas.Session;
        if (session == null || _canvasEditBox is not { } box) return;
        if (_canvasEditLayer is not { } layer || CurrentCanvasEditElement() is not { } current) return;

        var text = box.Text ?? "";
        if (text == current.Text) return;
        lock (session.Document.SyncRoot)
        {
            layer.ReplaceElement(current with { Text = text });
        }
        session.RefreshSelectionHandles(); // 邊界跟著內容長
    }

    /// <summary>
    /// 讓編輯框跟上元素目前的樣式（開啟時與編輯期間工具列改樣式時都會呼叫）。
    /// 以圖層中的現行實例為準 —— 工具列的改動是即時 ReplaceElement 進圖層的。
    /// </summary>
    private void UpdateCanvasEditBoxStyle()
    {
        if (_canvasEditBox is not { } box) return;
        var current = CurrentCanvasEditElement();
        if (current == null) return;

        var family = SafeFontFamily(current.FontFamily);
        box.FontFamily = family;
        var weight = (FontWeight)Math.Clamp(
            current.Bold ? Math.Max(700, current.FontWeight) : current.FontWeight, 100, 950);
        // 探測該字重建不建得出 GlyphTypeface —— 建不出就退回一般字重，別讓排版時才炸
        try
        {
            _ = new Typeface(family, FontStyle.Normal, weight).GlyphTypeface;
            box.FontWeight = weight;
        }
        catch
        {
            box.FontWeight = FontWeight.Normal;
        }
        box.FontStyle = current.Italic ? FontStyle.Italic : FontStyle.Normal;
        box.TextAlignment = current.Alignment switch
        {
            Core.Vectors.TextAlign.Center => TextAlignment.Center,
            Core.Vectors.TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        StyleCanvasEditBox(box, current.Color, _canvasEditComposing);
        RepositionCanvasTextEdit();
    }

    /// <summary>
    /// 文字被水平拉寬/拉窄或旋轉過 → 編輯框跟著變形，維持所見即所得。
    /// 編輯期間角度可能變（角度重置鈕），所以每次重新定位都同步一次。
    /// </summary>
    private static void SyncCanvasEditBoxTransform(TextBox box, TextElement element)
    {
        var scaled = Math.Abs(element.ScaleX - 1f) > 0.001f;
        var rotated = Math.Abs(element.Rotation) > 0.01f;
        if (!scaled && !rotated)
        {
            if (box.RenderTransform != null) box.RenderTransform = null;
            return;
        }
        var transforms = new TransformGroup();
        if (scaled) transforms.Children.Add(new ScaleTransform(element.ScaleX, 1));
        if (rotated) transforms.Children.Add(new RotateTransform(element.Rotation));
        box.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        box.RenderTransform = transforms;
    }

    /// <summary>編輯中元素在圖層裡的現行實例（工具列改動會即時替換，Id 不變）。</summary>
    private TextElement? CurrentCanvasEditElement() =>
        _canvasEditElement != null
            ? _canvasEditLayer?.FindElement(_canvasEditElement.Id) as TextElement
            : null;

    /// <summary>
    /// 「最終的文字本身」由 Skia 即時算繪在畫布上（含效果），編輯框只負責游標、選取高亮
    /// 與鍵盤輸入 —— 自己的字畫成透明，避免和 Skia 的算繪重影（paint.net 式，無底色無外框）。
    /// Fluent 的 TextBox 樣板自帶深色底與框線，必須逐一覆蓋主題資源才蓋得掉。
    /// </summary>
    private static void StyleCanvasEditBox(TextBox box, SKColor textColor, bool showOwnText)
    {
        // 平常透明（字由 Skia 即時算繪）；IME 組字期間切回可見，組字串才看得到
        IBrush fgBrush = showOwnText
            ? new SolidColorBrush(Color.FromRgb(textColor.Red, textColor.Green, textColor.Blue))
            : Brushes.Transparent;
        // 游標要看得見：用字色但至少半不透明（字色全透明時游標不能跟著消失）
        var caretBrush = new SolidColorBrush(Color.FromArgb(
            Math.Max((byte)0xB0, textColor.Alpha), textColor.Red, textColor.Green, textColor.Blue));
        var accent = Color.FromRgb(0x2A, 0x9D, 0xF4);

        box.Foreground = fgBrush;
        box.Background = Brushes.Transparent; // Transparent（非 null）才吃得到點擊
        box.BorderBrush = Brushes.Transparent;
        box.CaretBrush = caretBrush;
        box.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B));
        box.SelectionForegroundBrush = fgBrush;

        // Fluent TextBox 樣板實際使用的資源鍵
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
        {
            box.Resources[key] = Brushes.Transparent;
        }

        foreach (var key in new[]
                 {
                     "TextControlForeground", "TextControlForegroundPointerOver",
                     "TextControlForegroundFocused", "TextControlForegroundDisabled",
                 })
        {
            box.Resources[key] = fgBrush;
        }

        box.Resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(accent);
    }

    private void RepositionCanvasTextEdit()
    {
        if (_canvasEditBox == null || _canvasEditElement == null) return;
        var current = CurrentCanvasEditElement() ?? _canvasEditElement;
        var view = Canvas.DocToView(current.Position);
        // 補償 Padding(2)，讓框內文字的左上角對齊元素的 Position
        Avalonia.Controls.Canvas.SetLeft(_canvasEditBox, view.X - 2);
        Avalonia.Controls.Canvas.SetTop(_canvasEditBox, view.Y - 2);
        var fontSize = Math.Max(8, current.FontSize * Canvas.Scale);
        _canvasEditBox.FontSize = fontSize;
        SyncCanvasEditBoxTransform(_canvasEditBox, current);
        // 與 TextElement 同一套排版參數（行高倍率/字距），游標位置才對得上 Skia 的算繪
        _canvasEditBox.LineHeight = fontSize * current.LineHeightScale;
        _canvasEditBox.LetterSpacing = current.LetterSpacing * Canvas.Scale;
    }

    /// <summary>畫布內文字編輯的 <see cref="IPendingEdit"/> 包裝（見該介面的說明）。</summary>
    private sealed class CanvasTextPendingEdit(MainWindow owner) : IPendingEdit
    {
        public bool IsActive => owner._canvasEditBox != null;
        public void Commit() => owner.CommitCanvasTextEdit();
    }

    private void CommitCanvasTextEdit(bool cancel = false)
    {
        var box = _canvasEditBox;
        var layer = _canvasEditLayer;
        var original = _canvasEditElement;
        var isNew = _canvasEditIsNew;
        _canvasEditBox = null;
        _canvasEditLayer = null;
        _canvasEditElement = null;
        _canvasEditIsNew = false;
        _canvasEditComposing = false;
        if (box == null || layer == null || original == null) return;

        EditHost.Children.Remove(box);
        var session = Canvas.Session;
        if (session == null) return;

        lock (session.Document.SyncRoot)
        {
            layer.HiddenElementId = null; // 可能在組字中途落地（點到別處），把 Skia 那份放回來
        }

        // 內容是逐鍵即時寫進圖層的（LiveApplyCanvasEditText），樣式改動也是 —— 圖層現行實例
        // 就是「編輯後的最終樣子」，這裡只負責一次補成單一步 undo（或取消時整個還原）。
        TextElement? current;
        lock (session.Document.SyncRoot)
        {
            current = layer.FindElement(original.Id) as TextElement;
        }
        if (current == null) return; // 元素已不存在（例如編輯中被 undo 收走）

        var newText = box.Text ?? "";
        var sameDoc = layer.Document == session.Document;
        var final = current with { Text = newText };

        if (isNew)
        {
            // 單擊建立、尚未進 history：空內容/取消 → 靜默移除（誤觸不留痕跡）；
            // 有內容 → 補單一步「新增文字」（undo 一次收掉整個元素）。
            if (cancel || newText.Length == 0 || !sameDoc)
            {
                if (session.SelectedElement?.ElementId == current.Id) session.SelectedElement = null;
                VectorCommands.DiscardElement(layer.Document ?? session.Document, layer, current.Id);
                // 文字一定自己一層：空的文字圖層一起收掉（不留痕跡）
                if (!layer.HasElements && layer.Surface.TileCount == 0 && sameDoc)
                    VectorCommands.DiscardNewTextLayer(session.Document, layer);
            }
            else
            {
                VectorCommands.CommitNewTextLayer(session.Document, session.History, layer, final, "新增文字");
                session.RefreshSelectionHandles();
            }
            _layersContent.Refresh();
        }
        else if (cancel || !sameDoc)
        {
            // Esc = 無損還原：把編輯前的原件放回去（內容與編輯期間的樣式改動一起退掉）
            lock (session.Document.SyncRoot)
            {
                if (!Equals(current, original)) layer.ReplaceElement(original);
            }
            session.RefreshSelectionHandles();
        }
        else if (newText.Length == 0)
        {
            // 內容清空 = 刪除文字（不留看不見的空元素）；undo 要能把「編輯前」的原件找回來
            VectorCommands.RemoveElement(session.Document, session.History, layer, original, "刪除文字");
            if (session.SelectedElement?.ElementId == original.Id) session.SelectedElement = null;
        }
        else if (!Equals(final, original))
        {
            // 與「編輯前」比對 —— 內容或編輯期間的樣式改動合成一步「編輯文字」
            VectorCommands.ReplaceElement(session.Document, session.History, layer,
                original, final, "編輯文字");
            session.RefreshSelectionHandles(); // 文字內容變了，框跟著變
        }
        RefreshUiState();
    }

    /// <summary>選取變化時把選中文字元素的屬性帶回 UI。</summary>
    private void SyncVectorOptionsFromSelection()
    {
        if (SelectedText is not { } sel) return;
        _suppressVectorEvents = true;
        FontSizeBox.Value = sel.Element.FontSize;
        ShowFontFamily(sel.Element.FontFamily);
        RepopulateFontStyles(sel.Element.FontFamily, sel.Element.FontWeight);
        BoldToggle.IsChecked = sel.Element.Bold;
        ItalicToggle.IsChecked = sel.Element.Italic;
        UnderlineToggle.IsChecked = sel.Element.Underline;
        StrikeToggle.IsChecked = sel.Element.Strikethrough;
        AlignLeftToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Left;
        AlignCenterToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Center;
        AlignRightToggle.IsChecked = sel.Element.Alignment == Core.Vectors.TextAlign.Right;
        _suppressVectorEvents = false;
    }

    // ---- 選取框旁的小按鈕（進階文字設定／角度重置） ----

    private Border _frameActions = null!;
    private Button _frameResetButton = null!;
    private Rect _frameActionsLast = default;

    /// <summary>
    /// 疊在畫布上、跟著把手框走的一小條按鈕：選著文字時有「進階文字設定」，
    /// 任何有框的情況都有「角度重置」（框住的東西沒有角度可重設時變灰）。
    /// 放在 EditHost（與畫布內文字編輯框同一層）；按鈕不可聚焦，點了不會把焦點從編輯框搶走。
    /// </summary>
    private void BuildFrameActions()
    {
        _frameResetButton = FrameActionButton(MaterialIconKind.Restore, "重置角度與比例（轉回 0°、回到原始比例）");
        _frameResetButton.Click += (_, _) => ResetFrameTransform();

        _frameActions = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 1,
                Children = { _frameResetButton },
            },
        };
        EditHost.Children.Add(_frameActions);

        static Button FrameActionButton(MaterialIconKind icon, string tip)
        {
            var b = new Button
            {
                Content = new MaterialIcon { Kind = icon, Width = 15, Height = 15 },
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                Focusable = false, // 不搶焦點：畫布內編輯框的 LostFocus 會落地編輯
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }
    }

    /// <summary>每幀對位：把手框的右上角外側；框在畫面外時夾回可視範圍，按鈕永遠按得到。</summary>
    private void UpdateFrameActions()
    {
        var session = Canvas.Session;
        if (session?.SelectionHandles is not { } frame || Canvas.Bounds.Width <= 0)
        {
            if (_frameActions.IsVisible) _frameActions.IsVisible = false;
            return;
        }

        var hasText = SelectedText != null;
        _frameResetButton.IsEnabled = session.CanResetTransform;
        _frameResetButton.Opacity = _frameResetButton.IsEnabled ? 1.0 : 0.4;

        // 框可能整個旋轉（變形 session）：取四個角旋轉後的外接矩形
        var deg = session.SelectionHandlesRotation;
        Span<SKPoint> corners =
        [
            new(frame.Left, frame.Top), new(frame.Right, frame.Top),
            new(frame.Right, frame.Bottom), new(frame.Left, frame.Bottom),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue;
        foreach (var c in corners)
        {
            var p = Math.Abs(deg) > 0.01f
                ? MoveTool.RotatePoint(c, new SKPoint(frame.MidX, frame.MidY), deg)
                : c;
            var v = Canvas.DocToView(p);
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            maxX = Math.Max(maxX, v.X);
        }

        var w = _frameActions.Bounds.Width > 0 ? _frameActions.Bounds.Width : 60;
        var h = _frameActions.Bounds.Height > 0 ? _frameActions.Bounds.Height : 28;
        var x = Math.Clamp(maxX + 10, 4, Math.Max(4, Canvas.Bounds.Width - w - 4));
        var y = Math.Clamp(minY, 4, Math.Max(4, Canvas.Bounds.Height - h - 4));
        var rect = new Rect(Math.Round(x), Math.Round(y), w, h);
        if (rect != _frameActionsLast)
        {
            _frameActionsLast = rect;
            Avalonia.Controls.Canvas.SetLeft(_frameActions, rect.X);
            Avalonia.Controls.Canvas.SetTop(_frameActions, rect.Y);
        }
        if (!_frameActions.IsVisible) _frameActions.IsVisible = true;
    }

    /// <summary>
    /// 重置角度與比例。畫布內編輯中的文字走 ApplyTextEdit（摺進那一步「編輯文字」），
    /// 其餘交給 session（文字物件記一步「重設角度與比例」；變形 session 回到原尺寸與 0° 不記步）。
    /// </summary>
    private void ResetFrameTransform()
    {
        var session = Canvas.Session;
        if (session == null) return;

        if (_canvasEditBox != null && SelectedText is { } sel && _canvasEditElement?.Id == sel.Element.Id)
        {
            ApplyTextEdit(el => el.WithTransformReset());
            UpdateCanvasEditBoxStyle(); // 編輯框的旋轉／拉伸跟著解掉
        }
        else
        {
            session.CommitFloating();
            session.ResetTransform();
        }
        RefreshUiState();
    }

    // ---- UI 同步 ----

    private void RefreshUiState()
    {
        var session = Canvas.Session;
        if (session == null) return;

        var fg = session.Foreground;
        CurrentColorSwatch.Background = new SolidColorBrush(Color.FromRgb(fg.Red, fg.Green, fg.Blue));
        _paletteContent.SetColor(fg);

        UndoMenuItem.IsEnabled = session.History.CanUndo;
        RedoMenuItem.IsEnabled = session.History.CanRedo;
        UndoMenuItem.Header = session.History.UndoLabel is { } ul ? $"復原 {ul}(_U)" : "復原(_U)";
        RedoMenuItem.Header = session.History.RedoLabel is { } rl ? $"重做 {rl}(_R)" : "重做(_R)";

        SyncVectorOptionsFromSelection();
    }
}
