using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
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
    }

    private readonly ElementDragHelper _elementDrag = new();
    private TargetKind _kind;
    private int _corner;
    private SKRect _startRect;
    private SelectionMask? _startSelection;

    public bool IsActive => _kind != TargetKind.None;

    /// <summary>目前畫布上「被框住的東西」的外框；null = 沒有。</summary>
    public static SKRect? GetFrame(EditorSession session)
    {
        if (session.Transform is { } transform) return transform.TargetRect;
        if (session.Floating is { } floating) return floating.TargetRect;

        if (session.SelectedElement is { } sel &&
            session.Document.FindLayer(sel.LayerId) is RasterLayer layer &&
            ReferenceEquals(layer, session.Document.ActiveLayer) &&
            layer.FindElement(sel.ElementId) is { } element)
        {
            var frame = element.FrameBounds;
            return frame.IsEmpty ? null : frame;
        }

        if (session.Selection is { IsEmpty: false } selection)
        {
            var b = selection.Bounds;
            return new SKRect(b.Left, b.Top, b.Right, b.Bottom);
        }

        // 移動工具、什麼都沒被框住：框住的是「整個圖層（或群組）內容」（可超出畫布 ——
        // 圖層本來就可持有畫布外像素，這個框是把它們整批縮放回來的入口）。
        // 只在移動工具下顯示，繪畫類工具不該一直有個框在畫面上。
        if (session.ActiveTool == session.Move)
        {
            return session.Document.ActiveLayer is GroupLayer group
                ? GroupContentFrame(group)
                : LayerContentFrame(session);
        }
        return null;
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
                        var b = raster.Surface.ExactContentBounds();
                        if (b.Width > 0 && b.Height > 0)
                        {
                            Add(new SKRect(
                                b.Left + raster.Offset.X, b.Top + raster.Offset.Y,
                                b.Right + raster.Offset.X, b.Bottom + raster.Offset.Y));
                        }
                        foreach (var el in raster.Elements)
                        {
                            var eb = el.FrameBounds;
                            if (!eb.IsEmpty) Add(eb);
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

        var b = layer.Surface.ExactContentBounds(); // 內容沒變時是 O(1)（按寫入版本快取）
        if (b.Width > 0 && b.Height > 0)
        {
            Add(new SKRect(
                b.Left + layer.Offset.X, b.Top + layer.Offset.Y,
                b.Right + layer.Offset.X, b.Bottom + layer.Offset.Y));
        }
        // 拖角走變形 session（像素與本層文字一起縮放），框也要把文字算進去才對得上
        foreach (var el in layer.Elements)
        {
            var eb = el.FrameBounds;
            if (!eb.IsEmpty) Add(eb);
        }
        return acc is { Width: > 0, Height: > 0 } ? acc : null;
    }

    /// <summary>試著從四角把手開始拖曳。tolerance 為 doc 像素。</summary>
    public bool TryBegin(EditorSession session, SKPoint p, float tolerance)
    {
        // 進行中的變形框（可能已旋轉：把指標反轉回未旋轉空間再測角）
        if (session.Transform is { } transform)
        {
            var local = MoveTool.RotatePoint(p,
                new SKPoint(transform.TargetRect.MidX, transform.TargetRect.MidY),
                -transform.RotationDeg);
            var tCorner = MoveTool.HitCorner(transform.TargetRect, local, tolerance);
            if (tCorner < 0) return false;
            _kind = TargetKind.Transform;
            _corner = tCorner;
            _startRect = transform.TargetRect;
            transform.BeginGesturePreview(); // 拖曳期間 render thread 直接畫，不逐步蓋章
            return true;
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
            if (session.BeginTransform() is not { } begun) return false;
            _kind = TargetKind.Transform;
            _corner = corner;
            _startRect = begun.TargetRect;
            begun.BeginGesturePreview();
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
                var resizedRect = SelectionMask.SnapToPixels(
                    MoveTool.ResizeRect(_startRect, _corner, p, keepAspect));
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

            case TargetKind.Transform when session.Transform is { } transform:
            {
                // 框可能已旋轉：在未旋轉空間裡算縮放（指標先反轉），角度不變
                var local = MoveTool.RotatePoint(p,
                    new SKPoint(_startRect.MidX, _startRect.MidY), -transform.RotationDeg);
                var target = SelectionMask.SnapToPixels(
                    MoveTool.ResizeRect(_startRect, _corner, local, keepAspect));
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
