using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 文字工具基底：把手縮放 / 點中元素移動（統一走 ElementDragHelper），
/// 空白處交由子類建立新元素。
/// </summary>
public abstract class VectorToolBase : ITool
{
    public abstract string Name { get; }

    private readonly ElementDragHelper _drag = new();
    private bool _creating;

    protected SKPoint DragStart;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        var doc = session.Document;

        // 1) 已選元素的把手/內部
        if (_drag.TryBegin(session, e.DocPosition, handleTolerance: 10f, allowInsideMove: true))
            return;

        // 2) 點中任何可見文字元素 → 選取 + 移動
        lock (doc.SyncRoot)
        {
            if (VectorHitTest.FindTextAt(doc, e.DocPosition) is { } hit)
            {
                _drag.BeginMoveLocked(session, hit.Layer, hit.Element, e.DocPosition);
                return;
            }
        }

        // 3) 空白處 → 由子類決定（建立新元素）
        _creating = true;
        DragStart = e.DocPosition;
        OnCreateStart(e, session);
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (_drag.IsActive)
        {
            _drag.Continue(session, e.DocPosition, e.Modifiers);
        }
        else if (_creating)
        {
            OnCreateDrag(e, session);
        }
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (_drag.IsActive)
        {
            _drag.End(session);
        }
        else if (_creating)
        {
            _creating = false;
            OnCreateEnd(e, session);
        }
    }

    protected abstract void OnCreateStart(ToolPointerEvent e, EditorSession session);

    protected virtual void OnCreateDrag(ToolPointerEvent e, EditorSession session)
    {
    }

    protected abstract void OnCreateEnd(ToolPointerEvent e, EditorSession session);

    protected static void SetSelected(EditorSession session, RasterLayer? layer, VectorElement? element)
    {
        if (layer == null || element == null)
        {
            session.SelectedElement = null;
            return;
        }
        ElementDragHelper.SetSelected(session, layer, element);
    }
}

/// <summary>文字工具：點空白建立文字，點文字選取/拖曳；內容與字型由 UI 隨時改（永遠可編輯）。</summary>
public sealed class TextTool : VectorToolBase
{
    public override string Name => "文字";

    /// <summary>新文字的預設樣式（UI 綁定）。</summary>
    public float FontSize { get; set; } = 48f;
    public string FontFamily { get; set; } = "Microsoft JhengHei";
    public int FontWeight { get; set; } = 400;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public TextAlign Alignment { get; set; } = TextAlign.Left;


    protected override void OnCreateStart(ToolPointerEvent e, EditorSession session)
    {
    }

    protected override void OnCreateEnd(ToolPointerEvent e, EditorSession session)
    {
        // 拖曳不算建立
        var dx = e.DocPosition.X - DragStart.X;
        var dy = e.DocPosition.Y - DragStart.Y;
        if (dx * dx + dy * dy > 25) return;

        var doc = session.Document;

        // 已選著文字（例如剛輸入完）→ 第一下先取消選取，不直接開新文字（使用者明示）
        bool hasSelection;
        lock (doc.SyncRoot)
        {
            hasSelection = session.SelectedElement is { } sel &&
                           doc.FindLayer(sel.LayerId) is RasterLayer selLayer &&
                           selLayer.FindElement(sel.ElementId) != null;
        }
        if (hasSelection)
        {
            SetSelected(session, null, null);
            return;
        }

        // 沒有選取時，單擊即開始輸入（paint.net 式）。文字一定自己一層：先靜默建圖層，
        // 落地時內容為空 → 連圖層一起靜默收掉；有內容 → CommitNewTextLayer 補單一步「新增文字」。
        var layer = VectorCommands.CreateTextLayerSilently(doc);
        var element = new TextElement
        {
            Text = "",
            Position = e.DocPosition,
            FontSize = FontSize,
            FontFamily = FontFamily,
            FontWeight = FontWeight,
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            Strikethrough = Strikethrough,
            Alignment = Alignment,
            Color = session.Foreground,
        };
        lock (doc.SyncRoot)
        {
            layer.AddElement(element);
        }
        SetSelected(session, layer, element);
        session.PendingTextEdit = (layer.Id, element.Id); // UI 立即開畫布內編輯
    }
}

/// <summary>
/// 形狀工具：拖曳出矩形/橢圓/直線，放開時直接柵格化進作用中的點陣圖層
/// （paint.net 式；不產生向量物件）。受選取範圍裁切。
/// 幾何對齊像素格（paint.net 行為）：純填色貼齊像素邊界、奇數線寬貼齊像素中心，
/// 1px 線才不會糊成兩條灰線；拖曳期間的預覽用同一份幾何真正渲染，所見即所得。
/// </summary>
public sealed class ShapeTool : ITool
{
    public string Name => "形狀";

    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
    public bool Filled { get; set; } = true;
    public float StrokeWidth { get; set; } = 4f;

