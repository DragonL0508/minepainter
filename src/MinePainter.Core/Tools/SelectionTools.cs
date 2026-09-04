using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

public static class SelectionCommands
{
    /// <summary>套用新選取並推 undo entry。newSelection 為 null = 取消選取。</summary>
    public static void SetSelection(EditorSession session, SelectionMask? newSelection, string label) =>
        SetSelection(session, session.Selection, newSelection, label);

    /// <summary>
    /// 顯式指定 undo 要還原到的舊選取（工具在 pointer-down 就先清掉畫面上的選取時用）。
    /// </summary>
    public static void SetSelection(EditorSession session, SelectionMask? oldSelection,
        SelectionMask? newSelection, string label)
    {
        Apply(session, newSelection);
        if (ReferenceEquals(oldSelection, newSelection)) return;

        session.History.Push(new ActionHistoryEntry(label, SKRectI.Empty,
            undo: _ => Apply(session, oldSelection),
            redo: _ => Apply(session, newSelection)));
    }

    /// <summary>
    /// 套用選取 —— 選取範圍與「可拖角的框」是同一個概念，
    /// 把手框由 EditorSession 自動推導，這裡不必（也不該）另外同步。
    /// </summary>
    private static void Apply(EditorSession session, SelectionMask? selection) =>
        session.ApplySelection(selection);

    /// <summary>
    /// 文字圖層拒收像素選取。不變式是「有物件的圖層沒有像素」（<see cref="RasterLayer.IsTextLayer"/>），
    /// 所以框出來的選取沒有任何操作會理它 —— 複製是空的、填滿/清除會被擋、移動改走整層平移，
    /// 只剩一圈螞蟻線留在畫面上。乾脆按下去就不做，跟「全選」的處理一致。
    /// </summary>
    internal static bool RefusePixelSelection(EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer { IsTextLayer: true }) return false;
        session.Notify("文字圖層不能選取像素；要編輯像素請先「圖層文字平面化」");
        return true;
    }

    public static SelectionCombineMode ModeFrom(ToolModifiers mods)
    {
        var shift = mods.HasFlag(ToolModifiers.Shift);
        var ctrl = mods.HasFlag(ToolModifiers.Ctrl);
        if (shift && ctrl) return SelectionCombineMode.Intersect;
        if (shift) return SelectionCombineMode.Add;
        if (ctrl) return SelectionCombineMode.Subtract;
        return SelectionCombineMode.Replace;
    }
}

/// <summary>矩形選取：拖曳出矩形，Shift 加選 / Ctrl 減選 / Shift+Ctrl 交集。</summary>
public sealed class RectangleSelectTool : ITool
{
    public string Name => "矩形選取";

    private SKPoint _anchor;
    private bool _dragging;
    private SelectionMask? _original;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (SelectionCommands.RefusePixelSelection(session)) return;
        _anchor = e.DocPosition;
        _dragging = true;
        session.Preview = null;

        // 沒按修飾鍵 = Replace 模式：按下瞬間就清掉畫面上的舊選取（螞蟻線與把手一起；
        // undo 由放開時的 entry 還原）
        _original = session.Selection;
        if (SelectionCommands.ModeFrom(e.Modifiers) == SelectionCombineMode.Replace)
            session.ApplySelection(null);
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        var r = SKRect.Create(
            Math.Min(_anchor.X, e.DocPosition.X), Math.Min(_anchor.Y, e.DocPosition.Y),
            Math.Abs(e.DocPosition.X - _anchor.X), Math.Abs(e.DocPosition.Y - _anchor.Y));
        session.Preview = new OverlayPreview(
        [
            new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top),
            new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom),
        ], Closed: true);
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        _dragging = false;
        session.Preview = null;
        var original = _original;
        _original = null;

        var r = SKRect.Create(
            Math.Min(_anchor.X, e.DocPosition.X), Math.Min(_anchor.Y, e.DocPosition.Y),
            Math.Abs(e.DocPosition.X - _anchor.X), Math.Abs(e.DocPosition.Y - _anchor.Y));
        if (r.Width < 1 || r.Height < 1)
        {
            // 點一下 = 取消選取（paint.net 慣例）
            SelectionCommands.SetSelection(session, original, null, "取消選取");
            return;
        }

        using var path = new SKPath();
        path.AddRect(SelectionMask.SnapToPixels(r)); // 選取以整像素為單位，邊界才不會糊掉
        Apply(session, path, e.Modifiers, original, "矩形選取");
    }

    internal static void Apply(EditorSession session, SKPath path, ToolModifiers mods,
        SelectionMask? original, string label)
    {
        SelectionMask incoming;
        lock (session.Document.SyncRoot)
        {
            incoming = SelectionMask.FromPath(path, session.Document.Bounds);
        }
        // Replace 模式在 down 時已把 session.Selection 清掉，combine 以 original 為基準
        var combined = SelectionMask.Combine(original, incoming, SelectionCommands.ModeFrom(mods));
        SelectionCommands.SetSelection(session, original, combined is { IsEmpty: true } ? null : combined, label);
    }
}

