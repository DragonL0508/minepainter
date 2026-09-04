using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 統一的「選取框把手」互動：畫布上被框住的東西只有一個概念，
/// 不論它是選取範圍、已提起的浮動內容、文字物件，還是整個圖層內容，
/// 拖角把手的操作方式都一樣。
///
/// 優先序：浮動內容 → 文字物件 → 選取範圍 → 圖層內容（僅移動工具）。
/// </summary>
public sealed class HandleDragController
{
    public enum TargetKind
    {
        None,
        Floating,
        Element,
        Selection,
        /// <summary>變形框 session（圖層/群組的移動縮放旋轉）。</summary>
        Transform,
        /// <summary>沒有別的東西被框住時，框住的是整個圖層／群組內容（僅移動工具）。</summary>
        LayerContent,
    }

    private readonly ElementDragHelper _elementDrag = new();
    private TargetKind _kind;
    private int _corner;
    private SKRect _startRect;
    private SelectionMask? _startSelection;
    private float _transformPad; // 變形框顯示用的效果外擴量（TargetRect 本身不含）

    // 四角（透視）／彎曲（扭曲）模式的拖曳：起始網格＋按下點，每步從起點換算不累積
    private SKPoint[]? _startQuad;
    private WarpMesh? _startWarp;
    private SKPoint _meshPress;
    private bool _freeCorner; // 透視模式按住 Shift：該角自由拖（PS 的「扭曲」）

    /// <summary>四角／彎曲模式下的把手命中與拖曳開始；沒命中回 false。</summary>
    private bool BeginMeshDrag(TransformSession transform, EditorSession session, SKPoint p, float tolerance,
        ToolModifiers modifiers, int forcedIndex = -1)
    {
        _startQuad = null;
        _startWarp = null;
        if (transform.Warp is { } warp)
        {
            var index = forcedIndex >= 0 ? forcedIndex : warp.HitPoint(p, tolerance);
            if (index < 0) return false;
            _startWarp = warp;
            _corner = index;
        }
        else if (transform.Quad is { } quad)
        {
            var handle = forcedIndex >= 0 ? forcedIndex : QuadGeometry.HitHandle(quad, p, tolerance, includeEdges: false);
            if (handle < 0) return false;
            _startQuad = quad;
            _corner = handle;
            _freeCorner = modifiers.HasFlag(ToolModifiers.Shift);
        }
        else
        {
            return false;
        }
        _kind = TargetKind.Transform;
        _meshPress = p;
        _startRect = transform.FrameRect;
        transform.BeginGesturePreview(session.LiveElementRendering);
        return true;
    }

    public bool IsActive => _kind != TargetKind.None;

    /// <summary>
    /// 物件的效果外擴量（doc px）：這層效果堆疊（外框／陰影／光暈…）會把像素畫到物件排版框之外，
    /// 使用者看到的框要包住算繪後的像素，不然框線會壓在外框上、把手也蓋住陰影。
    /// </summary>
    public static float ElementEffectPad(RasterLayer layer) =>
        layer.HasActiveEffects ? Effects.LayerEffectRenderer.TotalMargin(layer) : 0f;

    /// <summary>任一圖層節點的效果外擴量：點陣層看自己的堆疊；群組取子孫點陣層的最大值。</summary>
    public static float EffectPad(LayerNode node)
    {
        switch (node)
        {
            case RasterLayer raster:
                return ElementEffectPad(raster);
            case GroupLayer group:
            {
                var max = 0f;
                foreach (var child in group.Children) max = Math.Max(max, EffectPad(child));
                return max;
            }
            default:
                return 0f;
        }
    }

    private static SKRect Inflated(SKRect r, float pad)
    {
        if (pad > 0) r.Inflate(pad, pad);
        return r;
    }

    private static SKRect Deflated(SKRect r, float pad)
    {
        if (pad > 0) r.Inflate(-pad, -pad);
        return r;
    }

