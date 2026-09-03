using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MinePainter.App.Rendering;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 畫布控制項：滾輪（上下平移／Shift 左右平移／Ctrl 縮放）、中鍵、空白鍵 → viewport；
/// 其餘 pointer 事件轉交 EditorSession.ActiveTool。
/// 仍採連續重繪（RequestAnimationFrame 迴圈）量測效能；之後改成 dirty 驅動。
/// </summary>
public sealed class CanvasView : Control
{
    private readonly FrameStats _stats = new();

    /// <summary>目前顯示中的視圖（動畫過程中會逐幀逼近 _targetViewport）。</summary>
    private ViewportTransform _viewport = ViewportTransform.Identity;

    /// <summary>縮放的目標視圖；滾輪改的是它，畫面再平滑跟上。</summary>
    private ViewportTransform _targetViewport = ViewportTransform.Identity;

    private readonly System.Diagnostics.Stopwatch _animClock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastAnimSeconds;

    private EditorSession? _session;

    private bool _viewportInitialized;
    // 最近一次「自動 fit」的結果與當時的控制項大小：視口仍停在這個 fit 上而控制項大小又變了
    // （典型：啟動時先以預設視窗大小算一次 fit、下一瞬間視窗才最大化），就重新 fit 一次置中。
    // 使用者一旦縮放／平移過（視口 != fit）就不再干預。
    private ViewportTransform? _autoFit;
    private Size _autoFitSize;
    private bool _spaceDown;
    private bool _panning;

    // ---- 筆刷游標（畫筆型工具畫成筆刷實際大小的虛線圈）----
    private Point _hoverView;
    private bool _pointerInside;
    private bool _brushCursorShown;

    /// <summary>圈小於這個螢幕半徑就看不出是圈了，改回十字游標。</summary>
    private const double MinBrushCursorRadius = 3.5;

    // 黑白交錯的虛線：任何底色上都看得見（純白圈在亮圖上、純黑圈在暗圖上都會消失）
    private static readonly Pen BrushCursorPenDark =
        new(Brushes.Black, 1, new DashStyle([4, 4], 0));
    private static readonly Pen BrushCursorPenLight =
        new(Brushes.White, 1, new DashStyle([4, 4], 4));
    private bool _toolActive;
    private Point _lastPointerView;
    private bool _animationRunning;

    /// <summary>工具事件後發出（前景色/undo 狀態等 UI 需要刷新）。</summary>
    public event Action? StateChanged;

    /// <summary>縮放/平移變化（狀態列縮放條同步用）。</summary>
    public event Action? ViewportChanged;

    /// <summary>
    /// 每一幀（UI 執行緒）發出：畫布本來就連續重繪，疊在畫布上的 Avalonia 控制項
    /// （選取框旁的小按鈕）跟著這個對位，不必追蹤所有會改動把手框的路徑。
    /// </summary>
    public event Action? FrameTick;

    /// <summary>游標的 doc 座標（狀態列顯示用）。</summary>
    public event Action<SKPoint>? PointerDocMoved;

    /// <summary>
    /// 要求開啟畫布內文字編輯。第三個參數 isNew = 單擊剛建立、尚未進 history
    /// （落地時空內容 = 靜默移除）；false = 雙擊既有文字（游標接在末端）。
    /// </summary>
    public event Action<Core.Layers.RasterLayer, Core.Vectors.TextElement, bool>? TextEditRequested;

    public double ZoomPercent => _viewport.Scale * 100;

    public double Scale => _viewport.Scale;

    /// <summary>渲染統計（狀態列顯示 FPS / 合成中 tile 數）。</summary>
    public FrameStats Stats => _stats;

    /// <summary>是否顯示像素格線（放大 300% 以上才實際繪製）。</summary>
    public bool ShowPixelGrid { get; set; }

    /// <summary>放大時雙線性插值顯示（預設關：顯示真實像素、硬邊）。只影響上屏，不影響文件。</summary>
    public bool SmoothZoom { get; set; }