    private SKPoint _anchor;
    private bool _dragging;

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer layer) return;
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return;
        }
        _anchor = e.DocPosition;
        _dragging = true;
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        var end = Constrain(_anchor, e.DocPosition, e.Modifiers.HasFlag(ToolModifiers.Shift), Kind);
        session.Preview = new OverlayPreview([], Closed: false, BuildShape(_anchor, end, session.Foreground));
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
        if (!_dragging) return;
        _dragging = false;
        session.Preview = null;

        var end = Constrain(_anchor, e.DocPosition, e.Modifiers.HasFlag(ToolModifiers.Shift), Kind);
        var dx = Math.Abs(end.X - _anchor.X);
        var dy = Math.Abs(end.Y - _anchor.Y);
        if (dx < 2 && dy < 2) return;

        var doc = session.Document;
        if (doc.ActiveLayer is not RasterLayer layer) return;

        // 借用 ShapeElement 的繪製邏輯，但畫完即丟（不儲存物件）
        var shape = BuildShape(_anchor, end, session.Foreground);

        History.TileDeltaEntry? entry;
        var dirtyDoc = SKRectI.Intersect(shape.Bounds, doc.Bounds); // 不畫到畫布外
        if (dirtyDoc.Width <= 0 || dirtyDoc.Height <= 0) return;

        lock (doc.SyncRoot)
        {
            using var before = layer.Surface.Snapshot();
            Rasterize(layer, shape, session.Selection?.OutlinePath, doc.Bounds);

            var affected = new SKRectI(
                dirtyDoc.Left - layer.Offset.X, dirtyDoc.Top - layer.Offset.Y,
                dirtyDoc.Right - layer.Offset.X, dirtyDoc.Bottom - layer.Offset.Y);
            entry = History.TileDeltaEntry.Capture("形狀", layer, before, affected);
        }

        if (entry != null) session.History.Push(entry);
        layer.Invalidate(dirtyDoc);
    }

    /// <summary>
    /// Shift 約束（paint.net 式）：矩形／橢圓 → 正方形／正圓（邊長取較長的一軸、方向跟著指標）；
    /// 直線 → 角度吸附 15°。
    /// </summary>
    public static SKPoint Constrain(SKPoint anchor, SKPoint p, bool shift, ShapeKind kind)
    {
        if (!shift) return p;
        var dx = p.X - anchor.X;
        var dy = p.Y - anchor.Y;
        if (kind == ShapeKind.Line)
        {
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return p;
            var angle = MathF.Atan2(dy, dx);
            var step = MathF.PI / 12f; // 15°
            angle = MathF.Round(angle / step) * step;
            return new SKPoint(anchor.X + MathF.Cos(angle) * len, anchor.Y + MathF.Sin(angle) * len);
        }
        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var sx = dx < 0 ? -1f : 1f;
        var sy = dy < 0 ? -1f : 1f;
        return new SKPoint(anchor.X + sx * size, anchor.Y + sy * size);
    }

    /// <summary>由拖曳兩端點建立（像素對齊後的）形狀。</summary>
    public ShapeElement BuildShape(SKPoint a, SKPoint b, SKColor color)
    {
        var hasStroke = Kind == ShapeKind.Line || !Filled;
        var strokeWidth = hasStroke ? Math.Max(1f, StrokeWidth) : 0f;
        var rect = SnapRect(new SKRect(a.X, a.Y, b.X, b.Y), hasStroke ? strokeWidth : 0f);
        return new ShapeElement
        {
            Kind = Kind,
            Rect = rect,
            FillColor = Kind != ShapeKind.Line && Filled ? color : null,
            StrokeColor = color,
            StrokeWidth = strokeWidth,
        };
    }

    /// <summary>
    /// 像素對齊：無描邊 → 貼齊像素邊界；有描邊 → 奇數線寬貼齊像素中心（x.5）、偶數貼齊邊界。
    /// </summary>
    public static SKRect SnapRect(SKRect rect, float strokeWidth)
    {
        return new SKRect(Snap(rect.Left), Snap(rect.Top), Snap(rect.Right), Snap(rect.Bottom));

        float Snap(float v)
        {
            if (strokeWidth <= 0f) return MathF.Round(v);
            var odd = ((int)MathF.Round(strokeWidth) & 1) == 1;
            return odd ? MathF.Floor(v) + 0.5f : MathF.Round(v);
        }
    }

    private static void Rasterize(RasterLayer layer, ShapeElement shape, SKPath? selectionClip, SKRectI docBounds)
    {
        var docRect = SKRectI.Intersect(shape.Bounds, docBounds);
        if (docRect.Width <= 0 || docRect.Height <= 0) return;
        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        foreach (var idx in Tiles.TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tiles.Tile.Info, tile.Pixels, Tiles.Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = idx.ToPixelRect();
            // canvas → doc 座標
            canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
            canvas.ClipRect(SKRect.Create(docBounds.Left, docBounds.Top, docBounds.Width, docBounds.Height));
            if (selectionClip != null) canvas.ClipPath(selectionClip, antialias: true);
            shape.Render(canvas);
            canvas.Flush();
        }
    }
}
