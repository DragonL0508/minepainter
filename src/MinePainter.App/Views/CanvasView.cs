using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MinePainter.App.Rendering;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 畫布控制項：滾輪（縮放／Shift 左右平移／按住 Caps Lock 上下平移）、中鍵、空白鍵 → viewport；
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
    private bool _spaceDown;
    private bool _panning;
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

    /// <summary>是否顯示像素格線（放大 500% 以上才實際繪製）。</summary>
    public bool ShowPixelGrid { get; set; }

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

    private void StartAnimationLoop()
    {
        if (_animationRunning) return;
        _animationRunning = true;

        void Frame(TimeSpan _)
        {
            if (!_animationRunning) return;
            _session?.CollectOverlayGhost(); // 落地後的殘影：合成器追上就收掉
            StepViewportAnimation();
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewportChanged?.Invoke());
        }

        context.Custom(new CanvasDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), session, _viewport, _stats, ShowPixelGrid,
            (float)CurrentContentFade));
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
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _targetViewport = _targetViewport.PanBy(delta * WheelPanStep, 0);
        }
        else if (Platform.KeyState.IsCapsLockHeld)
        {
            _targetViewport = _targetViewport.PanBy(0, delta * WheelPanStep);
        }
        else
        {
            var factor = Math.Pow(1.18, delta);
            _targetViewport = _targetViewport.ZoomAt(e.GetPosition(this), factor);
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
        Cursor = new Cursor(StandardCursorType.Cross);
        e.Pointer.Capture(null);
    }

    private ToolPointerEvent ToToolEvent(PointerPoint point)
    {
        var doc = _viewport.ViewToDoc(point.Position);
        var pressure = point.Properties.Pressure;
        return new ToolPointerEvent(
            new SKPoint((float)doc.X, (float)doc.Y),
            pressure <= 0 ? 1f : pressure,
            ToModifiers(_currentModifiers),
            _currentClickCount);
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
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            e.Handled = true;
        }
    }
}
