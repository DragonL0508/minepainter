using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 鋼筆工具（Photoshop 式）：點一下＝角點、按住拖曳＝拉出對稱把手的平滑點；
/// 點回第一個錨點＝封閉；拖既有錨點＝移動它；拖選中錨點的把手＝調曲線
/// （對側把手跟著轉、保持自己的長度；Alt＝只動這一側，把平滑點折成尖角）；Shift＝把手方向吸 45°。
/// 路徑本身不畫進圖層 —— 之後「轉為選取」「描邊路徑」「填滿路徑」（見 <see cref="PenCommands"/>）。
/// Enter／Esc／Backspace 由 UI 轉呼叫 PenCommands（情境鍵）。
/// </summary>
public sealed class PenTool : ITool
{
    public string Name => "鋼筆";

    /// <summary>描邊路徑的線寬（工具列「大小」）。</summary>
    public float StrokeWidth { get; set; } = 4f;

    private enum Mode
    {
        None,
        /// <summary>剛加入錨點；拖過門檻就拉出對稱把手。</summary>
        NewAnchor,
        MoveAnchor,
        MoveHandle,
    }

    private Mode _mode;
    private int _index;
    private bool _outHandle;
    private SKPoint _press;
    private PenAnchor _startAnchor = PenAnchor.Corner(SKPoint.Empty);
    private float _tolerance = 8f;

    /// <summary>命中容差（doc px）＝螢幕 8px。</summary>
    private static float ToleranceFor(ToolPointerEvent e) => 8f / Math.Max(0.01f, e.ViewScale);

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        var p = e.DocPosition;
        _tolerance = ToleranceFor(e);
        _press = p;
        var path = session.PenPath;

        if (path is { IsEmpty: false })
        {
            // 1) 選中錨點的把手
            if (path.Active >= 0 && path.Active < path.Count)
            {
                var a = path.Anchors[path.Active];
                if (a.HasHandleOut && Near(p, a.HandleOut))
                {
                    _mode = Mode.MoveHandle; _index = path.Active; _outHandle = true; _startAnchor = a;
                    return;
                }
                if (a.HasHandleIn && Near(p, a.HandleIn))
                {
                    _mode = Mode.MoveHandle; _index = path.Active; _outHandle = false; _startAnchor = a;
                    return;
                }
            }

            // 2) 既有錨點：接著畫時點回起點＝封閉；否則移動該錨點
            var hit = path.HitAnchor(p, _tolerance);
            if (hit >= 0)
            {
                if (hit == 0 && path.IsAppendable && path.Count >= 2)
                {
                    session.PenPath = path.WithClosed();
                    _mode = Mode.None;
                    return;
                }
                _mode = Mode.MoveAnchor;
                _index = hit;
                _startAnchor = path.Anchors[hit];
                session.PenPath = path.WithActive(hit);
                return;
            }
        }

        // 3) 新錨點（封閉／已結束的路徑 → 從頭開一條新的，PS 也是這樣）
        if (path == null || !path.IsAppendable) path = PenPath.Empty;
        path = path.Append(PenAnchor.Corner(p));
        session.PenPath = path;
        _mode = Mode.NewAnchor;
        _index = path.Count - 1;
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (_mode == Mode.None || session.PenPath is not { } path || _index < 0 || _index >= path.Count) return;
        var p = e.DocPosition;
        var shift = e.Modifiers.HasFlag(ToolModifiers.Shift);
        var alt = e.Modifiers.HasFlag(ToolModifiers.Alt);

        switch (_mode)
        {
            case Mode.NewAnchor:
            {
                // 沒拖過門檻維持角點（單純點一下）；拖了就是平滑點，把手對稱
                if (Dist(p, _press) < _tolerance * 0.5f) return;
                var anchor = path.Anchors[_index];
                var handle = shift ? Snap45(anchor.Point, p) : p;
                session.PenPath = path.Replace(_index, anchor.WithSymmetricOut(handle));
                break;
            }

            case Mode.MoveAnchor:
            {
                var dx = p.X - _press.X;
                var dy = p.Y - _press.Y;
                if (shift)
                {
                    if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0;
                }
                session.PenPath = path.Replace(_index, _startAnchor.Translated(dx, dy));
                break;
            }

            case Mode.MoveHandle:
            {
                var anchor = _startAnchor;
                var handle = shift ? Snap45(anchor.Point, p) : p;
                PenAnchor next;
                if (_outHandle)
                {
                    next = anchor with { HandleOut = handle };
                    if (!alt && anchor.HasHandleIn)
                        next = next with { HandleIn = Mirror(anchor.Point, handle, Dist(anchor.Point, anchor.HandleIn)) };
                }
                else
                {
                    next = anchor with { HandleIn = handle };
                    if (!alt && anchor.HasHandleOut)
                        next = next with { HandleOut = Mirror(anchor.Point, handle, Dist(anchor.Point, anchor.HandleOut)) };
                }
                session.PenPath = path.Replace(_index, next);
                break;
            }
        }
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        _mode = Mode.None;
    }

    private bool Near(SKPoint a, SKPoint b) =>
        Math.Abs(a.X - b.X) <= _tolerance && Math.Abs(a.Y - b.Y) <= _tolerance;

    private static float Dist(SKPoint a, SKPoint b) => SKPoint.Distance(a, b);

    /// <summary>對側把手：方向相反、長度維持原本的（PS 平滑點的行為）。</summary>
    private static SKPoint Mirror(SKPoint center, SKPoint handle, float keepLength)
    {
        var dx = handle.X - center.X;
        var dy = handle.Y - center.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return center;
        return new SKPoint(center.X - dx / len * keepLength, center.Y - dy / len * keepLength);
    }

    /// <summary>把手方向吸附 45° 的倍數（Shift）。</summary>
    public static SKPoint Snap45(SKPoint center, SKPoint p)
    {
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return p;
        var step = MathF.PI / 4f;
        var angle = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
        return new SKPoint(center.X + MathF.Cos(angle) * len, center.Y + MathF.Sin(angle) * len);
    }
}