/// <summary>
/// 橢圓（圓形）選取：拖出外接矩形。Ctrl 減選 / Shift+Ctrl 交集（與矩形選取同一套），
/// **Shift 拖出正圓**（與形狀工具同一個約束，見 <see cref="ShapeTool.Constrain"/>）——
/// Shift 在選取工具裡本來就是「加選」，兩件事會同時發生：加一個正圓進選取範圍。
/// </summary>
public sealed class EllipseSelectTool : ITool
{
    public string Name => "橢圓選取";

    /// <summary>預覽用的取樣點數（畫面上的虛線橢圓；夠圓又不會太多點）。</summary>
    private const int PreviewSegments = 72;

    private SKPoint _anchor;
    private bool _dragging;
    private SelectionMask? _original;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (SelectionCommands.RefusePixelSelection(session)) return;
        _anchor = e.DocPosition;
        _dragging = true;
        session.Preview = null;

        // 沒按修飾鍵 = Replace 模式：按下瞬間就清掉畫面上的舊選取（與矩形選取一致）
        _original = session.Selection;
        if (SelectionCommands.ModeFrom(e.Modifiers) == SelectionCombineMode.Replace)
            session.ApplySelection(null);
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        session.Preview = new OverlayPreview(OutlinePoints(Rect(e.DocPosition, e.Modifiers)), Closed: true);
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        _dragging = false;
        session.Preview = null;
        var original = _original;
        _original = null;

        var r = Rect(e.DocPosition, e.Modifiers);
        if (r.Width < 1 || r.Height < 1)
        {
            // 點一下 = 取消選取（與矩形選取一致）
            SelectionCommands.SetSelection(session, original, null, "取消選取");
            return;
        }

        using var path = new SKPath();
        path.AddOval(SelectionMask.SnapToPixels(r)); // 選取以整像素為單位，邊界才不會糊掉
        RectangleSelectTool.Apply(session, path, e.Modifiers, original, "橢圓選取");
    }

    /// <summary>拖曳出來的外接矩形；按住 Shift 時邊長取較長的一軸 ＝ 正圓。</summary>
    private SKRect Rect(SKPoint p, ToolModifiers modifiers)
    {
        var end = ShapeTool.Constrain(_anchor, p, modifiers.HasFlag(ToolModifiers.Shift), ShapeKind.Ellipse);
        return SKRect.Create(
            Math.Min(_anchor.X, end.X), Math.Min(_anchor.Y, end.Y),
            Math.Abs(end.X - _anchor.X), Math.Abs(end.Y - _anchor.Y));
    }

    /// <summary>橢圓的取樣折線（覆疊預覽畫的是折線，沒有曲線）。</summary>
    private static SKPoint[] OutlinePoints(SKRect r)
    {
        var cx = r.MidX;
        var cy = r.MidY;
        var rx = r.Width / 2f;
        var ry = r.Height / 2f;
        var points = new SKPoint[PreviewSegments];
        for (var i = 0; i < PreviewSegments; i++)
        {
            var a = i * 2f * MathF.PI / PreviewSegments;
            points[i] = new SKPoint(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry);
        }
        return points;
    }
}

/// <summary>套索選取：自由圈選。</summary>
public sealed class LassoSelectTool : ITool
{
    public string Name => "套索選取";

    private readonly List<SKPoint> _points = new();
    private bool _dragging;
    private SelectionMask? _original;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (SelectionCommands.RefusePixelSelection(session)) return;
        _points.Clear();
        _points.Add(e.DocPosition);
        _dragging = true;

        // Replace 模式：按下瞬間清掉畫面上的舊選取（螞蟻線與把手一起）
        _original = session.Selection;
        if (SelectionCommands.ModeFrom(e.Modifiers) == SelectionCombineMode.Replace)
            session.ApplySelection(null);
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        var last = _points[^1];
        var dx = e.DocPosition.X - last.X;
        var dy = e.DocPosition.Y - last.Y;
        if (dx * dx + dy * dy < 4) return; // 最小取樣間距 2px
        _points.Add(e.DocPosition);
        session.Preview = new OverlayPreview(_points.ToArray(), Closed: false);
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        _dragging = false;
        session.Preview = null;
        var original = _original;
        _original = null;

        if (_points.Count < 3)
        {
            SelectionCommands.SetSelection(session, original, null, "取消選取");
            return;
        }

        using var path = new SKPath();
        path.MoveTo(_points[0]);
        for (var i = 1; i < _points.Count; i++) path.LineTo(_points[i]);
        path.Close();
        RectangleSelectTool.Apply(session, path, e.Modifiers, original, "套索選取");
    }
}

/// <summary>魔術棒：在作用中圖層 flood fill 相近色成為選取。</summary>
public sealed class MagicWandTool : ITool
{
    public string Name => "魔術棒";

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (SelectionCommands.RefusePixelSelection(session)) return;
        if (session.Document.ActiveLayer is not RasterLayer layer) return;

        SelectionMask incoming;
        lock (session.Document.SyncRoot)
        {
            incoming = FloodFiller.Fill(layer,
                new SKPointI((int)e.DocPosition.X, (int)e.DocPosition.Y),
                session.Tolerance, session.Document.Bounds);
        }
        if (incoming.IsEmpty) return;

        var combined = SelectionMask.Combine(session.Selection, incoming, SelectionCommands.ModeFrom(e.Modifiers));
        SelectionCommands.SetSelection(session, combined is { IsEmpty: true } ? null : combined, "魔術棒選取");
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
    }
}