    /// <summary>
    /// 把手抓的是「含效果外擴的顯示框」上的一點，換算成它對應到「內容框」上的那一點 ——
    /// 顯示框的那個角往內縮 <paramref name="pad"/> 就是內容框的同一個角（只算把手會動的那幾軸）。
    /// </summary>
    private static SKPoint ToInnerHandle(SKPoint p, int corner, float pad)
    {
        if (pad <= 0) return p;
        var dx = corner switch { 0 or 3 or 7 => pad, 1 or 2 or 5 => -pad, _ => 0f };
        var dy = corner switch { 0 or 1 or 4 => pad, 2 or 3 or 6 => -pad, _ => 0f };
        return new SKPoint(p.X + dx, p.Y + dy);
    }

    /// <summary>使用者看到的物件框 = 排版框往外加效果外擴量。</summary>
    public static SKRect ElementFrame(RasterLayer layer, VectorElement element)
    {
        var frame = element.FrameBounds;
        var pad = ElementEffectPad(layer);
        if (pad > 0 && !frame.IsEmpty) frame.Inflate(pad, pad);
        return frame;
    }

    /// <summary>目前畫布上「被框住的東西」的外框；null = 沒有。</summary>
    public static SKRect? GetFrame(EditorSession session) => GetFrame(session, out _);

    /// <summary>同上，另外回報框住的是什麼 —— 繪製端要靠它決定畫法（見 <see cref="EditorSession.SelectionHandlesKind"/>）。</summary>
    public static SKRect? GetFrame(EditorSession session, out TargetKind kind)
    {
        kind = TargetKind.None;
        // 選取工具拖曳中：畫面上只留正在框出來的那條線，把手框全部收掉，
        // 放開（選取區確定）才讓把手出現。
        if (session.SelectionGestureActive) return null;

        // 變形框：TargetRect 是被變形的像素框，使用者看到的框要再包住效果外擴
        if (session.Transform is { } transform)
        {
            // 四角模式的框就是四角本身（SelectionHandlesQuad），外接矩形只給其他消費者用、不加效果外擴
            kind = TargetKind.Transform;
            if (transform.Quad != null) return transform.FrameRect;
            return Inflated(transform.TargetRect, EffectPad(transform.Target));
        }
        if (session.Floating is { } floating)
        {
            kind = TargetKind.Floating;
            return floating.TargetRect;
        }

        if (session.SelectedElement is { } sel &&
            session.Document.FindLayer(sel.LayerId) is RasterLayer layer &&
            ReferenceEquals(layer, session.Document.ActiveLayer) &&
            layer.FindElement(sel.ElementId) is { } element)
        {
            var frame = ElementFrame(layer, element);
            if (frame.IsEmpty) return null;
            kind = TargetKind.Element;
            return frame;
        }

        if (session.Selection is { IsEmpty: false } selection)
        {
            kind = TargetKind.Selection;
            var b = selection.Bounds;
            return new SKRect(b.Left, b.Top, b.Right, b.Bottom);
        }

        // 移動工具、什麼都沒被框住：框住的是「整個圖層（或群組）內容」（可超出畫布 ——
        // 圖層本來就可持有畫布外像素，這個框是把它們整批縮放回來的入口）。
        // 只在移動工具下顯示，繪畫類工具不該一直有個框在畫面上。
        // 點過空白處（LayerFrameDismissed）就不再自動長回來，畫面才是真的乾淨；
        // 下一次點到圖層內容或換圖層會重新框起來（見 EditorSession.LayerFrameDismissed）。
        if (session.ActiveTool == session.Move && !session.LayerFrameDismissed)
        {
            var content = session.Document.ActiveLayer is GroupLayer group
                ? GroupContentFrame(group)
                : LayerContentFrame(session);
            if (content != null) kind = TargetKind.LayerContent;
            return content;
        }
        return null;
    }

