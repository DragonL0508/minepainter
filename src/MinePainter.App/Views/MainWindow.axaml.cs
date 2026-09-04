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

public partial class MainWindow : Window
{
    // 浮動面板（真正的 OS 子視窗，可拖出主視窗）
    private readonly LayersPanel _layersContent = new();
    private readonly ToolsPanelContent _toolsContent = new();
    private readonly HistoryPanelContent _historyContent = new();
    private readonly PalettePanelContent _paletteContent = new();
    private readonly PresetsPanelContent _presetsContent = new();
    private PanelWindow _toolsPanel = null!;
    private PanelWindow _historyPanel = null!;
    private PanelWindow _layersPanel = null!;
    private PanelWindow _palettePanel = null!;
    private PanelWindow _presetsPanel = null!;
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

    /// <summary>AI 去背模型資料夾（使用者放 .onnx 的地方）；app 旁的 models 資料夾也會掃。</summary>
    public static string ModelFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinePainter", "models");

    public MainWindow(string? initialFile)
    {
        InitializeComponent();
        StartupSoundMenuItem.IsChecked = Services.AppSettings.Instance.StartupSounds;
        CheckUpdatesMenuItem.IsChecked = Services.AppSettings.Instance.CheckUpdatesOnStartup;

        // 預設最大化（使用者上次是視窗模式就沿用）；要在 Show 之前設好，
        // 不然會先閃一下 1360×860 再放大，浮動面板也要跟著重排一次
        if (Services.AppSettings.Instance.WindowMaximized) WindowState = WindowState.Maximized;

        OnnxModels.ModelDirectories.Clear();
        OnnxModels.ModelDirectories.Add(ModelFolder);
        OnnxModels.ModelDirectories.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "models"));
        var envModels = Environment.GetEnvironmentVariable("MINEPAINTER_MODELS");
        if (!string.IsNullOrEmpty(envModels)) OnnxModels.ModelDirectories.Add(envModels);

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
        Canvas.SmoothZoom = Services.AppSettings.Instance.SmoothZoom;
        SmoothZoomMenuItem.IsChecked = Canvas.SmoothZoom;
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

        // 預設集面板：雙擊／右鍵套到目前圖層；拖到畫布的落點處理在 OnDrop
        _presetsContent.SessionProvider = () => Canvas.Session;
        _presetsContent.Notify += Toasts.Show;
        _presetsContent.ApplyRequested += (preset, mode) =>
        {
            var session = CommitPending();
            if (session == null)
            {
                Toasts.Show("先開一份文件");
                return;
            }
            LayerNode? layer;
            lock (session.Document.SyncRoot)
                layer = session.Document.ActiveLayer is { CanHaveEffects: true } n ? n : null;
            if (layer == null)
            {
                Toasts.Show("調整圖層不能套效果堆疊");
                return;
            }
            switch (mode)
            {
                case PresetsPanelContent.ApplyMode.Ask: _ = ApplyPresetAskingAsync(session, layer, preset); break;
                case PresetsPanelContent.ApplyMode.Replace: ApplyPresetToLayer(session, layer, preset, replace: true); break;
                default: ApplyPresetToLayer(session, layer, preset, replace: false); break;
            }
        };

        // 浮動面板黏著主視窗：移動、改變大小、最大化都跟著走
        PositionChanged += (_, _) => RepositionPanels();
        SizeChanged += (_, _) => RepositionPanels();
        Activated += (_, _) => EnsurePanelsVisible(); // 從最小化／別的程式切回來時對齊一次

        _initialFile = initialFile;
        Opened += (_, _) =>
        {
            Services.StartupSounds.MainWindowShown();
            _ = CheckUpdatesAsync(silent: true);
            EnsureInstalledAndAssociated();
            // 之後在檔案總管點圖片，路徑會送到這裡開成新分頁，而不是再開一個程式
            Services.SingleInstance.StartServer(files =>
                Dispatcher.UIThread.Post(() => OpenFilesFromOtherInstance(files)));
            PrepareBeforeShow(); // 正常流程 App 已先呼叫過（啟動畫面期間）；這裡是保險
            ShowPanels();
            RefreshRecentFilesMenu();
            StartPerfLabelTimer();
            Canvas.Focus();
            // 字型下拉的字重列舉／GlyphTypeface 探測預熱（一秒後、閒置時做），切字型才不會第一次碰到就卡
            Avalonia.Threading.DispatcherTimer.RunOnce(Services.FontCatalog.WarmUp, TimeSpan.FromSeconds(1));

            // 開發驗證用（GUI 驗證不得注入輸入，這是看到那些畫面的正規途徑）：
            // MINEPAINTER_DEBUG_TEXTFX=1 啟動即開進階文字設定；
            // =2 另外先放一段旋轉過、含兩層外框＋陰影的文字並選取它（看得到多層外框 UI 與選取框旁的按鈕）
            // MINEPAINTER_DEBUG_OFFSCREEN=1：整個 app（含浮窗、啟動畫面）擺到主螢幕右側之外 ——
            // 開發驗證用 PrintWindow 截圖時不會跳到使用者面前干擾他們；=main 只移主視窗（啟動畫面留在原位供截圖）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OFFSCREEN") is "1" or "main" &&
                Screens.Primary is { } primary)
            {
                _debugOffscreen = true; // 驗證模式：不要把這一輪的視窗/面板配置寫回使用者的設定
                WindowState = WindowState.Normal; // 最大化的視窗搬不出螢幕，會整片蓋在使用者面前
                Position = new PixelPoint(primary.Bounds.Right + 40, primary.Bounds.Y + 40);
            }

            // MINEPAINTER_DEBUG_EFFECT=<效果或調整名稱>：先鋪一張漸層＋幾何測試圖，畫幾筆筆刷與形狀，
            // 再直接開該效果的對話框（驗證預覽與對話框佈局）；=stroke 只鋪測試圖並放大到 400%
            var debugEffect = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_EFFECT");
            if (!string.IsNullOrEmpty(debugEffect)) SeedDebugEffect(debugEffect);

            var debugTextFx = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_TEXTFX");
            if (debugTextFx is "1" or "2" or "5") SeedDebugText();


            // MINEPAINTER_DEBUG_PRESETS=1 或 =<資料夾>：啟動即打開預設集面板（搭配 MINEPAINTER_PRESETS_DIR 指到測試用的庫）；
            // MINEPAINTER_DEBUG_PRESETS_DROP=<x>,<y>：1.5 秒後把庫裡第一個預設集當作丟在畫布那個 doc 座標（驗證落點套用）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PRESETS") is { Length: > 0 } debugPresets)
            {
                PresetsToggle.IsChecked = true;
                if (debugPresets != "1") _presetsContent.ShowFolder(debugPresets);
                // MINEPAINTER_DEBUG_PRESETS_EDIT=1：1 秒後對庫裡第一個預設集開編輯器（驗證編輯視窗）
                if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PRESETS_EDIT") == "1")
                {
                    Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                    {
                        if (EffectPresetStore.LoadAll().FirstOrDefault() is { } first)
                            PresetEditor.Edit(this, first, Toasts.Show);
                    }, TimeSpan.FromMilliseconds(1000));
                }
                if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PRESETS_DROP")?.Split(',') is [var xs, var ys, .. var rest] &&
                    float.TryParse(xs, out var dx) && float.TryParse(ys, out var dy))
                {
                    // 第三個值 = 丟幾次（第二次起落在已有堆疊的圖層上，會跳覆蓋／疊加詢問）
                    var times = rest is [var ts] && int.TryParse(ts, out var t) ? t : 1;
                    for (var i = 0; i < times; i++)
                    {
                        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                        {
                            if (EffectPresetStore.LoadAll().FirstOrDefault() is { } first)
                                DropPresetOnCanvas(first, new SKPoint(dx, dy));
                        }, TimeSpan.FromMilliseconds(1500 + i * 1200));
                    }
                }
            }
            // MINEPAINTER_DEBUG_NOTOUI=1：整個主視窗直接用內嵌 Noto 當主字型（效能對照：走後備 vs 直接用）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_NOTOUI") == "1")
                FontFamily = new FontFamily(Services.EmbeddedFonts.FamilyUri);

            // MINEPAINTER_DEBUG_HIDECANVAS=1：把畫布控制項藏起來（效能對照：整幀慢是不是畫布 draw op 害的）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_HIDECANVAS") == "1") Canvas.IsVisible = false;

            // MINEPAINTER_DEBUG_OVERLAY=1：Avalonia 的渲染診斷覆蓋層（fps、dirty rect、render/layout 時間圖）
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OVERLAY") == "1")
            {
                RendererDiagnostics.DebugOverlays = Avalonia.Rendering.RendererDebugOverlays.Fps |
                                                    Avalonia.Rendering.RendererDebugOverlays.DirtyRects |
                                                    Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph |
                                                    Avalonia.Rendering.RendererDebugOverlays.LayoutTimeGraph;
            }

            // MINEPAINTER_DEBUG_MENU_CYCLE=<毫秒> + MINEPAINTER_DEBUG_PERF=<檔案>：每隔那麼久把主選單的頂層子選單
            // 關掉、開下一個（模擬滑鼠在「檔案／編輯／影像…」之間來回滑），記每次開關在 UI 執行緒的毫秒與畫布 fps
            if (int.TryParse(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_MENU_CYCLE"), out var menuCycleMs) && menuCycleMs > 0 &&
                Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF") is { Length: > 0 } menuPerfFile)
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    // 字型後備解析的成本與快取狀況：同一個字連問 200 次，看每次幾毫秒、回傳的 GlyphTypeface 是不是同一份
                    try
                    {
                        var fm = Avalonia.Media.FontManager.Current;
                        {
                            var coll = Services.EmbeddedFonts.Collection;
                            fm.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new FontFamily(Services.EmbeddedFonts.FamilyUri)), out var direct);
                            var owns = coll != null && direct != null && coll.Owns(direct);
                            var sw0 = System.Diagnostics.Stopwatch.StartNew();
                            double adv = 0;
                            if (direct != null)
                                for (var cp = 0x4E00; cp < 0x4E00 + 2000; cp++) adv += direct.GetGlyphAdvance(direct.GetGlyph((uint)cp));
                            File.AppendAllText(menuPerfFile, $"collection: {(coll?.Diagnostics ?? "null")} ownsDirect={owns} directType={direct?.GetType().Name} 2000 advances={sw0.Elapsed.TotalMilliseconds:F1}ms error={Services.EmbeddedFonts.RegisterError?.Split('\n')[0]}\n");
                            // 後備排版會不會把中文切成一字一段 run？
                            foreach (var (text, weight) in new[] { ("檔案(F) 圖層屬性範例", Avalonia.Media.FontWeight.Normal), ("檔案(F) 圖層屬性範例", Avalonia.Media.FontWeight.Normal), ("新增開啟儲存", Avalonia.Media.FontWeight.Normal), ("檔案(F) 圖層屬性範例", Avalonia.Media.FontWeight.Bold), ("Layer Properties", Avalonia.Media.FontWeight.Normal) })
                            {
                                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                                var layout = new Avalonia.Media.TextFormatting.TextLayout(text, new Avalonia.Media.Typeface(FontFamily.Default, Avalonia.Media.FontStyle.Normal, weight), 13, Brushes.White);
                                var ms2 = sw2.Elapsed.TotalMilliseconds;
                                var runs = layout.TextLines.Sum(l => l.TextRuns.Count);
                                var desc = string.Join(" | ", layout.TextLines.SelectMany(l => l.TextRuns).Select(r => r is Avalonia.Media.TextFormatting.ShapedTextRun s ? $"{s.Properties.Typeface.FontFamily.Name}/{s.Properties.Typeface.Weight}:{s.Length}" : r.GetType().Name));
                                File.AppendAllText(menuPerfFile, $"layout '{text}' {weight}: {ms2:F2}ms runs={runs} [{desc}]\n");
                            }
                            // 拆開：排版（HarfBuzz）vs GlyphRun（bounds）vs 第二次建同一段
                            try
                            {
                                var cjk = "圖層屬性範例文字";
                                fm.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new FontFamily(Services.EmbeddedFonts.FamilyUri)), out var notoGt);
                                if (notoGt != null)
                                {
                                    var props = new Avalonia.Media.TextFormatting.TextShaperOptions(notoGt, 13);
                                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                                    var shaped = Avalonia.Media.TextFormatting.TextShaper.Current.ShapeText(cjk.AsMemory(), props);
                                    var shapeMs = sw3.Elapsed.TotalMilliseconds; sw3.Restart();
                                    var shaped2 = Avalonia.Media.TextFormatting.TextShaper.Current.ShapeText(cjk.AsMemory(), props);
                                    var shape2Ms = sw3.Elapsed.TotalMilliseconds; sw3.Restart();
                                    var run = new Avalonia.Media.GlyphRun(notoGt, 13, cjk.AsMemory(), shaped);
                                    _ = run.Bounds;
                                    var runMs = sw3.Elapsed.TotalMilliseconds; sw3.Restart();
                                    var run2 = new Avalonia.Media.GlyphRun(notoGt, 13, cjk.AsMemory(), shaped2);
                                    _ = run2.Bounds;
                                    var run2Ms = sw3.Elapsed.TotalMilliseconds; sw3.Restart();
                                    var lay = new Avalonia.Media.TextFormatting.TextLayout(cjk, new Avalonia.Media.Typeface(new FontFamily(Services.EmbeddedFonts.FamilyUri)), 13, Brushes.White);
                                    var lay1 = sw3.Elapsed.TotalMilliseconds; sw3.Restart();
                                    var lay2o = new Avalonia.Media.TextFormatting.TextLayout(cjk, new Avalonia.Media.Typeface(new FontFamily(Services.EmbeddedFonts.FamilyUri)), 13, Brushes.White);
                                    var lay2 = sw3.Elapsed.TotalMilliseconds;
                                    File.AppendAllText(menuPerfFile, $"split: shape={shapeMs:F2} shapeAgain={shape2Ms:F2} glyphRun={runMs:F2} glyphRunAgain={run2Ms:F2} layoutNoto={lay1:F2} layoutNotoAgain={lay2:F2} (ms)\n");
                                }
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(menuPerfFile, $"split EXCEPTION {ex.Message}\n");
                            }
                            // 含 bounds 的 GetGlyphWidths（要讀字形輪廓）：Avalonia 的 GlyphRunImpl 建構子就是這樣算 Bounds
                            if (Core.Vectors.BundledFont.Typeface is { } noto)
                            {
                                var glyphs = Enumerable.Range(0x4E00, 300).Select(cp => (ushort)noto.GetGlyph(cp)).ToArray();
                                using var memFont = new SkiaSharp.SKFont(noto, 13);
                                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                                memFont.GetGlyphWidths(glyphs, new float[glyphs.Length], new SkiaSharp.SKRect[glyphs.Length]);
                                var memMs = sw1.Elapsed.TotalMilliseconds;
                                using var streamTf = SkiaSharp.SKTypeface.FromStream(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MinePainter.App/Assets/Fonts/NotoSansTC-Regular.otf")));
                                using var streamFont = new SkiaSharp.SKFont(streamTf, 13);
                                var glyphs2 = Enumerable.Range(0x4E00, 300).Select(cp => (ushort)streamTf.GetGlyph(cp)).ToArray();
                                sw1.Restart();
                                streamFont.GetGlyphWidths(glyphs2, new float[glyphs2.Length], new SkiaSharp.SKRect[glyphs2.Length]);
                                var streamMs = sw1.Elapsed.TotalMilliseconds;
                                File.AppendAllText(menuPerfFile, $"GetGlyphWidths+bounds 300 glyphs: memory={memMs:F1}ms stream={streamMs:F1}ms\n");
                            }
                        }
                        foreach (var weight in new[] { Avalonia.Media.FontWeight.Normal, Avalonia.Media.FontWeight.Bold })
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            object? first = null;
                            var same = 0;
                            var found = 0;
                            for (var i = 0; i < 200; i++)
                            {
                                if (fm.TryMatchCharacter('圖', Avalonia.Media.FontStyle.Normal, weight, Avalonia.Media.FontStretch.Normal, null, null, out var tf))
                                {
                                    found++;
                                    var gt = tf.GlyphTypeface;
                                    if (first == null) first = gt;
                                    else if (ReferenceEquals(first, gt)) same++;
                                }
                            }
                            var ms = sw.Elapsed.TotalMilliseconds;
                            var name = first is Avalonia.Media.IGlyphTypeface g ? g.FamilyName + "/" + g.Weight + "/" + g.GetType().Name + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g) : "null";
                            File.AppendAllText(menuPerfFile, $"fallback weight={weight}: 200 calls {ms:F1}ms ({ms / 200:F3}ms each) found={found} sameInstance={same}/199 typeface={name}\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(menuPerfFile, $"fallback bench EXCEPTION {ex.Message}\n");
                    }

                    var tops = MainMenu.Items.OfType<MenuItem>().ToList();
                    var index = -1;
                    int switches = 0, layouts = 0;
                    double total = 0, max = 0;
                    LayoutUpdated += (_, _) => layouts++;
                    var cycle = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(menuCycleMs) };
                    cycle.Tick += (_, _) =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            if (index >= 0) tops[index].Close();
                            index = (index + 1) % tops.Count;
                            if (!MainMenu.IsOpen) MainMenu.Open();
                            tops[index].Open();
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(menuPerfFile, $"  [menu] EXCEPTION: {ex}\n");
                        }
                        var ms = sw.Elapsed.TotalMilliseconds;
                        switches++;
                        total += ms;
                        max = Math.Max(max, ms);
                    };
                    cycle.Start();
                    var perf = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    var lastFrame = Canvas.Stats.FrameIndex;
                    var gc0 = GC.CollectionCount(0);
                    perf.Tick += (_, _) =>
                    {
                        var frames = Canvas.Stats.FrameIndex - lastFrame;
                        lastFrame = Canvas.Stats.FrameIndex;
                        var gcNow = GC.CollectionCount(0);
                        File.AppendAllText(menuPerfFile,
                            $"{DateTime.Now:HH:mm:ss} menu fps={Canvas.Stats.Fps:F0} canvasFrames={frames} uiTickGap={Canvas.TakeMaxTickGapMs():F0}ms layouts={layouts} switches={switches} " +
                            (Rendering.TextBench.Enabled ? $"bench latin={Rendering.TextBench.LatinMs:F2} cjk={Rendering.TextBench.CjkMs:F2} cjkBold={Rendering.TextBench.CjkBoldMs:F2} cjkNewFont={Rendering.TextBench.CjkNewFontMs:F2} cjkStream={Rendering.TextBench.CjkStreamMs:F2} cjkStreamNewGlyphs={Rendering.TextBench.CjkAvaloniaMs:F2} fontCache={Rendering.TextBench.FontCacheUsed / 1024}K/{Rendering.TextBench.FontCacheLimit / 1024}K " : "") +
                            $"avgMs={(switches > 0 ? total / switches : 0):F1} maxMs={max:F1} gc0={gcNow - gc0}\n");
                        switches = layouts = 0;
                        total = max = 0;
                        gc0 = gcNow;
                    };
                    perf.Start();
                }, TimeSpan.FromMilliseconds(1500));
            }

            if (debugTextFx is "3" or "4" or "5")
            {
                // =3：切到文字工具、1.5 秒後把工具列的字型下拉打開（驗證下拉清單首次開啟的渲染）；
                // =4 不開下拉（效能對照組）；=5 先放一段選取中的文字（同 =2）再開下拉
                SelectTool("text");
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    // 視窗沒有焦點時 light-dismiss 會立刻把下拉關掉（截不到）；驗證模式先關掉它
                    foreach (var popup in FontFamilyCombo.GetTemplateChildren().OfType<Popup>())
                        popup.IsLightDismissEnabled = false;
                    if (debugTextFx != "4") FontFamilyCombo.IsDropDownOpen = true;

                    // MINEPAINTER_DEBUG_PERF=<檔案>：每秒把畫布 fps、主視窗與下拉 popup 的 layout 次數寫進去；
                    // MINEPAINTER_DEBUG_PERF_CYCLE=<毫秒>：每隔那麼久把字型下拉切到下一個字型（模擬使用者連續切換），
                    // 一併記每次切換在 UI 執行緒花的毫秒（平均／最大）與 GC 次數
                    if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF") is { Length: > 0 } perfFile)
                    {
                        // MINEPAINTER_DEBUG_PERF_BENCH=1：先對所有字型量一遍兩個嫌疑函式（各自的總時間與最慢的家族）
                        if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF_BENCH") == "1")
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            double stylesTotal = 0, safeTotal = 0, stylesMax = 0, safeMax = 0;
                            string stylesWorst = "", safeWorst = "";
                            foreach (var fam in _fontFamilies)
                            {
                                var t0 = sw.Elapsed.TotalMilliseconds;
                                Services.FontCatalog.StylesFor(fam);
                                var t1 = sw.Elapsed.TotalMilliseconds;
                                Services.FontCatalog.SafeFontFamily(fam);
                                var t2 = sw.Elapsed.TotalMilliseconds;
                                stylesTotal += t1 - t0;
                                safeTotal += t2 - t1;
                                if (t1 - t0 > stylesMax) { stylesMax = t1 - t0; stylesWorst = fam; }
                                if (t2 - t1 > safeMax) { safeMax = t2 - t1; safeWorst = fam; }
                            }
                            var t3 = sw.Elapsed.TotalMilliseconds;
                            foreach (var fam in _fontFamilies) Services.FontCatalog.SafeFontFamily(fam);
                            var safeSecond = sw.Elapsed.TotalMilliseconds - t3;
                            File.AppendAllText(perfFile,
                                $"bench families={_fontFamilies.Length} StylesFor total={stylesTotal:F0}ms max={stylesMax:F1}ms ({stylesWorst}) " +
                                $"SafeFontFamily total={safeTotal:F0}ms max={safeMax:F1}ms ({safeWorst}) secondPass={safeSecond:F0}ms\n");
                        }
                        int mainLayout = 0, popupLayout = 0, switches = 0;
                        double switchMs = 0, switchMax = 0;
                        var gc0 = GC.CollectionCount(0);
                        LayoutUpdated += (_, _) => mainLayout++;
                        var popup = FontFamilyCombo.GetTemplateChildren().OfType<Popup>().FirstOrDefault();
                        if (popup?.Child is { } child)
                        {
                            child.LayoutUpdated += (_, _) => popupLayout++;
                            if (TopLevel.GetTopLevel(child) is { } popupRoot)
                                popupRoot.LayoutUpdated += (_, _) => popupLayout++;
                        }
                        if (int.TryParse(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF_CYCLE"), out var cycleMs) && cycleMs > 0)
                        {
                            var cycle = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(cycleMs) };
                            cycle.Tick += (_, _) =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                try
                                {
                                    FontFamilyCombo.SelectedIndex = (FontFamilyCombo.SelectedIndex + 1) % FontFamilyCombo.ItemCount;
                                }
                                catch (Exception ex)
                                {
                                    File.AppendAllText(perfFile, $"  [cycle] EXCEPTION at {FontFamilyCombo.SelectedItem}: {ex}\n");
                                }
                                var ms = sw.Elapsed.TotalMilliseconds;
                                switches++;
                                switchMs += ms;
                                switchMax = Math.Max(switchMax, ms);
                            };
                            cycle.Start();
                        }
                        var perfTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        var lastFrame = Canvas.Stats.FrameIndex;
                        perfTimer.Tick += (_, _) =>
                        {
                            var frames = Canvas.Stats.FrameIndex - lastFrame;
                            lastFrame = Canvas.Stats.FrameIndex;
                            var gcNow = GC.CollectionCount(0);
                            File.AppendAllText(perfFile,
                                $"{DateTime.Now:HH:mm:ss} uiTickGap={Canvas.TakeMaxTickGapMs():F0}ms canvasFrames={frames} mainLayout={mainLayout} popupLayout={popupLayout} " +
                                $"switches={switches} avgMs={(switches > 0 ? switchMs / switches : 0):F1} maxMs={switchMax:F1} gc0={gcNow - gc0} font={FontFamilyCombo.SelectedItem}\n");
                            mainLayout = popupLayout = switches = 0;
                            switchMs = switchMax = 0;
                            gc0 = gcNow;
                        };
                        perfTimer.Start();
                    }
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

    /// <summary>MINEPAINTER_DEBUG_OFFSCREEN 驗證模式：視窗被硬搬到螢幕外，配置不該存回設定。</summary>
    private bool _debugOffscreen;

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
            // =quad：移動工具、透視模式、對整層開變形並拉動兩個角（看四角把手框與工具列「變形」群組）
            // =warp：扭曲（彎曲）模式，拉動幾個網格控制點
            if (name is "quad" or "warp")
            {
                SelectTool("move");
                var mode = name == "warp" ? TransformMode.Warp : TransformMode.Perspective;
                SetTransformMode(mode);
                if (session.EnterTransformMode(mode) is { } t)
                {
                    if (t.Warp is { } w)
                    {
                        var m = Core.Tools.WarpMesh.Drag(w, 5, new SKPoint(0, doc.Height * 0.18f));
                        m = Core.Tools.WarpMesh.Drag(m, 10, new SKPoint(0, -doc.Height * 0.18f));
                        m = Core.Tools.WarpMesh.Drag(m, 3, new SKPoint(-doc.Width * 0.08f, doc.Height * 0.1f));
                        t.SetWarp(m);
                    }
                    else if (t.Quad != null)
                    {
                        t.SetQuad(Core.Tools.QuadGeometry.DistortDrag(t.Quad!, 2, new SKPoint(-doc.Width * 0.15f, doc.Height * 0.12f), false));
                        t.SetQuad(Core.Tools.QuadGeometry.PerspectiveDrag(t.Quad!, 0, new SKPoint(doc.Width * 0.1f, 0)));
                    }
                    t.Apply(preview: false);
                    session.RefreshSelectionHandles();
                }
                RefreshUiState();
                return;
            }
            // =pen：鋼筆工具，種一條含平滑點的開放路徑（看路徑／錨點／把手的繪製與工具列群組）
            if (name == "pen")
            {
                SelectTool("pen");
                var w = doc.Width; var h = doc.Height;
                session.PenPath = new Core.Vectors.PenPath(
                [
                    Core.Vectors.PenAnchor.Corner(new SKPoint(w * 0.15f, h * 0.7f)),
                    new Core.Vectors.PenAnchor(new SKPoint(w * 0.4f, h * 0.25f), new SKPoint(w * 0.28f, h * 0.25f), new SKPoint(w * 0.52f, h * 0.25f)),
                    new Core.Vectors.PenAnchor(new SKPoint(w * 0.7f, h * 0.6f), new SKPoint(w * 0.62f, h * 0.45f), new SKPoint(w * 0.78f, h * 0.75f)),
                    Core.Vectors.PenAnchor.Corner(new SKPoint(w * 0.9f, h * 0.3f)),
                ], Closed: false, Finished: false, Active: 2);
                RefreshUiState();
                return;
            }
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
            if (fx != null) _ = ApplyEffectAsync(Services.EffectParamMemory.Recall(fx.Create(), Canvas.Session?.Foreground ?? SKColors.Black), fx.Name, showDialog: true);
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
        Wire(() => _presetsPanel, PresetsToggle);

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
                SchedulePanelLayoutSave(); // 開關狀態也記住
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
        _presetsPanel = Create(new PanelWindow("預設集", _presetsContent, 440, resizableHeight: 400), PresetsToggle);

        PanelWindow Create(PanelWindow panel, ToggleButton toggle)
        {
            panel.CloseRequested += () => toggle.IsChecked = false;
            panel.PositionChanged += (_, _) => OnPanelMoved(panel);
            panel.KeyFallback = HandlePanelKey; // 焦點在面板上時的快捷鍵（Ctrl+Z…）
            panel.SizeChanged += (_, _) => SchedulePanelLayoutSave(); // 拉大小也記住
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
            _panelSaveTimer.Tick += (_, _) => SavePanelLayout(withWindowState: false);
            _panelsPlaced = true;
            var frame = MainWorkArea();
            Place(_toolsPanel, new PanelAnchor(false, false, Px(18), Px(96)));
            Place(_palettePanel, new PanelAnchor(false, true, Px(18), Px(470)));
            Place(_layersPanel, new PanelAnchor(true, false, Px(348), Px(96)));
            Place(_historyPanel, new PanelAnchor(true, true, Px(286), Px(380)));
            Place(_presetsPanel, new PanelAnchor(false, true, Px(18 + 330 + 12), Px(420))); // 調色盤右邊、貼底
            RestorePanelLayout(frame); // 上次關掉時的位置／大小／開關蓋過預設排法

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

        SchedulePanelLayoutSave();

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
        yield return (_presetsPanel, PresetsToggle);
    }

    // ---- 面板配置的記憶（settings.json）----

    /// <summary>設定檔裡的面板 id（與 <see cref="PanelPairs"/> 同順序）。</summary>
    private static readonly string[] PanelIds = ["tools", "history", "layers", "palette", "presets"];

    /// <summary>
    /// 套用上次關掉時記下的面板配置：貼哪一組邊、距離、大小、開關。
    /// 沒記錄過（第一次啟動、或設定檔壞掉）就維持內建預設排法。
    /// </summary>
    private void RestorePanelLayout(PixelRect frame)
    {
        var saved = Services.AppSettings.Instance.Panels;
        if (saved.Count == 0) return;

        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var id = PanelIds[i++];
            if (!saved.TryGetValue(id, out var layout)) continue;

            var anchor = new PanelAnchor(layout.Right, layout.Bottom, layout.OffsetX, layout.OffsetY);
            panel.Anchor = anchor;
            panel.Position = AnchoredPosition(anchor, frame);
            // 大小只有「可拉大小」的面板記得住（工具／調色盤的高度是隨內容算的）
            if (panel.IsResizable && layout.Width >= panel.MinWidth && layout.Height >= panel.MinHeight)
            {
                panel.Width = layout.Width;
                panel.Height = layout.Height;
            }
            toggle.IsChecked = layout.Visible;
        }
    }

    /// <summary>
    /// 面板動過了 → 1 秒後把配置寫回設定。拖曳中每一次 PositionChanged 都寫檔太吵，
    /// 只在關閉時寫又會被當掉／強制結束整碗端走。
    /// </summary>
    private readonly Avalonia.Threading.DispatcherTimer _panelSaveTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    private void SchedulePanelLayoutSave()
    {
        if (!_panelsPlaced) return;
        _panelSaveTimer.Stop();
        _panelSaveTimer.Start();
    }

    /// <summary>
    /// 把目前的面板配置寫回設定。<paramref name="withWindowState"/>＝連「有沒有最大化」一起記
    /// （只有真的關視窗那次才算數：拖曳中的存檔看到的可能是最小化／驗證模式硬拉成視窗的狀態）。
    /// </summary>
    private void SavePanelLayout(bool withWindowState)
    {
        _panelSaveTimer.Stop();
        if (!_panelsPlaced || _debugOffscreen) return;
        var settings = Services.AppSettings.Instance;
        var i = 0;
        foreach (var (panel, toggle) in PanelPairs())
        {
            var id = PanelIds[i++];
            if (panel.Anchor is not { } a) continue;
            settings.Panels[id] = new Services.AppSettings.PanelLayout
            {
                Right = a.Right,
                Bottom = a.Bottom,
                OffsetX = a.OffsetX,
                OffsetY = a.OffsetY,
                Width = panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width,
                Height = panel.Bounds.Height > 0 ? panel.Bounds.Height : panel.Height,
                Visible = toggle.IsChecked == true,
            };
        }
        // 最小化中關掉的話 WindowState 是 Minimized，那不是使用者想記住的狀態
        if (withWindowState && WindowState != WindowState.Minimized)
            settings.WindowMaximized = WindowState == WindowState.Maximized;
        settings.Save();
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
            "pencil" => session.Pencil,
            "eraser" => session.Eraser,
            "bgeraser" => session.BackgroundEraser,
            "eyedropper" => session.Eyedropper,
            "move" => session.Move,
            "rectselect" => session.RectSelect,
            "ellipseselect" => session.EllipseSelect,
            "lasso" => session.Lasso,
            "wand" => session.Wand,
            "fill" => session.Fill,
            "text" => session.Text,
            "shape" or "line" => session.Shape,
            "pen" => session.Pen,
            _ => session.Brush,
        };

        // 直線是形狀工具的一種：按鈕直接把下拉切過去（下拉仍是唯一真相來源）
        if (key is "shape" or "line")
        {
            var wanted = key == "line" ? 2 : ShapeKindCombo.SelectedIndex == 2 ? 0 : ShapeKindCombo.SelectedIndex;
            if (ShapeKindCombo.SelectedIndex != wanted) ShapeKindCombo.SelectedIndex = wanted;
        }

        _toolsContent.SetActive(key);
        ActiveToolLabel.Text = key == "line" ? "直線" : session.ActiveTool.Name;
        UpdateToolOptions(key);
        if (changed) Toasts.Show($"工具：{session.ActiveTool.Name}");
        if (key != "text") Canvas.Focus();
    }

    /// <summary>依工具顯示對應的選項群組（單行內切換，不改變工具列高度）。</summary>
    private void UpdateToolOptions(string key)
    {
        // 新出現的群組從下方 4px 淡入；消失的立刻收掉（淡出中會佔位，單行版面會跳）
        Motion.Reveal(SizeGroup, key is "brush" or "pencil" or "eraser" or "bgeraser" or "shape" or "line" or "pen");
        Motion.Reveal(TransformGroup, key == "move");
        Motion.Reveal(PenGroup, key == "pen");
        Motion.Reveal(HardnessGroup, key is "brush" or "eraser" or "bgeraser");
        Motion.Reveal(SmoothingGroup, key is "brush" or "eraser");
        Motion.Reveal(OpacityGroup, key is "brush" or "pencil" or "eraser" or "fill");
        Motion.Reveal(ToleranceGroup, key is "fill" or "wand" or "bgeraser");
        Motion.Reveal(BgEraserGroup, key == "bgeraser");
        Motion.Reveal(TextGroup, key == "text");
        Motion.Reveal(ShapeGroup, key is "shape" or "line");
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
        DocSizeLabel.Text = $"{session.Document.Width} × {session.Document.Height}";
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
        if (PresetsPanelContent.PresetFrom(e.Data) != null)
        {
            // 預設集只能丟在畫布上（有文件時）
            e.DragEffects = Canvas.Session != null && IsOverCanvas(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.DragEffects = DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private bool IsOverCanvas(DragEventArgs e)
    {
        var p = e.GetPosition(Canvas);
        return p.X >= 0 && p.Y >= 0 && p.X < Canvas.Bounds.Width && p.Y < Canvas.Bounds.Height;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (PresetsPanelContent.PresetFrom(e.Data) is { } preset)
        {
            e.Handled = true;
            if (!IsOverCanvas(e)) return;
            DropPresetOnCanvas(preset, Canvas.ViewToDoc(e.GetPosition(Canvas)));
            return;
        }

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

    // ---- 效果預設集拖進畫布 ----

    /// <summary>
    /// 預設集丟在畫布上：落點底下最上面「看得到且有像素」的點陣圖層就是目標
    /// （像 Premiere 把預設集丟到剪輯上）；落在空白處就套到目前圖層。
    /// </summary>
    private void DropPresetOnCanvas(EffectPreset preset, SKPoint docPoint)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        LayerNode? target;
        bool hit;
        lock (doc.SyncRoot)
        {
            target = LayerAtLocked(doc, docPoint);
            hit = target != null;
            // 落在空白處：套到目前選的東西（群組也可以 —— 整組一起吃）
            target ??= doc.ActiveLayer is { CanHaveEffects: true } active
                ? active
                : doc.Descendants().OfType<RasterLayer>().LastOrDefault();
        }
        if (target == null)
        {
            Toasts.Show("文件裡沒有可以套效果的圖層或群組");
            return;
        }
        if (hit)
        {
            lock (doc.SyncRoot) doc.ActiveLayer = target; // 套到哪層就選哪層，圖層面板看得到
        }
        _ = ApplyPresetAskingAsync(session, target, preset);
    }

    /// <summary>圖層還沒有效果堆疊就直接套；已經有了就問要覆蓋還是疊加。</summary>
    private async Task ApplyPresetAskingAsync(EditorSession session, LayerNode layer, EffectPreset preset)
    {
        IReadOnlyList<LayerEffect> existing;
        lock (session.Document.SyncRoot) existing = layer.Effects;
        if (existing.Count == 0)
        {
            ApplyPresetToLayer(session, layer, preset, replace: false);
            return;
        }
        var dialog = new PresetApplyDialog(preset.Name, layer.Name, existing.Select(e => e.Name).ToList());
        await dialog.ShowDialog(this);
        if (dialog.Result == PresetApplyDialog.Choice.Cancel) return;
        ApplyPresetToLayer(session, layer, preset, replace: dialog.Result == PresetApplyDialog.Choice.Replace);
    }

    /// <summary>落點（doc 座標）附近 5×5 內有不透明像素的最上層點陣圖層（含效果輸出）；沒有就 null。</summary>
    private static RasterLayer? LayerAtLocked(MinePainter.Core.Documents.Document doc, SKPoint p)
    {
        var x = (int)MathF.Floor(p.X);
        var y = (int)MathF.Floor(p.Y);
        if (x < 0 || y < 0 || x >= doc.Width || y >= doc.Height) return null;

        foreach (var node in doc.Descendants().Reverse())
        {
            if (node is not RasterLayer layer || !IsShown(layer)) continue;
            var lx = x - layer.Offset.X;
            var ly = y - layer.Offset.Y;
            var rect = new SKRectI(lx - 2, ly - 2, lx + 3, ly + 3);
            var pixels = layer.EffectsRendered
                ? LayerEffectRenderer.ReadPixels(layer.DisplaySurface, rect)
                : LayerEffectRenderer.ReadPixelsWithElements(layer, rect);
            if (pixels.Any(px => (px >> 24) != 0)) return layer;
        }
        return null;

        static bool IsShown(LayerNode node)
        {
            for (LayerNode? n = node; n != null; n = n.Parent)
            {
                if (!n.IsVisible || n.Opacity <= 0f) return false;
            }
            return true;
        }
    }

    /// <summary>套用預設集到某層（一步 undo），同步圖層面板／屬性視窗並回報。</summary>
    private void ApplyPresetToLayer(EditorSession session, LayerNode layer, EffectPreset preset, bool replace)
    {
        if (preset.Effects.Count == 0)
        {
            Toasts.Show($"預設集「{preset.Name}」是空的");
            return;
        }
        EffectPresetStore.Apply(session, layer, preset, replace);
        _layersContent.Refresh();
        _layersContent.SyncPropertiesWindow();
        RefreshUiState();
        Toasts.Show(replace
            ? $"已用預設集「{preset.Name}」取代「{layer.Name}」的效果堆疊"
            : $"已把預設集「{preset.Name}」套到「{layer.Name}」");
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
            RememberRecentFile(path);
            if (Path.GetExtension(path).Equals(".mpp", StringComparison.OrdinalIgnoreCase))
            {
                var doc = MppFormat.Load(path);
                SetDocument(doc, path);
                WarnAboutMissingFonts(doc, Path.GetFileName(path));
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

    // ---- 最近使用的檔案 ----

    /// <summary>清單長度上限（paint.net 也是 10 個左右）。</summary>
    private const int MaxRecentFiles = 10;

    /// <summary>
    /// 把一個檔案記進「最近使用」（最新在最前面、去重、去掉不存在的）。
    /// 開啟與儲存都會走到這裡 —— 另存新檔之後那個新路徑才是使用者下次要找的。
    /// </summary>
    private void RememberRecentFile(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return; // 路徑怪到 GetFullPath 都不行：不值得為了這個擋住開檔
        }

        var settings = Services.AppSettings.Instance;
        settings.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, full);
        if (settings.RecentFiles.Count > MaxRecentFiles)
            settings.RecentFiles.RemoveRange(MaxRecentFiles, settings.RecentFiles.Count - MaxRecentFiles);
        settings.Save();
        RefreshRecentFilesMenu();
    }

    /// <summary>
    /// 重建「最近使用的檔案」子選單。檔案被搬走／刪掉就從清單移除 ——
    /// 點下去才發現開不了是最沒用的回饋。
    /// </summary>
    private void RefreshRecentFilesMenu()
    {
        var settings = Services.AppSettings.Instance;
        var alive = settings.RecentFiles.Where(File.Exists).ToList();
        if (alive.Count != settings.RecentFiles.Count)
        {
            settings.RecentFiles = alive;
            settings.Save();
        }

        RecentFilesMenu.Items.Clear();
        RecentFilesMenu.IsEnabled = alive.Count > 0;
        if (alive.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "（沒有記錄）", IsEnabled = false });
            return;
        }

        for (var i = 0; i < alive.Count; i++)
        {
            var path = alive[i];
            // 前 9 個給數字快捷鍵（_1…_9），跟 Windows 的檔案選單一樣好按
            var prefix = i < 9 ? $"_{i + 1}  " : "     ";
            var item = new MenuItem { Header = prefix + Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) =>
            {
                if (!File.Exists(path))
                {
                    Toasts.Show($"找不到 {Path.GetFileName(path)}（已從清單移除）");
                    RefreshRecentFilesMenu();
                    return;
                }
                OpenFile(path);
            };
            RecentFilesMenu.Items.Add(item);
        }

        RecentFilesMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除清單(_C)" };
        clear.Click += (_, _) =>
        {
            settings.RecentFiles.Clear();
            settings.Save();
            RefreshRecentFilesMenu();
        };
        RecentFilesMenu.Items.Add(clear);
    }

    /// <summary>.pdn 只能讀不能寫，所以當成匯入：不記成目前檔案，之後存檔會走「另存為 .mpp」。</summary>
    private void OpenPaintDotNetFile(string path)
    {
        var doc = PdnFormat.Load(path, out var warnings);
        SetDocument(doc, importedName: Path.GetFileName(path));

        Toasts.Show("已匯入 paint.net 專案（儲存時會存成 .mpp）");
        foreach (var warning in warnings.Take(2)) Toasts.Show(warning);
        WarnAboutMissingFonts(doc, Path.GetFileName(path));
    }

    /// <summary>
    /// 專案檔用到的字型這台機器沒裝就跳視窗說明。檔案只記家族名，換一台機器沒裝那支，
    /// Skia 會安靜地換一支畫出來 —— 排版跑掉卻沒有任何提示，所以要主動講。
    /// 對話框在文件已經開好、畫面看得到之後才彈（使用者可以一邊看著那份文件一邊讀）。
    /// </summary>
    private void WarnAboutMissingFonts(MinePainter.Core.Documents.Document doc, string fileName)
    {
        IReadOnlyList<MinePainter.Core.Vectors.MissingFont> missing;
        lock (doc.SyncRoot) missing = MinePainter.Core.Vectors.FontAvailability.MissingIn(doc);
        if (missing.Count == 0) return;

        var projectName = Path.GetFileNameWithoutExtension(fileName);
        Dispatcher.UIThread.Post(async () =>
        {
            if (!IsVisible) return;
            var dialog = new MissingFontsDialog(projectName, missing);
            await dialog.ShowDialog(this);
            if (!dialog.Confirmed || dialog.Replacements.Count == 0) return;

            // 找回那份文件所屬的分頁：對話框開著的時候使用者可能已經切走了
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Session.Document, doc));
            if (tab == null) return;
            var replaced = VectorCommands.ReplaceFontFamilies(
                doc, tab.Session.History, dialog.Replacements, "替換缺少的字型");
            if (replaced == 0) return;

            doc.NotifyChanged(doc.Bounds);
            _layersContent.Refresh();
            RefreshUiState();
            Toasts.Show($"已替換 {dialog.Replacements.Count} 種字型（{replaced} 段文字）");
        }, DispatcherPriority.Background);
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
            RememberRecentFile(path);
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

    /// <summary>
    /// 小工具「YouTube 縮圖預覽」：把目前文件的合成結果塞進一份本機的假 YouTube 頁面，
    /// 用系統預設瀏覽器開起來看縮圖在真實版面裡的樣子（不連網、不上傳）。
    /// </summary>
    private async void OnYouTubePreviewClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session == null)
        {
            Toasts.Show("先開一份文件");
            return;
        }

        CommitCanvasTextEdit();       // 預覽的是合成結果，先把進行中的編輯落地
        session.CommitPendingEdits(); // 浮動內容、變形框等所有進行中編輯一次涵蓋

        var dialog = new YouTubePreviewWindow(SuggestedName("我的縮圖"));
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;

        var doc = session.Document;
        var options = dialog.Options;
        try
        {
            // 合成 + base64 內嵌對大圖不算便宜，丟背景執行緒免得視窗卡住
            var path = await Task.Run(() => Gadgets.YouTubeMockup.Render(doc, options));
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Toasts.Show("已在瀏覽器開啟 YouTube 縮圖預覽");
        }
        catch (Exception ex)
        {
            Toasts.Show("縮圖預覽失敗：" + ex.Message);
            LogError("YouTube 縮圖預覽", ex);
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
            SavePanelLayout(withWindowState: true); // 面板還在才問得到位置
            foreach (var (panel, _) in PanelPairs()) panel.AllowClose();
            foreach (var owned in OwnedWindows.ToList()) owned.Close(); // 圖層屬性等臨時視窗
            if (_pendingUpdaterScript is { } script)
            {
                // 更新：程式結束後由 updater 覆蓋 exe 並重啟
                try { Services.UpdateService.Launch(script); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"updater launch failed: {ex.Message}"); }
                _pendingUpdaterScript = null;
            }
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

        using var image = session.CopyToImage(out var origin);
        if (image == null)
        {
            Toasts.Show("沒有可複製的內容");
            return;
        }
        Toasts.Show(Platform.ClipboardImage.TrySetImage(image, origin)
            ? $"已複製 {image.Width} × {image.Height}"
            : "複製失敗：無法存取剪貼簿");
    }

    private void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        using var image = session.CopyToImage(out var origin);
        if (image == null)
        {
            Toasts.Show("沒有可剪下的內容");
            return;
        }
        if (!Platform.ClipboardImage.TrySetImage(image, origin))
        {
            Toasts.Show("剪下失敗：無法存取剪貼簿");
            return;
        }

        // 剪下 = 複製 + 挖掉。挖不掉的（文字圖層沒有像素、群組不是繪製對象）就只是複製，
        // 不能報「已剪下」—— 內容其實還在。
        if (session.Document.ActiveLayer is not RasterLayer { IsTextLayer: false })
        {
            Toasts.Show("已複製；這個圖層的內容不能剪下（文字要先平面化、群組要選裡面的圖層）");
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

    /// <summary>
    /// 貼上位置：本程式複製的內容貼回原座標（換圖層、換文件都一樣，位置不會被重置），
    /// 外來影像則放在目前可視範圍的左上角。兩者都夾到「整張影像盡量放得進畫布」的範圍。
    /// </summary>
    private SKPointI PastePosition(EditorSession session, int width, int height)
    {
        var doc = session.Document;
        var topLeft = Platform.ClipboardImage.TryGetCopyOrigin(width, height) is { } copyOrigin
            ? new SKPoint(copyOrigin.X, copyOrigin.Y)
            : Canvas.ViewToDoc(new Point(0, 0));
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
        var resample = dialog.Resample; // 交給背景執行緒前先在 UI 執行緒讀完（不得懶讀控制項）
        try
        {
            await ProgressDialog.RunAsync(this, "調整影像大小", _ => ImageCommands.ResizeImage(session, w, h, resample: resample));
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

    private void OpenModelFolder()
    {
        System.IO.Directory.CreateDirectory(ModelFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ModelFolder) { UseShellExecute = true });
    }

    private void OnModelFolderClicked(object? sender, RoutedEventArgs e) => OpenModelFolder();

    private async void OnDownloadModelsClicked(object? sender, RoutedEventArgs e) => await ShowModelDownloadAsync();

    /// <summary>開下載模型對話框；有裝好東西就重新掃資料夾。回傳現在掃得到的模型。</summary>
    private async Task<IReadOnlyList<OnnxModelInfo>> ShowModelDownloadAsync()
    {
        var dialog = new ModelDownloadWindow(ModelFolder);
        await dialog.ShowDialog(this);
        return OnnxModels.Scan();
    }

    /// <summary>圖層 → AI 去背：對話框選模型與選項，處理完直接寫進圖層（先平面化；一步 undo）。</summary>
    private async void OnRemoveBackgroundClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層");
            return;
        }
        var models = OnnxModels.Scan();
        if (models.Count == 0)
        {
            // 還沒有模型就直接請他下載，而不是丟一句話叫他自己去找 .onnx
            models = await ShowModelDownloadAsync();
            if (models.Count == 0) return;
        }
        session.SelectedElement = null; // 平面化後物件不存在，把手框不能還指著它
        var dialog = new BackgroundRemovalWindow(session, layer, models);
        await dialog.ShowDialog(this);
        if (dialog.Error != null) Toasts.Show("AI 去背失敗：" + dialog.Error);
        else if (dialog.Applied) Toasts.Show(dialog.Note == null ? "AI 去背完成" : "AI 去背完成：" + dialog.Note);
        _layersContent.Refresh();
        RefreshUiState();
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
                item.Click += (_, _) => _ = ApplyEffectAsync(Services.EffectParamMemory.Recall(e.Create(), Canvas.Session?.Foreground ?? SKColors.Black), e.Name, showDialog: true);
                sub.Items.Add(item);
            }
            EffectsMenu.Items.Add(sub);
        }
    }

    private Task ApplyAdjustmentAsync(AdjustmentRegistry.Entry entry) =>
        ApplyEffectAsync(Services.EffectParamMemory.Recall(new AdjustmentEffect(entry.CreateDefault()), Canvas.Session?.Foreground ?? SKColors.Black), entry.DisplayName, entry.HasDialog);

    private async Task ApplyAutoLevelAsync()
    {
        var session = CommitPending();
        if (session == null) return;
        // 群組：自動色階也走群組的效果堆疊（直方圖取整組合成後的樣子）
        if (session.Document.ActiveLayer is GroupLayer group)
        {
            var groupEntry = LayerEffect.Create(new AdjustmentEffect(new LevelsAdjustment()),
                session.Selection?.Clone().Mask, session.Foreground);
            using var groupPreview = new LayerEffectPreview(session, group, groupEntry, isNew: true);
            var groupLevels = LevelsAdjustment.FromHistogram(groupPreview.Histogram());
            groupPreview.Commit(new AdjustmentEffect(groupLevels));
            _lastEffect = new AdjustmentEffect(groupLevels);
            Toasts.Show("自動色階（已記錄在群組）");
            AfterEffect();
            return;
        }
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

        // 群組：效果一律進群組的效果堆疊（作用在整組合成後的樣子，組內每一層都吃得到）
        if (session.Document.ActiveLayer is GroupLayer group)
        {
            await ApplyToLayerStackAsync(session, group, effect, name, showDialog);
            return;
        }
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層或群組");
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
                Services.EffectParamMemory.Remember(dialog.Result);
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
    private async Task ApplyToLayerStackAsync(EditorSession session, LayerNode layer, IEffect effect, string name, bool showDialog)
    {
        var entry = LayerEffect.Create(effect, session.Selection?.Clone().Mask, session.Foreground);
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: true);
        if (!showDialog)
        {
            preview.Commit(effect);
            _lastEffect = effect;
            Toasts.Show($"{name}（已記錄在{(layer is GroupLayer ? "群組" : "圖層")}）");
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
            Services.EffectParamMemory.Remember(dialog.Result);
            Toasts.Show($"{name}（已記錄在{(layer is GroupLayer ? "群組" : "圖層")}）");
        }
        else
        {
            preview.Cancel();
        }
        AfterEffect();
    }

    /// <summary>圖層屬性視窗要求重新編輯堆疊裡的某一筆。</summary>
    public async Task EditLayerEffectAsync(LayerNode layer, LayerEffect entry)
    {
        var session = CommitPending();
        if (session == null) return;
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: false);
        var dialog = new EffectDialog(preview, entry.Effect, entry.Name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            preview.Commit(dialog.Result);
            Services.EffectParamMemory.Remember(dialog.Result);
        }
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

    // ---- 更新 ----

    /// <summary>更新已下載好時的 updater 腳本；程式關閉的最後一步啟動它。</summary>
    private string? _pendingUpdaterScript;
    private bool _checkingUpdates;

    private void OnCheckUpdatesClicked(object? sender, RoutedEventArgs e) => _ = CheckUpdatesAsync(silent: false);

    private void OnToggleCheckUpdatesClicked(object? sender, RoutedEventArgs e)
    {
        Services.AppSettings.Instance.CheckUpdatesOnStartup = CheckUpdatesMenuItem.IsChecked;
        Services.AppSettings.Instance.Save();
        Toasts.Show(CheckUpdatesMenuItem.IsChecked ? "啟動時檢查更新：開" : "啟動時檢查更新：關");
    }

    /// <summary>
    /// 查 GitHub 最新版。silent＝啟動時的靜默檢查：沒新版、查不到、開發建置、使用者略過的版本都不出聲；
    /// 手動檢查則每種結果都回報。
    /// </summary>
    private async Task CheckUpdatesAsync(bool silent)
    {
        if (_checkingUpdates) return;
        if (silent)
        {
            if (!Services.AppSettings.Instance.CheckUpdatesOnStartup) return;
            if (Services.UpdateService.IsDevBuild || !Services.UpdateService.IsSupported) return;
            await Task.Delay(TimeSpan.FromSeconds(3)); // 讓啟動先安靜完成
        }
        else if (!Services.UpdateService.IsSupported)
        {
            Toasts.Show("這個建置不支援程式內更新，請到下載頁取得新版");
            return;
        }

        _checkingUpdates = true;
        Services.UpdateInfo? info;
        try
        {
            info = await Services.UpdateService.CheckAsync();
        }
        catch (Exception ex)
        {
            if (!silent) Toasts.Show("檢查更新失敗：" + ex.Message);
            _checkingUpdates = false;
            return;
        }
        _checkingUpdates = false;

        if (info == null)
        {
            if (!silent) Toasts.Show($"已是最新版（{Services.UpdateService.CurrentVersion.ToString(3)}）");
            return;
        }
        if (silent && string.Equals(Services.AppSettings.Instance.SkippedUpdateTag, info.Tag, StringComparison.OrdinalIgnoreCase))
            return;

        var dialog = new UpdateDialog(info);
        await dialog.ShowDialog(this);
        switch (dialog.Result)
        {
            case UpdateDialog.Choice.Skip:
                Services.AppSettings.Instance.SkippedUpdateTag = info.Tag;
                Services.AppSettings.Instance.Save();
                break;
            case UpdateDialog.Choice.Update when dialog.UpdaterScript != null:
                _pendingUpdaterScript = dialog.UpdaterScript;
                Close(); // 走正常關閉流程（未儲存會問）；真的關掉時才啟動 updater
                break;
        }
    }

    private void OnToggleStartupSoundClicked(object? sender, RoutedEventArgs e)
    {
        Services.AppSettings.Instance.StartupSounds = StartupSoundMenuItem.IsChecked;
        Services.AppSettings.Instance.Save();
        Toasts.Show(StartupSoundMenuItem.IsChecked ? "啟動音效：開" : "啟動音效：關");
    }

    private void OnTogglePixelGridClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.ShowPixelGrid = PixelGridMenuItem.IsChecked;
        Toasts.Show(Canvas.ShowPixelGrid ? "像素格線：開（放大 300% 以上顯示）" : "像素格線：關");
    }

    private void OnToggleSmoothZoomClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.SmoothZoom = SmoothZoomMenuItem.IsChecked;
        Services.AppSettings.Instance.SmoothZoom = Canvas.SmoothZoom;
        Services.AppSettings.Instance.Save();
        Toasts.Show(Canvas.SmoothZoom ? "放大時平滑取樣：開（只影響顯示）" : "放大時平滑取樣：關（顯示真實像素）");
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
        foreach (var key in new[] { "brush", "pencil", "eraser", "bgeraser", "eyedropper", "move", "rectselect", "ellipseselect", "lasso", "wand", "fill", "text", "shape", "line", "pen" })
        {
            var toolKey = key;
            _shortcutActions[$"tool.{key}"] = () => SelectTool(toolKey);
        }
        _shortcutActions["layer.transformFree"] = () => BeginTransformFromMenu(TransformMode.Free);
        _shortcutActions["layer.transformPerspective"] = () => BeginTransformFromMenu(TransformMode.Perspective);
        _shortcutActions["layer.transformDistort"] = () => BeginTransformFromMenu(TransformMode.Warp);

        _shortcutActions["image.resize"] = () => OnResizeImageClicked(null, new RoutedEventArgs());
        _shortcutActions["image.canvasSize"] = () => OnCanvasSizeClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.import"] = () => OnImportLayerClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipH"] = () => OnFlipLayerHorizontalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipV"] = () => OnFlipLayerVerticalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.properties"] = () => OnLayerPropertiesClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.removeBackground"] = () => OnRemoveBackgroundClicked(null, new RoutedEventArgs());
        _shortcutActions["gadget.youtubePreview"] = () => OnYouTubePreviewClicked(null, new RoutedEventArgs());

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

    /// <summary>
    /// 浮動面板（獨立 OS 視窗）沒處理掉的按鍵：當成主視窗的按鍵處理一次。
    /// 面板上的焦點不該讓整套快捷鍵失效 —— 使用者按了「新增圖層」之後就想直接 Ctrl+Z。
    /// </summary>
    private void HandlePanelKey(Avalonia.Input.KeyEventArgs e)
    {
        OnGlobalKeyDown(this, e);
        if (!e.Handled) OnKeyDown(e);
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

        // 鋼筆路徑：Enter 轉為選取、Esc 清除、Backspace 退一個錨點（情境鍵，不參與自訂）
        if (session.ActiveTool == session.Pen && session.PenPath != null &&
            e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.Enter:
                    PenMakeSelection();
                    e.Handled = true;
                    return;
                case Avalonia.Input.Key.Escape:
                    PenCommands.Clear(session);
                    Toasts.Show("已清除路徑");
                    e.Handled = true;
                    return;
                case Avalonia.Input.Key.Back:
                    PenCommands.RemoveLast(session);
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

    private async void OnFileAssociationsClicked(object? sender, RoutedEventArgs e)
    {
        await new FileAssociationsWindow().ShowDialog(this);
        Services.AppSettings.Instance.Save();
    }

    /// <summary>
    /// 另一個 MinePainter 程序把使用者點開的檔案轉過來（同 paint.net：不再開一個視窗）：
    /// 開成新分頁並把視窗叫到前景。
    /// </summary>
    private void OpenFilesFromOtherInstance(string[] files)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = Services.AppSettings.Instance.WindowMaximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        Activate();
        foreach (var file in files)
        {
            if (File.Exists(file)) OpenFile(file);
        }
    }

    /// <summary>
    /// 啟動時在背景做兩件事：（1）還沒安裝就裝到 %LocalAppData%\Programs\MinePainter，
    /// 檔案關聯才有不會被搬走的落點；（2）把關聯對齊現況 —— 第一次執行自動登記，
    /// 之後只在目標路徑變了時改寫。複製 exe 與寫登錄檔都要一段時間，不放在啟動路徑上。
    /// </summary>
    private void EnsureInstalledAndAssociated() => Task.Run(() =>
    {
        var settings = Services.AppSettings.Instance;

        var installed = false;
        if (settings.AutoInstall)
        {
            try
            {
                installed = Services.AppInstaller.EnsureInstalled();
            }
            catch
            {
                // 沒權限／磁碟滿了之類：照樣往下登記關聯（會指向現在這份 exe）
            }
        }

        bool registered;
        try
        {
            // 剛裝好＝全新的一次設定，關聯要跟著重建（例如安裝資料夾被手動刪掉過）
            registered = Services.FileAssociations.EnsureUpToDate(
                settings.FileAssociationsOptOut, settings.FileAssociationsRegistered && !installed);
        }
        catch
        {
            registered = false; // 登錄檔被政策鎖住之類：關聯沒了不影響其他功能
        }

        if (!installed && !registered) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (registered) settings.FileAssociationsRegistered = true;
            settings.Save();
            // 使用者的 exe 是自己解 zip 放的，突然多一份在 AppData 會嚇到人，講一聲
            if (installed) Toasts.Show("MinePainter 已安裝到本機，開始功能表找得到");
        });
    });

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
        session.Pen.StrokeWidth = (float)SizeBox.Value; // 鋼筆「描邊路徑」的線寬共用「大小」

        foreach (var settings in new[] { session.Brush.Settings, session.Eraser.Settings })
        {
            settings.Radius = radius;
            settings.Hardness = hardness;
            settings.Opacity = opacity;
            settings.Smoothing = smoothing;
        }

        // 鉛筆：大小與不透明度跟著工具列，硬度／平滑固定（像素繪圖不做羽化與手抖平滑）
        var pencil = session.Pencil.Settings;
        pencil.Radius = radius;
        pencil.Opacity = opacity;

        session.Shape.StrokeWidth = Math.Max(1f, (float)SizeBox.Value / 4);
        session.Tolerance = (byte)Math.Round(ToleranceBar.Value * 2.55); // 滑桿 0..100%，工具吃 0..255

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

        // 下拉切到／離開「直線」時，工具面板的高亮跟著換（兩顆鈕是同一個工具的兩種形狀）
        if (_currentToolKey is "shape" or "line")
        {
            _currentToolKey = session.Shape.Kind == ShapeKind.Line ? "line" : "shape";
            _toolsContent.SetActive(_currentToolKey);
            ActiveToolLabel.Text = _currentToolKey == "line" ? "直線" : session.Shape.Name;
        }
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

        // 預設微軟正黑；英文版 Windows 常常沒裝中文字型，退到內嵌的 Noto Sans TC
        var defaultIdx = Array.IndexOf(_fontFamilies, "Microsoft JhengHei");
        if (defaultIdx < 0) defaultIdx = Array.IndexOf(_fontFamilies, Services.EmbeddedFonts.FamilyName);
        FontFamilyCombo.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;
        if (FontFamilyCombo.SelectedItem is string picked && Canvas.Session is { } textSession)
            textSession.Text.FontFamily = picked;
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
            PerfTrace.Begin();
            RepopulateFontStyles(family, currentWeight);
            PerfTrace.Lap("styles");
            var weight = SelectedFontWeight();
            if (Canvas.Session is { } s)
            {
                s.Text.FontFamily = family;
                s.Text.FontWeight = weight;
            }
            ApplyTextEdit(el => el with { FontFamily = family, FontWeight = weight });
            PerfTrace.Lap("applyEdit");
            CommitTextEdit();
            PerfTrace.Lap("commit");
            UpdateCanvasEditBoxStyle();
            PerfTrace.Lap("editBox");
            PerfTrace.End("fontSwitch");
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

        WireTransformToggle(TransformFreeToggle, TransformMode.Free);
        WireTransformToggle(TransformPerspectiveToggle, TransformMode.Perspective);
        WireTransformToggle(TransformWarpToggle, TransformMode.Warp);

        PenSelectButton.Click += (_, _) => PenMakeSelection();
        PenStrokeButton.Click += (_, _) => RunPenCommand(s => PenCommands.StrokePath(s, s.Pen.StrokeWidth), "已沿路徑描邊");
        PenFillButton.Click += (_, _) => RunPenCommand(PenCommands.FillPath, "已填滿路徑");
        PenClearButton.Click += (_, _) => RunPenCommand(s => { PenCommands.Clear(s); return true; }, "已清除路徑");
    }

    // ---- 變形模式（移動工具的工具列群組）----

    private bool _suppressTransformToggle;

    /// <summary>三個互斥的變形模式鈕：選一個其餘自動關；點已選中的維持選中。</summary>
    private void WireTransformToggle(ToggleButton button, TransformMode mode)
    {
        button.IsCheckedChanged += (_, _) =>
        {
            if (_suppressTransformToggle) return;
            if (button.IsChecked == true)
            {
                SetTransformMode(mode);
            }
            else if (CurrentTransformMode() == mode)
            {
                _suppressTransformToggle = true;
                button.IsChecked = true;
                _suppressTransformToggle = false;
            }
        };
    }

    private TransformMode CurrentTransformMode()
    {
        if (TransformPerspectiveToggle.IsChecked == true) return TransformMode.Perspective;
        if (TransformWarpToggle.IsChecked == true) return TransformMode.Warp;
        return TransformMode.Free;
    }

    /// <summary>
    /// 切換變形模式。變形中切到自由變形時先落地目前的四角變形（四角映射回不到矩形模式；
    /// 落地後再拖角會從原始像素續接，不糊）；切到透視／扭曲時現有 session 直接進四角模式。
    /// </summary>
    private void SetTransformMode(TransformMode mode)
    {
        _suppressTransformToggle = true;
        TransformFreeToggle.IsChecked = mode == TransformMode.Free;
        TransformPerspectiveToggle.IsChecked = mode == TransformMode.Perspective;
        TransformWarpToggle.IsChecked = mode == TransformMode.Warp;
        _suppressTransformToggle = false;

        var session = Canvas.Session;
        if (session == null) return;
        session.Move.TransformMode = mode;

        if (session.Transform is { } t)
        {
            // 網格模式之間或回到自由變形：網格映射回不到矩形模式，先落地（續接／重新框都不糊）
            var needsCommit = mode == TransformMode.Free ? t.IsMeshMode
                : mode == TransformMode.Perspective ? t.Warp != null
                : t.Quad != null && t.IsQuadChanged;
            if (needsCommit) session.CommitTransform();
            if (mode != TransformMode.Free) session.EnterTransformMode(mode);
        }
        session.RefreshSelectionHandles(); // 沒在變形也要換把手（4 角／16 控制點／8 把手）
        RefreshUiState();
    }

    /// <summary>把工具列的變形模式推進 session（每份文件各自的工具實例）。</summary>
    private void ApplyMoveOptions()
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.Move.TransformMode = CurrentTransformMode();
    }

    /// <summary>圖層 → 變形 → …：切到移動工具、設定模式、立刻框住圖層內容開始變形。</summary>
    private void BeginTransformFromMenu(TransformMode mode)
    {
        var session = Canvas.Session;
        if (session == null) return;
        session.CommitPendingEdits();
        SelectTool("move");
        SetTransformMode(mode);
        if (session.EnterTransformMode(mode) == null) return;
        Toasts.Show(mode switch
        {
            TransformMode.Perspective => "透視：拖四角（Shift＝只動一角）；Enter 套用、Esc 還原",
            TransformMode.Warp => "扭曲：拖網格上的 16 個控制點；Enter 套用、Esc 還原",
            _ => "自由變形：拖角縮放、右鍵旋轉；Enter 套用、Esc 還原",
        });
        RefreshUiState();
    }

    private void OnTransformFreeClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Free);
    private void OnTransformPerspectiveClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Perspective);
    private void OnTransformDistortClicked(object? sender, RoutedEventArgs e) => BeginTransformFromMenu(TransformMode.Warp);

    // ---- 鋼筆 ----

    private void PenMakeSelection()
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (PenCommands.MakeSelection(session)) Toasts.Show("路徑已轉為選取範圍");
        RefreshUiState();
        Canvas.Focus();
    }

    private void RunPenCommand(Func<EditorSession, bool> command, string doneMessage)
    {
        var session = Canvas.Session;
        if (session == null) return;
        if (command(session)) Toasts.Show(doneMessage);
        RefreshUiState();
        Canvas.Focus();
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
            Motion.SetVisible(_frameActions, false);
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
        Motion.SetVisible(_frameActions, true);
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
        Canvas.RequestRedraw(); // 幾乎所有會改到畫面的操作最後都會經過這裡
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

    // ---- 整窗 layout 計時（MINEPAINTER_DEBUG_PERF 用；根節點的 Measure/Arrange 就是整棵樹）----
    private static readonly bool PerfEnabled = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF") is { Length: > 0 };
    private double _measureMs, _arrangeMs, _measureMax;
    private int _measureCount;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!PerfEnabled) return base.MeasureOverride(availableSize);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = base.MeasureOverride(availableSize);
        var ms = sw.Elapsed.TotalMilliseconds;
        _measureMs += ms;
        _measureMax = Math.Max(_measureMax, ms);
        _measureCount++;
        return r;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!PerfEnabled) return base.ArrangeOverride(finalSize);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = base.ArrangeOverride(finalSize);
        _arrangeMs += sw.Elapsed.TotalMilliseconds;
        return r;
    }

    private string TakeLayoutPerf()
    {
        var text = $"measure={_measureMs:F0}ms/{_measureCount}x(max {_measureMax:F0}) arrange={_arrangeMs:F0}ms";
        _measureMs = _arrangeMs = _measureMax = 0;
        _measureCount = 0;
        return text;
    }

    /// <summary>MINEPAINTER_DEBUG_PERF 有設時，把一段流程各步的毫秒寫進同一個記錄檔（沒設就全是空操作）。</summary>
    private static class PerfTrace
    {
        private static readonly string? File = Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_PERF");
        private static readonly System.Diagnostics.Stopwatch Watch = new();
        private static readonly System.Text.StringBuilder Line = new();
        private static double _last;

        public static void Begin()
        {
            if (File == null) return;
            Watch.Restart();
            _last = 0;
            Line.Clear();
        }

        public static void Lap(string name)
        {
            if (File == null) return;
            var now = Watch.Elapsed.TotalMilliseconds;
            Line.Append($" {name}={now - _last:F1}");
            _last = now;
        }

        public static void End(string what)
        {
            if (File == null) return;
            System.IO.File.AppendAllText(File, $"  [{what}] total={Watch.Elapsed.TotalMilliseconds:F1}{Line}\n");
        }
    }
}
