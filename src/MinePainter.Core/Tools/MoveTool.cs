using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 移動工具（paint.net 式，一律作用於「作用中圖層」）：
/// 　1. 有選取範圍 → 提起該範圍的像素成為浮動內容，可移動、拖四角縮放（Shift 等比）。
/// 　　 只要選取範圍還在，任何拖曳都是在動「選取的東西」，游標在不在框內都一樣
/// 　　 （paint.net 的「移動選取的像素」／Pinta MoveSelectedTool 也是不做命中測試）；
/// 　　 要移動圖層或物件得先取消選取（在範圍外點一下）。
/// 　2. 點中本圖層的文字物件 → 移動／縮放該物件
/// 　3. 其他 → 平移整個圖層；此時把手框顯示在「圖層實際內容」上（可超出畫布），
/// 　　 拖角＝提起整個內容縮放 —— 這是把畫布外像素整批抓回來的入口（GIMP 縮放圖層的對應）。
/// 　4. 作用中是群組 → 平移整個群組：所有子孫點陣圖層的像素（Offset）與文字物件一起動，
/// 　　 整趟一步 undo。整層平移時本層的文字物件也跟著走（像素與文字不拆散）。
/// </summary>
/// <summary>
/// 變形框的模式（工具列「變形」群組；Photoshop 編輯 → 變形 的對應）：
/// Free＝拖角縮放＋右鍵旋轉；Perspective＝透視（拖一角、同邊鄰角對稱跟著動；Ctrl＝該角自由拖）；
/// Warp＝扭曲／彎曲（4×4 貝茲網格，16 個控制點自由拖）。透視走 TransformSession 的四角模式、扭曲走彎曲模式。
/// </summary>
public enum TransformMode
{
    Free,
    Perspective,
    Warp,
}

public sealed class MoveTool : ITool
{
    public string Name => "移動";

    /// <summary>拖角時採用的變形模式（工具列設定；每份文件各自的工具實例，由 UI 推進來）。</summary>
    public TransformMode TransformMode { get; set; } = TransformMode.Free;

    /// <summary>算不算「拖曳」的門檻（螢幕像素）；沒超過就當成點一下。</summary>
    private const double DragThreshold = 2;

    private enum Mode
    {
        None,
        Layer,
        /// <summary>在選取範圍外按下：先不提起，等真的拖曳了才提。</summary>
        PendingLift,
        FloatingMove,
        Handles,
        /// <summary>拖曳整個變形框（session 進行中）。</summary>
        TransformMove,
    }

    private readonly ElementDragHelper _elementDrag = new();
    private readonly HandleDragController _handles = new();
    private Mode _mode;

    // 圖層／群組平移（群組 = 底下所有點陣圖層一起動，像素與文字物件都跟著走）
    private RasterLayer? _layer; // 單一圖層時非 null（可走覆疊快路徑）
    private readonly List<RasterLayer> _moveLayers = new();
    private readonly List<SKPointI> _startOffsets = new();
    private readonly List<VectorElement[]> _startElements = new();
    private SKPointI _lastMoveDelta;
    private bool _movingGroup;
    private bool _layerDetachTried;

    // 浮動內容
    private SKPoint _dragStart;
    private SKRect _startRect;
    private SKPoint[]? _startQuad; // 變形框四角模式的平移起點
    private WarpMesh? _startWarp;  // 彎曲模式的平移起點

    // 「點空白處取消選取」的判定
    private SKPoint _pressPoint;
    private bool _pressedOutsideSelection;

    /// <summary>目前檢視縮放（UI 設定）；用來把「算不算拖曳」的門檻換算成螢幕距離。</summary>
    public double ViewScale { get; set; } = 1.0;

    /// <summary>把手命中容差（doc 像素）；UI 依縮放調整。</summary>
    public float HandleTolerance { get; set; } = 10f;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        var doc = session.Document;

        // 1) 選取框把手（選取範圍／浮動內容／文字物件共用同一套）
        if (_handles.TryBegin(session, e.DocPosition, HandleTolerance, e.Modifiers))
        {
            _mode = Mode.Handles;
            return;
        }

        _pressPoint = e.DocPosition;
        _pressedOutsideSelection = false;

        // 1.5) 變形 session 進行中：拖曳＝移動整個變形框；框外點一下（沒拖）＝落地
        if (session.Transform is { } transform)
        {
            _mode = Mode.TransformMove;
            _dragStart = e.DocPosition;
            _startRect = transform.FrameRect;
            _startQuad = transform.Quad; // 四角模式：平移的是四角（immutable 實例，直接留著當起點）
            _startWarp = transform.Warp;
            // 變形中的圖層自己不能當參考（框還在原位，會被原地吸住）
            session.BeginSnapDrag(transform.Target.Id);
            _layerDetachTried = false; // 單層 session 的平移可走拖曳覆疊快路徑
            _pressedOutsideSelection = !TransformContains(transform, e.DocPosition);
            return;
        }