    /// <summary>doc 座標 → 此控制項的 view 座標。</summary>
    public Point DocToView(SKPoint doc) => _viewport.DocToView(new Point(doc.X, doc.Y));

    /// <summary>此控制項的 view 座標 → doc 座標（貼上定位在可視範圍用）。</summary>
    public SKPoint ViewToDoc(Point view)
    {
        var p = _viewport.ViewToDoc(view);
        return new SKPoint((float)p.X, (float)p.Y);
    }

    public void SetZoomPercent(double percent) =>
        _targetViewport = _targetViewport.WithScaleAroundCenter(percent / 100.0, Bounds.Width, Bounds.Height);

    /// <summary>縮放到剛好容納整份文件。</summary>
    public void ZoomToFit()
    {
        var doc = _session?.Document;
        if (doc == null || Bounds.Width <= 0) return;
        _targetViewport = ViewportTransform.Fit(doc.Width, doc.Height, Bounds.Width, Bounds.Height);
        _autoFit = _targetViewport;
        _autoFitSize = Bounds.Size;
    }

    /// <summary>以畫面中心為錨點縮放（選單的放大/縮小用）。</summary>
    public void ZoomBy(double factor)
    {
        if (Bounds.Width <= 0) return;
        _targetViewport = _targetViewport.ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);
    }

    public CanvasView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        UpdateBrushCursor();
    }

    public EditorSession? Session => _session;

    /// <summary>
    /// 換編輯會話。生命週期由呼叫端（MainWindow 的分頁）管理 —— 這裡不 Dispose，
    /// 同一個 session 可能只是暫時切到背景分頁。
    /// <paramref name="viewport"/> 給了就還原（分頁記住各自的縮放/位置），沒給則下一幀重新 fit。
    /// </summary>
    public void SetSession(EditorSession session, ViewportTransform? viewport = null)
    {
        _session = session;
        if (viewport is { } vp)
        {
            _viewport = vp;
            _targetViewport = vp;
            _viewportInitialized = true;
            _autoFit = null; // 分頁還原的視口是使用者的，不再自動置中
            ViewportChanged?.Invoke();
        }
        else
        {
            _viewportInitialized = false; // 下一幀重新 fit
        }
        StateChanged?.Invoke();
    }

    /// <summary>目前視口（分頁切換時保存用；尚未初始化回傳 null）。</summary>
    public ViewportTransform? SaveViewport() => _viewportInitialized ? _viewport : null;

    /// <summary>清空會話（最後一個分頁關掉後的零文件狀態）：畫布不再渲染任何東西。</summary>
    public void ClearSession()
    {
        _session = null;
        _viewportInitialized = false;
        _autoFit = null;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartAnimationLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animationRunning = false;
    }

    // ---- 方向鍵微調：按一下走一格，按住則由動畫迴圈等速滑行 ----
    //
    // 節奏本身在 Core 的 NudgeGlide（可單元測試）；這裡只負責把按鍵與幀時間餵進去、
    // 把它吐出的整數位移交給 MoveTool.Nudge。所有微調目標都適用（浮動內容／變形框／
    // 選取的像素／文字物件／整個圖層）；會壓 undo 的那幾條在滑行結束時併回一步。

    private readonly Core.Tools.NudgeGlide _glide = new();

    /// <summary>滑行期間壓進歷史的步數起算點（放開時併回一步）。</summary>
    private int _nudgeUndoBase = -1;

    private static (int X, int Y) NudgeDirection(Key key) => key switch
    {
        Key.Left => (-1, 0),
        Key.Right => (1, 0),
        Key.Up => (0, -1),
        _ => (0, 1),
    };

    /// <summary>方向鍵按下：第一次按記一格，之後的 OS 重複事件交給滑行處理。</summary>
    private void BeginNudge(EditorSession session, Key key, bool shift)
    {
        if (!Core.Tools.MoveTool.HasNudgeTarget(session)) return;
        var (dirX, dirY) = NudgeDirection(key);
        _glide.Shift = shift;
        if (!_glide.Press(dirX, dirY, shift ? 10 : 1)) return; // 按鍵重複：滑行已經在動了
        if (_nudgeUndoBase < 0) _nudgeUndoBase = session.History.UndoStack.Count;
    }

    private void EndNudge(Key key)
    {
        var (dirX, dirY) = NudgeDirection(key);
        _glide.Release(dirX, dirY);
    }

    /// <summary>一段微調結束：滑行期間每幀壓的那些步併回一步，Ctrl+Z 一次回到起點。</summary>
    private void FinishNudge(EditorSession? session)
    {
        if (_nudgeUndoBase >= 0 && session != null)
        {
            var added = session.History.UndoStack.Count - _nudgeUndoBase;
            if (added > 1) session.History.CollapseLast(added);
        }
        _nudgeUndoBase = -1;
        _glide.Reset();
    }

    /// <summary>畫布不再是焦點／目標消失：滑行停掉並收尾。</summary>
    private void CancelNudge() => FinishNudge(_session);

    /// <summary>這一幀的微調。</summary>
    private void StepNudgeAnimation(double dt)
    {
        if (_glide.IsIdle && !_glide.AnyHeld && _nudgeUndoBase < 0) return; // 沒在微調

        var session = _session;
        // 中途落地／取消（Enter、Esc、切工具）：剩下的位移就此作廢，不要事後補跳一段
        if (session == null || !Core.Tools.MoveTool.HasNudgeTarget(session))
        {
            CancelNudge();
            return;
        }

        var (dx, dy) = _glide.Step(dt);
        if ((dx != 0 || dy != 0) && Core.Tools.MoveTool.Nudge(session, dx, dy)) StateChanged?.Invoke();

        // 都放開、殘餘也送完了 → 收尾（把這一段的歷史併回一步）
        if (!_glide.AnyHeld && _glide.IsIdle) FinishNudge(session);
    }

    private TimeSpan _lastFrameTime;

    private void StartAnimationLoop()
    {
        if (_animationRunning) return;
        _animationRunning = true;

        void Frame(TimeSpan now)
        {
            if (!_animationRunning) return;
            // 幀間隔（掉幀／切到背景時夾住上限，免得一幀滑過頭）
            var dt = _lastFrameTime == TimeSpan.Zero
                ? 1 / 60.0
                : Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.1);
            _lastFrameTime = now;
            _session?.CollectOverlayGhost(); // 落地後的殘影：合成器追上就收掉
            StepViewportAnimation();
            StepNudgeAnimation(dt);
            UpdateBrushCursor();
            FrameTick?.Invoke();
            InvalidateVisual();
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
        }

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }

    public override void Render(DrawingContext context)
    {
        var session = _session;
        if (session == null)
        {
            // 零文件狀態：只鋪外圍底色，不渲染任何文件內容
            var c = AppTheme.CanvasSurround;
            context.FillRectangle(
                new SolidColorBrush(Avalonia.Media.Color.FromRgb(c.Red, c.Green, c.Blue)),
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            return;
        }
        var doc = session.Document;

        if (!_viewportInitialized && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var fit = ViewportTransform.Fit(doc.Width, doc.Height, Bounds.Width, Bounds.Height);
            _viewport = fit;
            _targetViewport = fit;
            _viewportInitialized = true;
            _autoFit = fit;
            _autoFitSize = Bounds.Size;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewportChanged?.Invoke());
        }
        else if (_autoFit is { } autoFit && _viewport == autoFit && _targetViewport == autoFit &&
                 Bounds.Size != _autoFitSize && Bounds.Width > 0 && Bounds.Height > 0)
        {
            // 控制項大小變了（視窗最大化／拉大），視口還停在舊的 fit 上：重新置中，不走動畫
            var fit = ViewportTransform.Fit(doc.Width, doc.Height, Bounds.Width, Bounds.Height);
            _viewport = fit;
            _targetViewport = fit;
            _autoFit = fit;
            _autoFitSize = Bounds.Size;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewportChanged?.Invoke());
        }

        context.Custom(new CanvasDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), session, _viewport, _stats, ShowPixelGrid,
            (float)CurrentContentFade, SmoothZoom));

        DrawBrushCursor(context);
    }

    // ---- 文件內容 fade（分頁切換動畫）----
    // 不走 Visual.Opacity：Opacity=0 時 Avalonia 會剔除整個子樹（畫面閃黑），
    // 而且 lease 未必看得到祖先的動畫值。畫布本來就連續重繪，時間插值每幀取值即可。

    private double _fadeFrom = 1;
    private double _fadeTo = 1;
    private long _fadeStartMs;
    private double _fadeDurationMs;

    /// <summary>從目前值開始把文件內容透明度動畫到 <paramref name="to"/>。</summary>
    public void BeginContentFade(double to, double durationMs)
    {
        _fadeFrom = CurrentContentFade;
        _fadeTo = to;
        _fadeStartMs = Environment.TickCount64;
        _fadeDurationMs = durationMs;
    }

    /// <summary>不經動畫直接設定文件內容透明度。</summary>
    public void SnapContentFade(double value)
    {
        _fadeFrom = value;
        _fadeTo = value;
        _fadeDurationMs = 0;
    }

    /// <summary>目前的文件內容透明度（CubicEaseOut 插值）。</summary>
    public double CurrentContentFade
    {
        get
        {
            if (_fadeDurationMs <= 0) return _fadeTo;
            var t = (Environment.TickCount64 - _fadeStartMs) / _fadeDurationMs;
            if (t >= 1) return _fadeTo;
            if (t < 0) return _fadeFrom;
            var eased = 1 - Math.Pow(1 - t, 3); // CubicEaseOut
            return _fadeFrom + (_fadeTo - _fadeFrom) * eased;
        }
    }

    // ---- 縮放 ----

    /// <summary>滾輪平移一格走多少「檢視像素」（與縮放無關，手感固定）。</summary>
    private const double WheelPanStep = 60;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // 垂直滾輪為主；橫向滾輪（傾斜輪/觸控板）沒有 Y 時退而取 X
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;

        // 平移改的也是目標值，跟縮放共用同一套插值，滾起來一樣是連續的。
        // 往上滾 = 內容往下/往右走（跟捲軸同向）。
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var factor = Math.Pow(1.18, delta);
            _targetViewport = _targetViewport.ZoomAt(e.GetPosition(this), factor);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _targetViewport = _targetViewport.PanBy(delta * WheelPanStep, 0);
        }
        else
        {
            _targetViewport = _targetViewport.PanBy(0, delta * WheelPanStep);
        }
        e.Handled = true;
    }

    /// <summary>每幀把顯示中的視圖指數插值逼近目標值（時間常數約 70ms）。</summary>
    private void StepViewportAnimation()
    {
        var now = _animClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastAnimSeconds, 0, 0.1);
        _lastAnimSeconds = now;

        var dScale = _targetViewport.Scale - _viewport.Scale;
        var dx = _targetViewport.OffsetX - _viewport.OffsetX;
        var dy = _targetViewport.OffsetY - _viewport.OffsetY;

        // 已經夠接近就直接吸附，避免無止盡的微小更新
        if (Math.Abs(dScale) < _viewport.Scale * 0.0005 && Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.05)
        {
            if (dScale != 0 || dx != 0 || dy != 0)
            {
                _viewport = _targetViewport;
                ViewportChanged?.Invoke();
            }
            return;
        }

        var t = 1 - Math.Exp(-dt / 0.07);
        _viewport = new ViewportTransform(
            _viewport.Scale + dScale * t,
            _viewport.OffsetX + dx * t,
            _viewport.OffsetY + dy * t);
        ViewportChanged?.Invoke();
    }

    /// <summary>直接設定視圖（平移等需要跟手的操作用，不走動畫）。</summary>
    private void SetViewportImmediate(ViewportTransform viewport)
    {
        _viewport = viewport;
        _targetViewport = viewport;
        ViewportChanged?.Invoke();
    }

    // ---- Pointer → 工具 ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _currentModifiers = e.KeyModifiers;
        _currentClickCount = e.ClickCount; // move/up 沿用，工具才能判斷單擊或雙擊
        if (_session != null)
        {
            // 讓工具能以螢幕距離判定「算不算拖曳」，手感不受縮放影響
            _session.Move.ViewScale = _viewport.Scale;
            _session.Move.HandleTolerance = (float)(9 / Math.Max(0.01, _viewport.Scale));
            _session.SnapTolerance = (float)(8 / Math.Max(0.01, _viewport.Scale)); // 對齊吸附 ≈ 螢幕 8px
        }
        var point = e.GetCurrentPoint(this);
        _lastPointerView = point.Position;

        if (point.Properties.IsMiddleButtonPressed || (_spaceDown && point.Properties.IsLeftButtonPressed))
        {
            _panning = true;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (point.Properties.IsRightButtonPressed && _session != null &&
                 (_session.ActiveTool == _session.Move || _session.ActiveTool == _session.Text))
        {
            var rotatePos = ToToolEvent(point).DocPosition;
            // 右鍵拖曳＝旋轉。選著文字物件 → 轉物件本身（文字/移動工具皆可）；
            // 否則移動工具下旋轉變形框（paint.net 式；需要時自動開始變形 session）
            _elementRotating = _elementRotate.TryBeginRotate(_session, rotatePos);
            if (!_elementRotating && _session.ActiveTool == _session.Move)
                _transformRotating = _session.Move.BeginRotate(_session, rotatePos);
            if (_elementRotating || _transformRotating) StateChanged?.Invoke();
        }
        else if (point.Properties.IsLeftButtonPressed && _session != null)
        {
            var docPoint = ToToolEvent(point).DocPosition;

            // 雙擊文字 → 畫布內編輯。只在矩形選取／移動／文字工具下（使用者明示）——
            // 筆刷、橡皮擦這類繪畫工具連點兩下是在畫東西，不該跳去編輯文字
            var editTools = _session.ActiveTool == _session.RectSelect ||
                            _session.ActiveTool == _session.Move ||
                            _session.ActiveTool == _session.Text;
            if (e.ClickCount == 2 && editTools)
            {
                (Core.Layers.RasterLayer, Core.Vectors.TextElement)? hit;
                lock (_session.Document.SyncRoot)
                {
                    hit = Core.Tools.VectorHitTest.FindTextAt(_session.Document, docPoint);
                }
                if (hit is { } h)
                {
                    _session.SelectedElement = (h.Item1.Id, h.Item2.Id);
                    TextEditRequested?.Invoke(h.Item1, h.Item2, false);
                    StateChanged?.Invoke();
                    e.Handled = true;
                    return;
                }
            }

            // 選取框把手（選取範圍／浮動內容／文字物件都是同一套）：
            // 選取類與移動工具下可直接拉大小；繪畫類工具不攔截，免得干擾落筆。
            var handleTools = _session.ActiveTool == _session.RectSelect ||
                              _session.ActiveTool == _session.Lasso ||
                              _session.ActiveTool == _session.Wand;
            if (handleTools && _handles.TryBegin(_session, docPoint,
                    tolerance: (float)(9 / Math.Max(0.01, _viewport.Scale))))
            {
                _handleDragging = true;
            }
            else
            {
                _toolActive = true;
                _session.ActiveTool.OnPointerDown(ToToolEvent(point), _session);
            }
            StateChanged?.Invoke();
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private readonly Core.Tools.HandleDragController _handles = new();
    private readonly Core.Tools.ElementDragHelper _elementRotate = new(); // 右鍵旋轉文字物件
    private bool _handleDragging;
    private bool _transformRotating;
    private bool _elementRotating;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _currentModifiers = e.KeyModifiers;
        var pos = e.GetPosition(this);
        _hoverView = pos;
        _pointerInside = true;

        {
            var doc = _viewport.ViewToDoc(pos);
            PointerDocMoved?.Invoke(new SKPoint((float)doc.X, (float)doc.Y));
        }

        if (_panning)
        {
            // 平移要完全跟手，不走插值
            SetViewportImmediate(_viewport.PanBy(pos.X - _lastPointerView.X, pos.Y - _lastPointerView.Y));
        }
        else if (_elementRotating && _session != null)
        {
            var doc = _viewport.ViewToDoc(pos);
            _elementRotate.ContinueRotate(_session,
                new SKPoint((float)doc.X, (float)doc.Y), ToModifiers(_currentModifiers));
            StateChanged?.Invoke();
        }
        else if (_transformRotating && _session != null)
        {
            var doc = _viewport.ViewToDoc(pos);
            _session.Move.ContinueRotate(_session,
                new SKPoint((float)doc.X, (float)doc.Y), ToModifiers(_currentModifiers));
            StateChanged?.Invoke();
        }
        else if (_handleDragging && _session != null)
        {
            var doc = _viewport.ViewToDoc(pos);
            _handles.Continue(_session, new SKPoint((float)doc.X, (float)doc.Y), ToModifiers(_currentModifiers));
            StateChanged?.Invoke();
        }
        else if (_toolActive && _session != null)
        {
            // 取回被合併的高頻採樣點，確保快速移動不掉點
            foreach (var p in e.GetIntermediatePoints(this))
                _session.ActiveTool.OnPointerMove(ToToolEvent(p), _session);
            StateChanged?.Invoke();
        }

        _lastPointerView = pos;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _currentModifiers = e.KeyModifiers;
        if (_elementRotating && _session != null)
        {
            _elementRotating = false;
            _elementRotate.End(_session); // 一步「旋轉文字」undo
            StateChanged?.Invoke();
        }
        else if (_transformRotating && _session != null)
        {
            _transformRotating = false;
            _session.Move.EndRotate(_session);
            StateChanged?.Invoke();
        }
        else if (_handleDragging && _session != null)
        {
            _handleDragging = false;
            _handles.End(_session);
            StateChanged?.Invoke();
        }
        else if (_toolActive && _session != null)
        {
            _toolActive = false;
            _session.ActiveTool.OnPointerUp(ToToolEvent(e.GetCurrentPoint(this)), _session);

            // 文字工具剛建立元素 → 直接進入畫布內編輯
            if (_session.PendingTextEdit is { } pending)
            {
                _session.PendingTextEdit = null;
                if (_session.Document.FindLayer(pending.LayerId) is Core.Layers.RasterLayer vlayer &&
                    vlayer.FindElement(pending.ElementId) is Core.Vectors.TextElement text)
                {
                    TextEditRequested?.Invoke(vlayer, text, true);
                }
            }
            StateChanged?.Invoke();
        }
        _panning = false;
        _brushCursorShown = false; // 平移把游標換成 SizeAll 了，強制重新決定一次
        Cursor = new Cursor(StandardCursorType.Cross);
        UpdateBrushCursor();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hoverView = e.GetPosition(this);
        _pointerInside = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
    }

    /// <summary>畫筆型工具且圈夠大時藏掉系統游標，只留我們畫的圈。</summary>
    private void UpdateBrushCursor()
    {
        if (_panning) return; // 平移中的 SizeAll 不要被蓋掉
        var wanted = BrushCursorRadius() != null;
        if (wanted == _brushCursorShown) return;
        _brushCursorShown = wanted;
        Cursor = new Cursor(wanted ? StandardCursorType.None : StandardCursorType.Cross);
    }

    /// <summary>目前該畫的圈的螢幕半徑；不該畫時回 null。</summary>
    private double? BrushCursorRadius()
    {
        if (_session?.ActiveTool is not IBrushCursorTool tool) return null;
        var radius = tool.CursorRadius * _viewport.Scale;
        return radius >= MinBrushCursorRadius ? radius : null;
    }

    private void DrawBrushCursor(DrawingContext context)
    {
        if (_panning || !_pointerInside) return;
        if (BrushCursorRadius() is not { } radius) return;
        context.DrawEllipse(null, BrushCursorPenDark, _hoverView, radius, radius);
        context.DrawEllipse(null, BrushCursorPenLight, _hoverView, radius, radius);
    }

    private ToolPointerEvent ToToolEvent(PointerPoint point)
    {
        var doc = _viewport.ViewToDoc(point.Position);
        var pressure = point.Properties.Pressure;
        return new ToolPointerEvent(
            new SKPoint((float)doc.X, (float)doc.Y),
            pressure <= 0 ? 1f : pressure,
            ToModifiers(_currentModifiers),
            _currentClickCount,
            (float)_viewport.Scale);
    }

    private KeyModifiers _currentModifiers;
    private int _currentClickCount = 1;

    private static ToolModifiers ToModifiers(KeyModifiers m)
    {
        var result = ToolModifiers.None;
        if (m.HasFlag(KeyModifiers.Shift)) result |= ToolModifiers.Shift;
        if (m.HasFlag(KeyModifiers.Control)) result |= ToolModifiers.Ctrl;
        if (m.HasFlag(KeyModifiers.Alt)) result |= ToolModifiers.Alt;
        return result;
    }

    // ---- 鍵盤 ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Shift 隨時反映：按住方向鍵之後才按 Shift 也要跟著加速
        if (e.Key is Key.LeftShift or Key.RightShift) _glide.Shift = true;
        var session = _session;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // 復原/重做/取消選取查快捷鍵表（可自訂）——與 MainWindow 是同一張表，改一處兩邊同步。
        // Ctrl+Shift+Z 是重做的固定別名（與 paint.net 一致），不參與自訂。
        if (session != null)
        {
            if (Services.ShortcutMap.Matches("edit.undo", e.Key, e.KeyModifiers))
            {
                session.Undo();
                StateChanged?.Invoke();
                e.Handled = true;
                return;
            }
            if (Services.ShortcutMap.Matches("edit.redo", e.Key, e.KeyModifiers) ||
                (e.Key == Key.Z && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
            {
                session.Redo();
                StateChanged?.Invoke();
                e.Handled = true;
                return;
            }
            if (Services.ShortcutMap.Matches("edit.deselect", e.Key, e.KeyModifiers))
            {
                if (session.Selection != null)
                {
                    session.CommitFloating();
                    Core.Tools.SelectionCommands.SetSelection(session, null, "取消選取");
                }
                StateChanged?.Invoke();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;
            case Key.Delete when session?.SelectedElement is { } sel:
                if (session.Document.FindLayer(sel.LayerId) is Core.Layers.RasterLayer vlayer &&
                    vlayer.FindElement(sel.ElementId) is { } element)
                {
                    Core.History.VectorCommands.RemoveElement(session.Document, session.History, vlayer, element);
                    session.SelectedElement = null;
                    StateChanged?.Invoke();
                }
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down
                when session != null && !ctrl &&
                     (session.ActiveTool == session.Move || session.SelectedElement != null):
            {
                // 方向鍵微調：1px；Shift = 10px（Photoshop／paint.net 的慣例）。
                // 移動工具下依序動變形框 → 浮動內容 → 選中的文字物件 → 整個圖層／群組。
                // 按住不放時忽略 OS 的按鍵重複，改由動畫迴圈等速滑行（見 NudgeGlide）
                _glide.Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                BeginNudge(session, e.Key, _glide.Shift);
                e.Handled = true;
                break;
            }
            case Key.D0 or Key.NumPad0 when session != null && !ctrl:
                ZoomToFit();
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1 when !ctrl:
                // 走動畫，跟滾輪縮放一致的手感
                _targetViewport = _targetViewport.WithScaleAroundCenter(1.0, Bounds.Width, Bounds.Height);
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftShift or Key.RightShift) _glide.Shift = false;
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            e.Handled = true;
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) EndNudge(e.Key);
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        CancelNudge(); // 焦點跑掉就收不到 KeyUp，會一直滑下去
    }
}