/// <summary>鋼筆路徑的落地動作（工具列按鈕與 Enter／Esc／Backspace）。</summary>
public static class PenCommands
{
    /// <summary>Backspace：拿掉最後一個錨點；空了就清掉。</summary>
    public static void RemoveLast(EditorSession session)
    {
        if (session.PenPath is not { IsEmpty: false } path) return;
        var next = path.RemoveLast();
        session.PenPath = next.IsEmpty ? null : next;
    }

    /// <summary>Esc：丟棄工作路徑。</summary>
    public static void Clear(EditorSession session) => session.PenPath = null;

    /// <summary>結束開放路徑（之後點擊開新路徑）。</summary>
    public static void Finish(EditorSession session)
    {
        if (session.PenPath is { IsEmpty: false } path && path.IsAppendable)
            session.PenPath = path.WithFinished();
    }

    /// <summary>路徑 → 選取範圍（開放路徑以直線封回；少於 3 點做不出面積）。路徑保留。</summary>
    public static bool MakeSelection(EditorSession session, SelectionCombineMode mode = SelectionCombineMode.Replace)
    {
        if (session.PenPath is not { } path || path.Count < 3)
        {
            session.Notify("至少要三個錨點才能轉成選取範圍");
            return false;
        }
        session.CommitPendingEdits();
        using var sk = path.ToSKPath(forceClose: true);
        var mask = SelectionMask.FromPath(sk, session.Document.Bounds);
        var combined = mode == SelectionCombineMode.Replace
            ? mask
            : SelectionMask.Combine(session.Selection, mask, mode);
        SelectionCommands.SetSelection(session, combined, "路徑轉選取");
        session.PenPath = path.WithFinished();
        return true;
    }

    /// <summary>沿路徑描邊（前景色、指定線寬）到作用中圖層；受選取範圍裁切。</summary>
    public static bool StrokePath(EditorSession session, float width)
    {
        if (session.PenPath is not { Count: >= 2 } path)
        {
            session.Notify("至少要兩個錨點才能描邊");
            return false;
        }
        using var sk = path.ToSKPath();
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, width),
            Color = session.Foreground,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        var bounds = sk.Bounds;
        bounds.Inflate(paint.StrokeWidth, paint.StrokeWidth);
        return Rasterize(session, "描邊路徑", bounds, canvas => canvas.DrawPath(sk, paint));
    }

    /// <summary>以前景色填滿路徑（開放路徑以直線封回）到作用中圖層；受選取範圍裁切。</summary>
    public static bool FillPath(EditorSession session)
    {
        if (session.PenPath is not { Count: >= 3 } path)
        {
            session.Notify("至少要三個錨點才能填滿");
            return false;
        }
        using var sk = path.ToSKPath(forceClose: true);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = session.Foreground,
            IsAntialias = true,
        };
        return Rasterize(session, "填滿路徑", sk.Bounds, canvas => canvas.DrawPath(sk, paint));
    }

    /// <summary>把繪製動作烙進作用中圖層（doc 座標），記單一步 undo（同形狀工具）。</summary>
    private static bool Rasterize(EditorSession session, string label, SKRect docBoundsF, Action<SKCanvas> draw)
    {
        var doc = session.Document;
        if (doc.ActiveLayer is not RasterLayer layer)
        {
            session.Notify("請先選一個點陣圖層");
            return false;
        }
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return false;
        }
        session.CommitPendingEdits();

        var docRect = SKRectI.Intersect(SKRectI.Ceiling(docBoundsF), doc.Bounds);
        docRect.Inflate(1, 1);
        docRect = SKRectI.Intersect(docRect, doc.Bounds);
        if (docRect.Width <= 0 || docRect.Height <= 0)
        {
            session.Notify("路徑不在畫布內");
            return false;
        }

        TileDeltaEntry? entry;
        var selectionClip = session.Selection?.OutlinePath;
        lock (doc.SyncRoot)
        {
            using var before = layer.Surface.Snapshot();
            var layerRect = new SKRectI(
                docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
                docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

            foreach (var idx in Tiles.TileIndex.CoveringRect(layerRect))
            {
                var tile = layer.Surface.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tiles.Tile.Info, tile.Pixels, Tiles.Tile.RowBytes);
                var canvas = surface.Canvas;
                var tileRect = idx.ToPixelRect();
                canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
                canvas.ClipRect(SKRect.Create(doc.Bounds.Left, doc.Bounds.Top, doc.Bounds.Width, doc.Bounds.Height));
                if (selectionClip != null) canvas.ClipPath(selectionClip, antialias: true);
                draw(canvas);
                canvas.Flush();
            }
            entry = TileDeltaEntry.Capture(label, layer, before, layerRect);
        }

        if (entry != null) session.History.Push(entry);
        layer.Invalidate(docRect);
        return entry != null;
    }
}