        // 2) 已浮動 → 移動它。從浮動內容外面按下也一樣：拖曳仍然是在動浮動內容，
        //    只有「沒拖曳就放開」才當成點空白處（落地並取消選取，見 OnPointerUp）。
        if (session.Floating is { } floating)
        {
            _mode = Mode.FloatingMove;
            _dragStart = e.DocPosition;
            _startRect = floating.TargetRect;
            session.BeginSnapDrag();
            _pressedOutsideSelection = !floating.TargetRect.Contains(e.DocPosition.X, e.DocPosition.Y);
            return;
        }

        // 3) 有選取範圍 → 這一下一定是在動選取的內容，不會碰到圖層或物件。
        //    範圍內按下就提起；範圍外先不提起（否則單純「點一下取消選取」也會白挖一次像素），
        //    等真的拖曳過門檻才提，見 OnPointerMove。
        //    文字圖層例外：它的內容是文字物件不是像素，提起只會挖到一塊空白 ——
        //    看起來就是「怎麼拖都沒東西跟著動」。改走下面的物件／整層平移（選取框跟著走）。
        if (session.Selection is { IsEmpty: false } selection &&
            doc.ActiveLayer is not RasterLayer { IsTextLayer: true })
        {
            _dragStart = e.DocPosition;
            session.BeginSnapDrag();
            if (selection.CoverageAt((int)e.DocPosition.X, (int)e.DocPosition.Y) > 0)
            {
                var lifted = session.LiftSelection();
                if (lifted != null)
                {
                    _mode = Mode.FloatingMove;
                    _startRect = lifted.TargetRect;
                    return;
                }
            }
            else
            {
                // 點在選取範圍外：若接下來沒有拖曳，放開時就取消選取
                _pressedOutsideSelection = true;
            }

            _mode = Mode.PendingLift;
            return;
        }

        // 3) 本圖層的物件（把手/內部）
        if (_elementDrag.TryBegin(session, e.DocPosition, HandleTolerance, allowInsideMove: true))
            return;

        lock (doc.SyncRoot)
        {
            if (VectorHitTest.FindTextAt(doc, e.DocPosition) is { } hit)
            {
                _elementDrag.BeginMoveLocked(session, hit.Layer, hit.Element, e.DocPosition);
                return;
            }
        }

        // 4) 平移整個圖層／群組（群組 = 所有子孫點陣圖層的像素與文字物件一起動；單層只動像素）
        session.SelectedElement = null;

        // 圖層內容框的出現／消失：點到這層真的有東西的地方＝框回來（可拖角縮放）；
        // 點在空白處＝沒拖曳的話放開時就把框收掉（見 OnPointerUp）。
        // 判準是實際像素而不是內容的外接矩形 —— L 形或散落的圖層，外接矩形裡大半是空的，
        // 用矩形判會變成「明明是空白卻清不掉框」。
        // 拖曳照舊平移整層，只有「點一下沒拖」才算點空白處。
        bool onContent;
        lock (doc.SyncRoot)
        {
            onContent = doc.ActiveLayer is { } node &&
                        HandleDragController.HitsContent(node, e.DocPosition, HandleTolerance / 2f);
        }
        if (onContent) session.LayerFrameDismissed = false;
        else _pressedOutsideSelection = true;

        _moveLayers.Clear();
        _startOffsets.Clear();
        _startElements.Clear();
        _layer = null;
        _movingGroup = false;
        switch (doc.ActiveLayer)
        {
            case RasterLayer single:
                _moveLayers.Add(single);
                _layer = single; // 單一圖層才有覆疊快路徑
                break;
            case GroupLayer group:
                CollectRasterLayers(group, _moveLayers);
                _movingGroup = true;
                break;
        }
        if (_moveLayers.Count == 0) return;