    /// <summary>
    /// 這一點上有沒有「這個圖層（或群組）的內容」：像素不透明（含外框／陰影等效果畫出來的），
    /// 或落在某個物件的框裡。用外接矩形判斷會把 L 形、散落內容之間的空白也算成內容
    /// —— 使用者在明顯空無一物的地方點下去卻清不掉框，就是那樣來的。
    /// <paramref name="tolerance"/> 是允許的誤差半徑（doc px），細線與小點才抓得到。
    /// 須在 Document.SyncRoot 內呼叫。
    /// </summary>
    public static bool HitsContent(LayerNode node, SKPoint p, float tolerance)
    {
        switch (node)
        {
            case RasterLayer raster:
                return HitsRaster(raster, p, tolerance);
            case GroupLayer group:
                foreach (var child in group.Children)
                {
                    if (child.IsVisible && HitsContent(child, p, tolerance)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool HitsRaster(RasterLayer layer, SKPoint p, float tolerance)
    {
        foreach (var el in layer.Elements)
        {
            var b = el.FrameBounds;
            if (b.IsEmpty) continue;
            b.Inflate(tolerance, tolerance);
            if (b.Contains(p.X, p.Y)) return true;
        }

        // 中心＋一圈取樣點：只測單一像素的話，細線在縮小檢視下幾乎點不到
        if (AlphaAt(layer, p.X, p.Y)) return true;
        if (tolerance < 0.5f) return false;
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4f;
            if (AlphaAt(layer, p.X + MathF.Cos(a) * tolerance, p.Y + MathF.Sin(a) * tolerance)) return true;
        }
        return false;
    }

    private static bool AlphaAt(RasterLayer layer, float docX, float docY)
    {
        var lx = (int)MathF.Floor(docX) - layer.Offset.X;
        var ly = (int)MathF.Floor(docY) - layer.Offset.Y;
        var idx = Tiles.TileIndex.FromPixel(lx, ly);
        var tile = layer.DisplaySurface.GetTileForRead(idx); // 效果算好的那份：陰影／外框也算內容
        if (tile == null) return false;
        var rect = idx.ToPixelRect();
        using var pixmap = tile.AsPixmap();
        return pixmap.GetPixelColor(lx - rect.Left, ly - rect.Top).Alpha > 0;
    }

    /// <summary>群組內容外框 = 所有子孫點陣圖層的像素內容 ∪ 文字物件。須在 SyncRoot 內呼叫。</summary>
    public static SKRect? GroupContentFrame(GroupLayer group)
    {
        SKRect? acc = null;
        void Add(SKRect r) => acc = acc is { } a
            ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
            : r;

        void Walk(GroupLayer g)
        {
            foreach (var child in g.Children)
            {
                switch (child)
                {
                    case RasterLayer raster:
                    {
                        // 每層各自的效果外擴（外框／陰影畫在內容之外，框要包住它們）
                        var pad = ElementEffectPad(raster);
                        var b = raster.Surface.ExactContentBounds();
                        if (b.Width > 0 && b.Height > 0)
                        {
                            Add(Inflated(new SKRect(
                                b.Left + raster.Offset.X, b.Top + raster.Offset.Y,
                                b.Right + raster.Offset.X, b.Bottom + raster.Offset.Y), pad));
                        }
                        foreach (var el in raster.Elements)
                        {
                            var eb = el.FrameBounds;
                            if (!eb.IsEmpty) Add(Inflated(eb, pad));
                        }
                        break;
                    }
                    case GroupLayer sub:
                        Walk(sub);
                        break;
                }
            }
        }

        Walk(group);
        return acc is { Width: > 0, Height: > 0 } ? acc : null;
    }

    /// <summary>作用中圖層實際內容的外框（doc 座標；可超出畫布）；無內容為 null。須在 SyncRoot 內呼叫。</summary>
    public static SKRect? LayerContentFrame(EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer layer) return null;
        SKRect? acc = null;
        void Add(SKRect r) => acc = acc is { } a
            ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
            : r;

        var pad = ElementEffectPad(layer);
        var b = layer.Surface.ExactContentBounds(); // 內容沒變時是 O(1)（按寫入版本快取）
        if (b.Width > 0 && b.Height > 0)
        {
            Add(Inflated(new SKRect(
                b.Left + layer.Offset.X, b.Top + layer.Offset.Y,
                b.Right + layer.Offset.X, b.Bottom + layer.Offset.Y), pad));
        }
        // 拖角走變形 session（像素與本層文字一起縮放），框也要把文字算進去才對得上；
        // 效果外擴也一起算進去，第一次選到圖層時框就在外框／陰影之外
        foreach (var el in layer.Elements)
        {
            var eb = el.FrameBounds;
            if (!eb.IsEmpty) Add(Inflated(eb, pad));
        }
        return acc is { Width: > 0, Height: > 0 } ? acc : null;
    }

    /// <summary>試著從四角把手開始拖曳。tolerance 為 doc 像素。</summary>
    public bool TryBegin(EditorSession session, SKPoint p, float tolerance, ToolModifiers modifiers = ToolModifiers.None)
    {
        // 進行中的變形框（可能已旋轉：把指標反轉回未旋轉空間再測角）
        if (session.Transform is { } transform)
        {
            if (transform.IsMeshMode) return BeginMeshDrag(transform, session, p, tolerance, modifiers);

            var local = MoveTool.RotatePoint(p,
                new SKPoint(transform.TargetRect.MidX, transform.TargetRect.MidY),
                -transform.RotationDeg);
            _transformPad = EffectPad(transform.Target);
            var shownRect = Inflated(transform.TargetRect, _transformPad); // 把手畫在含效果外擴的框上
            var tCorner = MoveTool.HitCorner(shownRect, local, tolerance);
            if (tCorner < 0) return false;

            // 工具列切到透視／扭曲後才拖角：此時才進網格模式（同一個 session，不用先落地）
            if (session.Move.TransformMode != TransformMode.Free &&
                session.EnterTransformMode(session.Move.TransformMode) is { IsMeshMode: true } entered)
                return BeginMeshDrag(entered, session, p, tolerance, modifiers);

            _kind = TargetKind.Transform;
            _corner = tCorner;
            _startRect = shownRect;
            transform.BeginGesturePreview(session.LiveElementRendering); // 拖曳期間 render thread 直接畫，不逐步蓋章
            return true;
        }

        // 移動工具在透視／扭曲模式、還沒開始變形：框已經畫成該模式的把手（EditorSession.RefreshSelectionHandles），
        // 點中哪個把手就以它開 session（含文字的圖層會先自動平面化），沿用同一個索引繼續拖
        if (session.Move.TransformMode != TransformMode.Free && session.ActiveTool == session.Move &&
            session.Floating == null && session.Selection is not { IsEmpty: false })
        {
            var index = -1;
            if (session.SelectionHandlesWarp is { } previewWarp) index = previewWarp.HitPoint(p, tolerance);
            else if (session.SelectionHandlesQuad is { } previewQuad) index = QuadGeometry.HitHandle(previewQuad, p, tolerance, includeEdges: false);
            if (index < 0) return false;
            if (session.EnterTransformMode(session.Move.TransformMode) is not { IsMeshMode: true } entered) return false;
            return BeginMeshDrag(entered, session, p, tolerance, modifiers, index);
        }

        // 浮動內容
        if (session.Floating is { } floating)
        {
            var corner = MoveTool.HitCorner(floating.TargetRect, p, tolerance);
            if (corner < 0) return false;
            _kind = TargetKind.Floating;
            _corner = corner;
            _startRect = floating.TargetRect;
            return true;
        }

        // 文字物件（沿用既有的物件拖曳邏輯，含「太小就刪除」）
        if (_elementDrag.TryBegin(session, p, tolerance, allowInsideMove: false))
        {
            _kind = TargetKind.Element;
            return true;
        }

        // 選取範圍本身
        if (session.Selection is { IsEmpty: false } selection)
        {
            var b = selection.Bounds;
            var rect = new SKRect(b.Left, b.Top, b.Right, b.Bottom);
            var corner = MoveTool.HitCorner(rect, p, tolerance);
            if (corner < 0) return false;
            _kind = TargetKind.Selection;
            _corner = corner;
            _startRect = rect;
            _startSelection = selection;
            return true;
        }

        // 圖層／群組內容框（僅移動工具；走到這裡表示 SelectionHandles 就是它）：
        // 拖角＝開始變形 session（像素與文字一起縮放；含畫布外像素）。
        // 單層以前走「整層提起成浮動內容」，改走變形 session 的原因：
        // 落地後再拉回來能從原始像素續接（EditorSession.BeginTransform），縮小再放大不糊。
        if (session.ActiveTool == session.Move && session.SelectionHandles is { } content)
        {
            var corner = MoveTool.HitCorner(content, p, tolerance);
            if (corner < 0) return false;
            // 透視／扭曲：走 EnterTransformMode（含文字的圖層會先自動平面化再框）
            if (session.Move.TransformMode != TransformMode.Free)
            {
                if (session.EnterTransformMode(session.Move.TransformMode) is not { } entered) return false;
                if (entered.IsMeshMode) return BeginMeshDrag(entered, session, p, tolerance, modifiers);
            }
            if (session.BeginTransform() is not { } begun) return false;
            _kind = TargetKind.Transform;
            _corner = corner;
            _transformPad = EffectPad(begun.Target);
            _startRect = Inflated(begun.TargetRect, _transformPad);
            begun.BeginGesturePreview(session.LiveElementRendering);
            return true;
        }

        return false;
    }

    public void Continue(EditorSession session, SKPoint p, ToolModifiers modifiers)
    {
        var keepAspect = modifiers.HasFlag(ToolModifiers.Shift);

        switch (_kind)
        {
            case TargetKind.Element:
                _elementDrag.Continue(session, p, modifiers);
                break;

            case TargetKind.Floating when session.Floating is { } floating:
            {
                var before = floating.TargetBounds;
                // 對齊整像素：浮動內容的框與螞蟻線都由 TargetRect 推導，
                // 留小數會讓兩者在放大時對不齊
                // Shift＝回到像素「最原始」的比例（續接時 Pixels 是原始那份），不是目前變形後的框
                var floatingAspect = floating.PixelSize.Height > 0
                    ? (float)floating.PixelSize.Width / floating.PixelSize.Height : (float?)null;
                var resizedRect = SelectionMask.SnapToPixels(
                    MoveTool.ResizeRect(_startRect, _corner, p, keepAspect, floatingAspect));
                if (resizedRect == floating.TargetRect) break; // 同一格像素內的抖動
                floating.TargetRect = resizedRect;
                MoveTool.InvalidateFloating(session, floating, before); // 覆疊/重合成的取捨在那裡
                break;
            }

            case TargetKind.Selection when _startSelection != null:
            {
                var target = SelectionMask.SnapToPixels(
                    MoveTool.ResizeRect(_startRect, _corner, p, keepAspect));
                var resized = _startSelection.TransformedTo(target, session.Document.Bounds);
                if (resized != null)
                    session.Selection = resized; // 拖曳期間即時更新（不進 history）；把手框自動跟上
                break;
            }

            case TargetKind.Transform when session.Transform is { } transform && _startWarp != null:
            {
                // 彎曲：控制點從起始網格＋位移換算（角點帶著切線把手）；Shift 只沿一軸
                var delta = new SKPoint(p.X - _meshPress.X, p.Y - _meshPress.Y);
                if (keepAspect)
                {
                    if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) delta.Y = 0; else delta.X = 0;
                }
                if (!transform.SetWarp(WarpMesh.Drag(_startWarp, _corner, delta))) break;
                transform.Apply(preview: true);
                session.RefreshSelectionHandles();
                break;
            }

            case TargetKind.Transform when session.Transform is { } transform && _startQuad != null:
            {
                // 透視：從起始四角＋位移換算，鄰角對稱跟著動；Shift＝該角自由拖（PS 的扭曲）
                var delta = new SKPoint(p.X - _meshPress.X, p.Y - _meshPress.Y);
                var quad = _freeCorner || modifiers.HasFlag(ToolModifiers.Shift)
                    ? QuadGeometry.DistortDrag(_startQuad, _corner, delta, constrain: false)
                    : QuadGeometry.PerspectiveDrag(_startQuad, _corner, delta);
                if (!transform.SetQuad(quad)) break; // 凹／翻面的四邊形不接受，停在上一個合法狀態
                transform.Apply(preview: true);
                session.RefreshSelectionHandles();
                break;
            }

            case TargetKind.Transform when session.Transform is { } transform:
            {
                // 框可能已旋轉：在未旋轉空間裡算縮放（指標先反轉），角度不變
                var local = MoveTool.RotatePoint(p,
                    new SKPoint(_startRect.MidX, _startRect.MidY), -transform.RotationDeg);
                // Shift＝回到內容最原始的比例（ResetSize 是這輪／續接前的原始尺寸）
                var originalAspect = transform.ResetSize.Height > 0
                    ? transform.ResetSize.Width / transform.ResetSize.Height : (float?)null;
                // 縮放要在**內容框**上算，不是在含效果外擴的顯示框上：外擴是固定寬度、不跟著縮，
                // 兩個框的長寬比因此不一樣。在顯示框上套內容的比例、算完再扣掉外擴，出來的
                // 就不是原始比例了 —— 文字加了外光暈之後按住 Shift 縮放會歪掉就是這個
                // （外擴 0 的一般圖層維持原本的行為，下面兩行都等於沒做事）。
                var innerStart = Deflated(_startRect, _transformPad);
                var innerPoint = ToInnerHandle(local, _corner, _transformPad);
                var target = SelectionMask.SnapToPixels(
                    MoveTool.ResizeRect(innerStart, _corner, innerPoint, keepAspect, originalAspect));
                if (target.Width < 1 || target.Height < 1 || target == transform.TargetRect) break;
                transform.TargetRect = target;
                transform.Apply(preview: true);
                session.RefreshSelectionHandles();
                break;
            }
        }
    }

    public void End(EditorSession session)
    {
        var kind = _kind;
        var startSelection = _startSelection;
        _kind = TargetKind.None;
        _startSelection = null;
        _startQuad = null;
        _startWarp = null;

        switch (kind)
        {
            case TargetKind.Element:
                _elementDrag.End(session);
                break;

            case TargetKind.Floating when session.Floating is { } floating:
                session.RefreshSelectionHandles();
                if (floating.TargetRect.Width < 1 || floating.TargetRect.Height < 1)
                {
                    session.CancelFloating();
                    session.Notify("選取內容太小，已還原");
                }
                break;

            case TargetKind.Selection when startSelection != null:
            {
                var final = session.Selection;
                if (ReferenceEquals(final, startSelection)) break;

                // 值已即時套用，這裡只補上 history entry
                session.History.Push(new ActionHistoryEntry("調整選取範圍", SKRectI.Empty,
                    undo: _ => session.Selection = startSelection,
                    redo: _ => session.Selection = final));
                break;
            }

            case TargetKind.Transform when session.Transform is { } transform:
                // 手勢結束補一次 High 品質蓋章（覆疊殘影等合成器追上才收）；
                // history 等整個 session 落地時一次記
                transform.EndGesture();
                session.RefreshSelectionHandles();
                break;
        }
    }

    private static SKRect ToRect(SKRectI r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
