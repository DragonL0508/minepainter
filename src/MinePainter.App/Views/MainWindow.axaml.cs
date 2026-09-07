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

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialFile)
    {
        InitializeComponent();

        // 預設最大化（使用者上次是視窗模式就沿用）；要在 Show 之前設好，
        // 不然會先閃一下 1360×860 再放大，浮動面板也要跟著重排一次
        if (Services.AppSettings.Instance.WindowMaximized) WindowState = WindowState.Maximized;

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
        Canvas.ToolWheel += OnToolWheel;
        Canvas.TextEditRequested += StartCanvasTextEdit;
        Canvas.SmoothZoom = Services.AppSettings.Instance.SmoothZoom;
        SmoothZoomMenuItem.IsChecked = Canvas.SmoothZoom;
        Rendering.GpuLayerRenderer.LodEnabled = Services.AppSettings.Instance.CanvasLod;
        CanvasLodMenuItem.IsChecked = Rendering.GpuLayerRenderer.LodEnabled;
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

        // 被 CrashLog 攔下來的例外：視窗還活著，至少讓使用者知道剛剛有事情出錯、紀錄在哪
        Services.CrashLog.Caught += msg => Dispatcher.UIThread.Post(() =>
            Toasts.Show("發生未預期的錯誤（已記錄到 crash.log）：" + msg));

        // 效果堆疊裡某條效果算爆了：Core 會略過它繼續算，但畫面上少了一層效果一定要講，不然使用者只會以為效果壞了
        LayerEffectRenderer.EffectFailed += (layer, entry, ex) =>
        {
            LogError($"效果「{entry.Name}」（圖層「{layer.Name}」）", ex);
            Dispatcher.UIThread.Post(() =>
                Toasts.Show($"效果「{entry.Name}」算不出來，已暫時略過（已記錄到 error.log）"));
        };

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

        // 切到不會操作物件的工具，文字物件的選取框也要收掉。一般圖層的框只在移動工具下出現
        // （HandleDragController.GetFrame 的 LayerContent），文字圖層卻是靠 SelectedElement
        // 撐著，換了工具照樣掛在畫面上（使用者回報）。移動／文字工具之間切換則保留選取。
        if (key is not ("move" or "text") && session.SelectedElement != null)
            session.SelectedElement = null;

        WarnIfPixelToolInFastMode(session, key);

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

    /// <summary>
    /// 畫布上的滾輪手勢落到工具選項（設定 → 快捷鍵 → 滾輪，預設 Alt + 滾輪＝筆刷大小）：
    /// 動的就是工具列上那個控制項，級距、上下限、連動都與直接在上面滾一樣。
    /// 目前工具沒有那個選項時（移動工具沒有筆刷大小）什麼都不做。
    /// </summary>
    private void OnToolWheel(string id, int direction, int notches)
    {
        switch (id)
        {
            case "wheel.brushSize" when SizeGroup.IsVisible:
                SizeBox.StepBy(direction, notches);
                Toasts.Show($"大小：{SizeBox.Value:0}");
                break;
            case "wheel.brushOpacity" when OpacityGroup.IsVisible:
                OpacityBar.StepBy(direction, notches);
                Toasts.Show($"不透明度：{OpacityBar.Value:0}%");
                break;
        }
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
        Motion.Reveal(ObjectSelectGroup, key is "rectselect" or "ellipseselect" or "lasso");
        Motion.Reveal(BgEraserGroup, key == "bgeraser");
        Motion.Reveal(TextGroup, key == "text");
        Motion.Reveal(ShapeGroup, key is "shape" or "line");
    }

    // ---- UI 同步 ----

    /// <summary>已經提醒過「這份文件在快速模式下用畫素工具」的文件（每份提醒一次就夠）。</summary>
    private readonly HashSet<Core.Documents.Document> _fastModePaintWarned = new();

    /// <summary>
    /// 快速模式下第一次拿起畫素工具時提醒一次：畫上去的像素是代理解析度的，
    /// 輸出時只能放大取樣（文字／形狀／效果則是重畫，不受影響）。
    /// </summary>
    private void WarnIfPixelToolInFastMode(Core.Tools.EditorSession session, string key)
    {
        if (!session.Document.IsFastMode) return;
        if (key is not ("brush" or "pencil" or "eraser" or "bgeraser" or "fill" or "shape" or "line" or "pen")) return;
        if (!_fastModePaintWarned.Add(session.Document)) return;

        Toasts.Show($"快速模式：畫筆是畫在 {session.Document.Width} × {session.Document.Height} 上，" +
                    "輸出時會放大取樣（文字與效果則是重畫）");
    }

    /// <summary>狀態列的尺寸文字。快速模式要看得出「畫布是代理、輸出是另一個尺寸」。</summary>
    private static string DocSizeText(Core.Documents.Document doc) =>
        doc.IsFastMode
            ? $"{doc.Width} × {doc.Height}（快速模式 → 輸出 {doc.OutputWidth} × {doc.OutputHeight}）"
            : $"{doc.Width} × {doc.Height}";

    private void RefreshUiState()
    {
        Canvas.RequestRedraw(); // 幾乎所有會改到畫面的操作最後都會經過這裡
        var session = Canvas.Session;
        if (session == null) return;

        ToFullResolutionMenuItem.IsEnabled = session.Document.IsFastMode;
        ToFastModeMenuItem.IsEnabled = !session.Document.IsFastMode &&
            Core.Documents.FastMode.ShouldOffer(session.Document.Width, session.Document.Height);

        _paletteContent.SetColor(session.Foreground);

        UndoMenuItem.IsEnabled = session.History.CanUndo;
        RedoMenuItem.IsEnabled = session.History.CanRedo;
        UndoMenuItem.Header = session.History.UndoLabel is { } ul ? $"復原 {ul}(_U)" : "復原(_U)";
        RedoMenuItem.Header = session.History.RedoLabel is { } rl ? $"重做 {rl}(_R)" : "重做(_R)";

        SyncVectorOptionsFromSelection();
    }

}