        lock (doc.SyncRoot)
        {
            foreach (var l in _moveLayers)
            {
                _startOffsets.Add(l.Offset);
                // 群組＝「所有東西一起動」（像素＋各層文字）。單一圖層：文字圖層的內容就是文字，
                // 整層拖曳自然要帶著走 —— 走覆疊時文字已渲染進覆疊快照，拖曳中不逐步 ReplaceElement
                // （那會每步重合成、和覆疊步調不同看起來一直閃），放開才一次搬到定位。
                _startElements.Add((_movingGroup || l.IsTextLayer) && l.HasElements
                    ? l.Elements.ToArray()
                    : Array.Empty<VectorElement>());
            }
        }
        session.RefreshSelectionHandles(); // 確保拿到的是當下的框（不靠先前狀態）
        // 對齊模式吸附的起始框＝畫面上那個把手框（ExactContentBounds 推導）。
        // 不能用 LayerNode.ContentBounds —— 那是 tile 對齊（256 倍數）的保守外擴，
        // 吸附會對到看不見的 tile 邊界，整個對不齊。
        _startRect = session.SelectionHandles ?? SKRect.Empty;
        // 被拖的圖層（群組時是全部子層）不算參考框
        session.BeginSnapDrag(_moveLayers.Select(l => l.Id).ToArray());
        _dragStart = e.DocPosition;
        _lastMoveDelta = SKPointI.Empty;
        _layerDetachTried = false;
        _mode = Mode.Layer;
    }

    private static void CollectRasterLayers(GroupLayer group, List<RasterLayer> into)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case RasterLayer r: into.Add(r); break;
                case GroupLayer g: CollectRasterLayers(g, into); break;
            }
        }
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (_mode == Mode.Handles)
        {
            _handles.Continue(session, e.DocPosition, e.Modifiers);
            return;
        }

        if (_elementDrag.IsActive)
        {
            _elementDrag.Continue(session, e.DocPosition, e.Modifiers);
            return;
        }

        // 在選取範圍外按下後真的拖了 → 現在才提起，之後照浮動內容處理
        if (_mode == Mode.PendingLift)
        {
            if (SKPoint.Distance(e.DocPosition, _pressPoint) * ViewScale <= DragThreshold) return;

            var lifted = session.LiftSelection();
            if (lifted == null)
            {
                _mode = Mode.None;
                return;
            }
            _mode = Mode.FloatingMove;
            _startRect = lifted.TargetRect;
        }

        var floating = session.Floating;
        switch (_mode)
        {
            case Mode.FloatingMove when floating != null:
            {
                var before = floating.TargetBounds;
                // 位移取整到整數像素：子像素平移會引入重取樣模糊（Pinta 也是這樣做）
                var dx = MathF.Floor(e.DocPosition.X - _dragStart.X);
                var dy = MathF.Floor(e.DocPosition.Y - _dragStart.Y);
                (dx, dy) = CanvasSnap.Adjust(session, _startRect, dx, dy); // 對齊模式：吸附畫布邊/中線
                var moved = new SKRect(
                    _startRect.Left + dx, _startRect.Top + dy,
                    _startRect.Right + dx, _startRect.Bottom + dy);
                if (moved == floating.TargetRect) return; // 同一格像素內的抖動：沒有畫面變化
                floating.TargetRect = moved;
                InvalidateFloating(session, floating, before);
                break;
            }

            case Mode.TransformMove when session.Transform is { } transform:
            {
                var dx = MathF.Floor(e.DocPosition.X - _dragStart.X);
                var dy = MathF.Floor(e.DocPosition.Y - _dragStart.Y);
                (dx, dy) = CanvasSnap.Adjust(session, _startRect, dx, dy); // 對齊模式：吸附畫布邊/中線
                var moved = new SKRect(
                    _startRect.Left + dx, _startRect.Top + dy,
                    _startRect.Right + dx, _startRect.Bottom + dy);
                if (moved == transform.FrameRect) return;

                // 單層 session：第一次真的動了才拆下來走覆疊（純平移期間 render thread 直接畫，
                // 一格都不重合成）；群組沒有快路徑，但純平移也只改 Offset、不重取樣。
                if (!_layerDetachTried && transform.SoleLayer is { } sole)
                {
                    _layerDetachTried = true;
                    session.BeginLayerDrag(sole);
                }

                if (transform.Warp != null && _startWarp != null)
                    transform.SetWarp(_startWarp.Translated(dx, dy)); // 彎曲模式：整張網格平移
                else if (transform.Quad != null && _startQuad != null)
                    transform.SetQuad(QuadGeometry.Translated(_startQuad, dx, dy)); // 四角模式：整體平移四角
                else
                    transform.TargetRect = moved;
                transform.Apply(preview: true, layer =>
                    session.LayerOverlay is { HandingOver: false } overlay && overlay.Layer == layer);
                session.RefreshSelectionHandles();
                break;
            }

            case Mode.Layer when _moveLayers.Count > 0:
            {
                var doc = session.Document;
                var rawDx = e.DocPosition.X - _dragStart.X;
                var rawDy = e.DocPosition.Y - _dragStart.Y;
                (rawDx, rawDy) = CanvasSnap.Adjust(session, _startRect, rawDx, rawDy); // 對齊模式
                var delta = new SKPointI((int)MathF.Round(rawDx), (int)MathF.Round(rawDy));
                if (delta == _lastMoveDelta) return; // 同一格像素內的抖動
                _lastMoveDelta = delta;

                // 第一次真的動了才把圖層從合成結果拆下來 —— 只是點一下的話不必付這個代價。
                // 拆下來之後拖曳期間一格都不用重合成（見 EditorSession.BeginLayerDrag）。
                // 群組（多圖層）沒有覆疊快路徑，直接走合成器（層序正確優先）。
                if (_layer != null && !_layerDetachTried)
                {
                    _layerDetachTried = true;
                    session.BeginLayerDrag(_layer);
                }

                for (var i = 0; i < _moveLayers.Count; i++)
                {
                    var layer = _moveLayers[i];
                    var newOffset = new SKPointI(
                        _startOffsets[i].X + delta.X, _startOffsets[i].Y + delta.Y);

                    if (newOffset != layer.Offset)
                    {
                        var overlaid = layer == _layer &&
                                       session.LayerOverlay is { HandingOver: false } overlay &&
                                       overlay.Layer == layer;
                        if (overlaid)
                        {
                            lock (doc.SyncRoot) layer.Offset = newOffset;
                        }
                        else
                        {
                            SKRectI dirty;
                            lock (doc.SyncRoot)
                            {
                                var before = layer.DisplayContentBounds;
                                layer.Offset = newOffset;
                                var after = layer.DisplayContentBounds;
                                dirty = before.IsEmpty ? after
                                    : after.IsEmpty ? before : SKRectI.Union(before, after);
                            }
                            // 純平移：效果快取是圖層座標、與 Offset 無關 —— 只重新合成，不重算效果
                            if (!dirty.IsEmpty) layer.InvalidateComposite(dirty);
                        }
                    }

                    // 文字物件跟著整層移動；一律從起始快照換算，避免逐步累積誤差。
                    // 覆疊中的單一圖層：文字已在覆疊快照裡跟著動，放開才落地（見 OnPointerUp）。
                    var startEls = _startElements[i];
                    var deferred = layer == _layer &&
                                   session.LayerOverlay is { HandingOver: false, IncludesElements: true } ov &&
                                   ov.Layer == layer;
                    if (startEls.Length > 0 && !deferred)
                    {
                        lock (doc.SyncRoot)
                        {
                            foreach (var el in startEls)
                            {
                                if (layer.FindElement(el.Id) != null)
                                    layer.ReplaceElement(el.Translated(delta.X, delta.Y));
                            }
                        }
                    }
                }
                session.RefreshSelectionHandles(); // 圖層內容框跟著 offset 走（內容快取沒失效，O(1)）
                break;
            }
        }
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        session.EndSnapDrag(); // 導線只在拖曳中顯示；參考框快取跟著這趟拖曳結束
        if (_mode == Mode.Handles)
        {
            _handles.End(session);
            _mode = Mode.None;
            return;
        }

        if (_elementDrag.IsActive)
        {
            _elementDrag.End(session);
            return;
        }

        // 點一下（沒拖曳）在框外 → 落地（變形框/浮動內容）並取消選取。
        // 畫面上任何一種框都在這裡一起收掉（含圖層內容框），點空白處才是真的「清乾淨」。
        var moved = SKPoint.Distance(e.DocPosition, _pressPoint) * ViewScale;
        if (_pressedOutsideSelection && moved <= DragThreshold)
        {
            _pressedOutsideSelection = false;
            _mode = Mode.None;
            _layer = null;
            session.CommitTransform(); // 變形框外點一下＝完成變形（paint.net 式）
            session.CommitFloating();  // 浮動中先落地（沒動過等同還原，不會多記一步歷史）
            if (session.Selection != null)
                SelectionCommands.SetSelection(session, null, "取消選取");
            session.SelectedElement = null;
            session.LayerFrameDismissed = true;
            return;
        }
        _pressedOutsideSelection = false;

        switch (_mode)
        {
            case Mode.TransformMove when session.Transform is { } transform:
                transform.Apply(preview: false); // 手勢結束補 High；history 等 session 落地一次記
                session.RefreshSelectionHandles();
                break;

            case Mode.FloatingMove when session.Floating is { } floating:
                // 留在浮動狀態（可繼續調整），只同步把手框
                session.RefreshSelectionHandles();
                if (floating.TargetRect.Width < 1 || floating.TargetRect.Height < 1)
                {
                    session.CancelFloating();
                    session.Notify("選取內容太小，已還原");
                }
                break;

            case Mode.Layer when _moveLayers.Count > 0 && _lastMoveDelta != SKPointI.Empty:
            {
                // 覆疊中延後的文字：現在一次搬到定位（覆疊層交還時合成器會把它們畫在新位置）
                if (_layer != null && _startElements.Count == 1 && _startElements[0].Length > 0 &&
                    session.LayerOverlay is { HandingOver: false, IncludesElements: true } ov && ov.Layer == _layer)
                {
                    lock (session.Document.SyncRoot)
                    {
                        foreach (var el in _startElements[0])
                        {
                            if (_layer.FindElement(el.Id) != null)
                                _layer.ReplaceElement(el.Translated(_lastMoveDelta.X, _lastMoveDelta.Y));
                        }
                    }
                }

                var layers = _moveLayers.ToArray();
                var oldOffsets = _startOffsets.ToArray();
                var oldElements = _startElements.ToArray();
                var newOffsets = layers.Select(l => l.Offset).ToArray();
                var delta = _lastMoveDelta;
                var label = _movingGroup ? "移動群組" : "移動圖層";

                // 選取框跟著搬走的內容走（放開才柵格化一次：拖曳中每一步重算遮罩太貴）。
                // 沒有選取時兩者都是 null，undo/redo 也就什麼都不做。
                var oldSelection = session.Selection;
                var newSelection = oldSelection is { IsEmpty: false } sel && delta != SKPointI.Empty
                    ? sel.TransformedTo(
                        SKRect.Create(sel.Bounds.Left + delta.X, sel.Bounds.Top + delta.Y,
                            sel.Bounds.Width, sel.Bounds.Height),
                        session.Document.Bounds) ?? oldSelection
                    : oldSelection;
                if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(newSelection);

                session.History.Push(new ActionHistoryEntry(label, session.Document.Bounds,
                    undo: _ =>
                    {
                        if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(oldSelection);
                        for (var i = 0; i < layers.Length; i++)
                        {
                            layers[i].Offset = oldOffsets[i];
                            foreach (var el in oldElements[i])
                            {
                                if (layers[i].FindElement(el.Id) != null)
                                    layers[i].ReplaceElement(el);
                            }
                            layers[i].InvalidateComposite(layers[i].Document?.Bounds ?? SKRectI.Empty); // 平移不重算效果
                        }
                    },
                    redo: _ =>
                    {
                        if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(newSelection);
                        for (var i = 0; i < layers.Length; i++)
                        {
                            layers[i].Offset = newOffsets[i];
                            foreach (var el in oldElements[i])
                            {
                                if (layers[i].FindElement(el.Id) != null)
                                    layers[i].ReplaceElement(el.Translated(delta.X, delta.Y));
                            }
                            layers[i].InvalidateComposite(layers[i].Document?.Bounds ?? SKRectI.Empty);
                        }
                    }));
                break;
            }
        }

        session.EndLayerDrag(); // 覆疊層交還給合成器（逐格接手，畫面不會閃）；沒拆下來時是 no-op
        _mode = Mode.None;
        _layer = null;
        _moveLayers.Clear();
        _startOffsets.Clear();
        _startElements.Clear();
    }

    /// <summary>
    /// 浮動內容動了：通知畫面。
    ///
    /// 走覆疊路徑時（<see cref="EditorSession.FloatingOverlay"/>）合成器根本沒在畫浮動內容，
    /// 一格都不必重新合成 —— 這是大片內容拖曳能跟手的關鍵。
    ///
    /// 退回合成器路徑時只標髒「舊位置 ∪ 新位置」，刻意不含
    /// <see cref="FloatingSelection.SourceBounds"/> —— 提起時挖出的洞在整趟拖曳中
    /// 都不會再變，每次移動都把它一起標髒等於白做一次；而且一旦拖遠，
    /// 「原位置 ∪ 新位置」的外接矩形會膨脹成大半張畫布，每個滑鼠事件都重合成一次。
    /// 洞的失效由 <see cref="EditorSession.LiftSelection"/> 在提起時做一次就夠。
    /// </summary>
    internal static void InvalidateFloating(EditorSession session, FloatingSelection floating, SKRectI before)
    {
        session.RefreshSelectionHandles(); // TargetRect 是 FloatingSelection 內部狀態，要手動觸發
        if (session.IsFloatingOverlaid) return;
        if (session.Document.FindLayer(floating.LayerId) is RasterLayer layer)
            layer.Invalidate(SKRectI.Union(before, floating.TargetBounds));
    }

    // ---- 方向鍵微調（移動工具；1px，Shift＝10px，Photoshop 慣例）----

    /// <summary>
    /// 把「目前框住的東西」平移 delta：變形框 → 浮動內容 → 選中的文字物件 → 整個圖層／群組。
    /// 變形框與浮動內容在 session 裡動、不記步（落地時一起記）；物件與圖層各記一步。
    /// 回傳 false＝沒有可微調的東西。
    /// </summary>
    /// <summary>
    /// 有沒有東西可以微調（變形框／浮動內容／可提起的選取／選中的物件／作用中的圖層）。
    /// UI 用它決定要不要啟動按住滑行。
    /// </summary>
    public static bool HasNudgeTarget(EditorSession session) =>
        session.Transform != null || session.Floating != null || CanLiftForNudge(session) ||
        session.SelectedElement != null ||
        session.Document.ActiveLayer is RasterLayer or GroupLayer;

    /// <summary>
    /// 這一步微調會不會壓一筆 undo（物件／圖層會，變形框與浮動內容不會）。
    /// 按住滑行時 UI 靠它決定放開後要把幾步併回一步。
    /// </summary>
    public static bool NudgePushesHistory(EditorSession session) =>
        session.Transform == null && session.Floating == null && !CanLiftForNudge(session);

    /// <summary>有選取範圍、作用中是一般點陣圖層、也沒有選中的物件 → 方向鍵該提起選取的像素。</summary>
    private static bool CanLiftForNudge(EditorSession session) =>
        session.SelectedElement == null &&
        session.Selection is { IsEmpty: false } &&
        session.Document.ActiveLayer is RasterLayer { IsTextLayer: false };

    public static bool Nudge(EditorSession session, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return false;
        var doc = session.Document;

        if (session.Transform is { } transform)
        {
            if (transform.Warp is { } warp) transform.SetWarp(warp.Translated(dx, dy));
            else if (transform.Quad is { } quad) transform.SetQuad(QuadGeometry.Translated(quad, dx, dy));
            else
            {
                var r = transform.TargetRect;
                transform.TargetRect = new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
            }
            transform.Apply(preview: false);
            session.RefreshSelectionHandles();
            return true;
        }

        // 有選取範圍時，方向鍵動的是「選取的像素」（與拖曳同一套語意）：
        // 第一次按就把它提起來變成浮動內容，之後都在浮動內容上挪。
        // 文字圖層例外（內容是物件不是像素，提起只會挖到空白），走下面的物件／整層路徑。
        var target = session.Floating ?? (CanLiftForNudge(session) ? session.LiftSelection() : null);
        if (target is { } floating)
        {
            var before = floating.TargetBounds;
            var r = floating.TargetRect;
            floating.TargetRect = new SKRect(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);
            InvalidateFloating(session, floating, before);
            return true;
        }

        if (session.SelectedElement is { } sel)
        {
            RasterLayer? layer;
            VectorElement? element;
            lock (doc.SyncRoot)
            {
                layer = doc.FindLayer(sel.LayerId) as RasterLayer;
                element = layer?.FindElement(sel.ElementId);
            }
            if (layer != null && element != null)
            {
                VectorCommands.ReplaceElement(doc, session.History, layer, element, element.Translated(dx, dy), "微調物件");
                session.RefreshSelectionHandles();
                return true;
            }
        }

        // 整個圖層／群組：Offset 與文字物件一起動（同 OnPointerUp 的 Layer 路徑，單一步 undo）
        var layers = new List<RasterLayer>();
        switch (doc.ActiveLayer)
        {
            case RasterLayer single: layers.Add(single); break;
            case GroupLayer group: CollectRasterLayers(group, layers); break;
        }
        if (layers.Count == 0) return false;

        var oldOffsets = layers.Select(l => l.Offset).ToArray();
        var newOffsets = oldOffsets.Select(o => new SKPointI(o.X + dx, o.Y + dy)).ToArray();
        VectorElement[][] oldElements;
        lock (doc.SyncRoot)
        {
            oldElements = layers.Select(l => l.HasElements ? l.Elements.ToArray() : Array.Empty<VectorElement>()).ToArray();
        }
        var oldSelection = session.Selection;
        var newSelection = oldSelection is { IsEmpty: false } s
            ? s.TransformedTo(SKRect.Create(s.Bounds.Left + dx, s.Bounds.Top + dy, s.Bounds.Width, s.Bounds.Height), doc.Bounds) ?? oldSelection
            : oldSelection;

        void Apply(SKPointI[] offsets, bool moved)
        {
            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                lock (doc.SyncRoot)
                {
                    layer.Offset = offsets[i];
                    foreach (var el in oldElements[i])
                    {
                        if (layer.FindElement(el.Id) != null)
                            layer.ReplaceElement(moved ? el.Translated(dx, dy) : el);
                    }
                }
                layer.InvalidateComposite(doc.Bounds); // 純平移：效果快取是圖層座標，不重算
            }
            session.RefreshSelectionHandles();
        }

        Apply(newOffsets, moved: true);
        if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(newSelection);
        var label = doc.ActiveLayer is GroupLayer ? "微調群組" : "微調圖層";
        session.History.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: _ =>
            {
                if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(oldSelection);
                Apply(oldOffsets, moved: false);
            },
            redo: _ =>
            {
                if (!ReferenceEquals(newSelection, oldSelection)) session.ApplySelection(newSelection);
                Apply(newOffsets, moved: true);
            }));
        return true;
    }

    // ---- 旋轉手勢（右鍵拖曳，paint.net 式）----

    private bool _rotateActive;
    private float _rotateStartDeg;
    private float _rotateAnchorDeg;
    private SKPoint[]? _rotateStartQuad; // 四角模式：旋轉的是四角本身
    private WarpMesh? _rotateStartWarp;  // 彎曲模式：旋轉整張網格
    private SKPoint _rotateCenter;
    private float _rotateLastDeg;

    /// <summary>
    /// 右鍵按下開始旋轉：需要（或自動開始）變形 session。
    /// 旋轉角 = 指標相對框中心的角度變化；Shift 吸附 15°。
    /// </summary>
    public bool BeginRotate(EditorSession session, SKPoint p)
    {
        var transform = session.Transform ?? session.BeginTransform();
        if (transform == null) return false;
        _rotateActive = true;
        _rotateStartDeg = transform.RotationDeg;
        _rotateStartQuad = transform.Quad;
        _rotateStartWarp = transform.Warp;
        _rotateLastDeg = 0f;
        var frame = transform.FrameRect;
        _rotateCenter = new SKPoint(frame.MidX, frame.MidY);
        _rotateAnchorDeg = AngleDeg(p, _rotateCenter);
        transform.BeginGesturePreview(session.LiveElementRendering); // 拖曳期間 render thread 直接畫，不逐步蓋章
        return true;
    }

    public void ContinueRotate(EditorSession session, SKPoint p, ToolModifiers modifiers)
    {
        if (!_rotateActive || session.Transform is not { } transform) return;

        // 四角／彎曲模式：TargetRect／RotationDeg 已凍結，改把網格繞框中心轉
        if (transform.IsMeshMode)
        {
            var delta = AngleDeg(p, _rotateCenter) - _rotateAnchorDeg;
            if (modifiers.HasFlag(ToolModifiers.Shift))
                delta = MathF.Round(delta / 15f) * 15f;
            delta = NormalizeDeg(delta);
            if (Math.Abs(delta - _rotateLastDeg) < 0.05f) return;
            _rotateLastDeg = delta;
            var changed = transform.Warp != null && _rotateStartWarp != null
                ? transform.SetWarp(_rotateStartWarp.Rotated(_rotateCenter, delta))
                : _rotateStartQuad != null && transform.SetQuad(QuadGeometry.Rotated(_rotateStartQuad, _rotateCenter, delta));
            if (!changed) return;
            transform.Apply(preview: true);
            session.RefreshSelectionHandles();
            return;
        }

        var center = new SKPoint(transform.TargetRect.MidX, transform.TargetRect.MidY);
        var angle = _rotateStartDeg + AngleDeg(p, center) - _rotateAnchorDeg;
        if (modifiers.HasFlag(ToolModifiers.Shift))
            angle = MathF.Round(angle / 15f) * 15f;
        angle = NormalizeDeg(angle);
        if (Math.Abs(angle - transform.RotationDeg) < 0.05f) return;
        transform.RotationDeg = angle;
        transform.Apply(preview: true);
        session.RefreshSelectionHandles();
    }

    public void EndRotate(EditorSession session)
    {
        if (!_rotateActive) return;
        _rotateActive = false;
        if (session.Transform is not { } transform) return;
        transform.EndGesture();
        session.RefreshSelectionHandles();
    }

    private static float AngleDeg(SKPoint p, SKPoint center) =>
        MathF.Atan2(p.Y - center.Y, p.X - center.X) * 180f / MathF.PI;

    private static float NormalizeDeg(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    /// <summary>把點以 center 為軸旋轉 deg 度。</summary>
    public static SKPoint RotatePoint(SKPoint p, SKPoint center, float deg)
    {
        if (Math.Abs(deg) < 0.01f) return p;
        return SKMatrix.CreateRotationDegrees(deg, center.X, center.Y).MapPoint(p);
    }

    /// <summary>點是否落在（可能已旋轉的）變形框內。</summary>
    private static bool TransformContains(TransformSession transform, SKPoint p)
    {
        if (transform.Warp is { } warp) return warp.Bounds.Contains(p.X, p.Y);
        if (transform.Quad is { } quad) return QuadGeometry.Contains(quad, p);
        var local = RotatePoint(p,
            new SKPoint(transform.TargetRect.MidX, transform.TargetRect.MidY),
            -transform.RotationDeg);
        return transform.TargetRect.Contains(local.X, local.Y);
    }

    /// <summary>
    /// 八個把手的位置：0=左上 1=右上 2=右下 3=左下（角），4=上中 5=右中 6=下中 7=左中（邊）。
    /// 畫把手與命中測試共用同一份，才不會畫在一處、點在另一處。
    /// </summary>
    public static SKPoint[] HandlePoints(SKRect rect) =>
    [
        new(rect.Left, rect.Top), new(rect.Right, rect.Top),
        new(rect.Right, rect.Bottom), new(rect.Left, rect.Bottom),
        new(rect.MidX, rect.Top), new(rect.Right, rect.MidY),
        new(rect.MidX, rect.Bottom), new(rect.Left, rect.MidY),
    ];

    /// <summary>邊把手（4..7）只動一軸。</summary>
    public static bool IsEdgeHandle(int handle) => handle >= 4;

    /// <summary>把手命中測試；回傳 <see cref="HandlePoints"/> 的索引（角優先），未命中為 -1。</summary>
    public static int HitCorner(SKRect rect, SKPoint p, float tolerance)
    {
        var handles = HandlePoints(rect);
        for (var i = 0; i < handles.Length; i++)
        {
            if (Math.Abs(p.X - handles[i].X) <= tolerance && Math.Abs(p.Y - handles[i].Y) <= tolerance)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 拖角縮放：對角固定；keepAspect 時維持長寬比 —— 給了 <paramref name="aspect"/>（寬/高）就用它
    /// （＝物件「最原始」的比例，不是變形後的），否則用起始框的比例。
    /// 邊把手（4..7）只動那一軸、對邊固定；keepAspect 時另一軸以中心為準等比跟著變。
    /// </summary>
    public static SKRect ResizeRect(SKRect start, int corner, SKPoint p, bool keepAspect, float? aspect = null)
    {
        var l = Math.Min(start.Left, start.Right);
        var t = Math.Min(start.Top, start.Bottom);
        var r = Math.Max(start.Left, start.Right);
        var b = Math.Max(start.Top, start.Bottom);

        if (IsEdgeHandle(corner))
        {
            var nl = l; var nt = t; var nr = r; var nb = b;
            switch (corner)
            {
                case 4: nt = p.Y; break;
                case 5: nr = p.X; break;
                case 6: nb = p.Y; break;
                default: nl = p.X; break;
            }
            if (keepAspect && (aspect is > 0 || (start.Width > 0 && start.Height > 0)))
            {
                var ratio = aspect is > 0 ? aspect.Value : start.Width / start.Height;
                if (corner is 4 or 6)
                {
                    // 上下邊：高度跟指標，寬度依比例、左右對稱
                    var cx = (l + r) / 2f;
                    var ew = Math.Abs(nb - nt) * ratio;
                    nl = cx - ew / 2f; nr = cx + ew / 2f;
                }
                else
                {
                    var cy = (t + b) / 2f;
                    var eh = Math.Abs(nr - nl) / ratio;
                    nt = cy - eh / 2f; nb = cy + eh / 2f;
                }
            }
            return new SKRect(Math.Min(nl, nr), Math.Min(nt, nb), Math.Max(nl, nr), Math.Max(nt, nb));
        }

        // 對角（固定點）
        var anchor = corner switch
        {
            0 => new SKPoint(r, b),
            1 => new SKPoint(l, b),
            2 => new SKPoint(l, t),
            _ => new SKPoint(r, t),
        };

        var w = p.X - anchor.X;
        var h = p.Y - anchor.Y;

        if (keepAspect && (aspect is > 0 || (start.Width > 0 && start.Height > 0)))
        {
            var ratio = aspect is > 0 ? aspect.Value : start.Width / start.Height;
            // 取較大的一邊決定尺寸，另一邊依比例
            if (Math.Abs(w) / ratio > Math.Abs(h))
                h = Math.Sign(h == 0 ? 1 : h) * Math.Abs(w) / ratio;
            else
                w = Math.Sign(w == 0 ? 1 : w) * Math.Abs(h) * ratio;
        }

        return new SKRect(
            Math.Min(anchor.X, anchor.X + w), Math.Min(anchor.Y, anchor.Y + h),
            Math.Max(anchor.X, anchor.X + w), Math.Max(anchor.Y, anchor.Y + h));
    }
}
