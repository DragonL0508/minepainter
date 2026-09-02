using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>鋼筆工具：錨點／把手互動、封閉、轉選取、描邊與填滿。</summary>
public class PenToolTests
{
    private static ToolPointerEvent Ev(float x, float y, ToolModifiers mods = ToolModifiers.None) =>
        new(new SKPoint(x, y), 1f, mods, 1, 1f);

    private static SKColor LayerPx(RasterLayer layer, int docX, int docY)
    {
        var lx = docX - layer.Offset.X;
        var ly = docY - layer.Offset.Y;
        var idx = TileIndex.FromPixel(lx, ly);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Transparent;
        var rect = idx.ToPixelRect();
        using var pixmap = tile.AsPixmap();
        return pixmap.GetPixelColor(lx - rect.Left, ly - rect.Top);
    }

    private static void Click(PenTool pen, EditorSession s, float x, float y)
    {
        pen.OnPointerDown(Ev(x, y), s);
        pen.OnPointerUp(Ev(x, y), s);
    }

    [Fact]
    public void Click_AddsCornerAnchors_ClickOnFirst_ClosesPath()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var pen = session.Pen;
        session.ActiveTool = pen;

        Click(pen, session, 20, 20);
        Click(pen, session, 120, 30);
        Click(pen, session, 100, 130);
        var path = session.PenPath!;
        Assert.Equal(3, path.Count);
        Assert.All(path.Anchors, a => Assert.False(a.IsSmooth));
        Assert.True(path.IsAppendable);
        Assert.Equal(2, path.Active);

        Click(pen, session, 21, 19); // 點回起點（容差內）
        path = session.PenPath!;
        Assert.True(path.Closed);
        Assert.False(path.IsAppendable);
        Assert.Equal(3, path.Count);

        using var sk = path.ToSKPath();
        Assert.Equal(20, sk.Bounds.Left, 1);
        Assert.Equal(130, sk.Bounds.Bottom, 1);
    }

    [Fact]
    public void Drag_CreatesSmoothAnchor_WithMirroredHandles()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var pen = session.Pen;
        session.ActiveTool = pen;

        pen.OnPointerDown(Ev(50, 50), session);
        pen.OnPointerMove(Ev(80, 40), session);
        pen.OnPointerUp(Ev(80, 40), session);

        var a = session.PenPath!.Anchors[0];
        Assert.True(a.IsSmooth);
        Assert.Equal(new SKPoint(80, 40), a.HandleOut);
        Assert.Equal(new SKPoint(20, 60), a.HandleIn); // 鏡射
    }

    [Fact]
    public void DragHandle_RotatesOpposite_KeepingItsLength_AltBreaks()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var pen = session.Pen;
        session.ActiveTool = pen;

        pen.OnPointerDown(Ev(50, 50), session);
        pen.OnPointerMove(Ev(90, 50), session); // out=(90,50) in=(10,50)，長度各 40
        pen.OnPointerUp(Ev(90, 50), session);

        // 拖 out 把手到 (50, 90)（往下、長度 40）→ in 轉到 (50, 10)
        pen.OnPointerDown(Ev(90, 50), session);
        pen.OnPointerMove(Ev(50, 90), session);
        pen.OnPointerUp(Ev(50, 90), session);
        var a = session.PenPath!.Anchors[0];
        Assert.Equal(new SKPoint(50, 90), a.HandleOut);
        Assert.Equal(50, a.HandleIn.X, 2);
        Assert.Equal(10, a.HandleIn.Y, 2);

        // Alt 拖 in 把手：只動自己
        pen.OnPointerDown(Ev(50, 10), session);
        pen.OnPointerMove(Ev(30, 20, ToolModifiers.Alt), session);
        pen.OnPointerUp(Ev(30, 20, ToolModifiers.Alt), session);
        a = session.PenPath!.Anchors[0];
        Assert.Equal(new SKPoint(30, 20), a.HandleIn);
        Assert.Equal(new SKPoint(50, 90), a.HandleOut);
    }

    [Fact]
    public void DragAnchor_MovesItWithHandles_RemoveLast_Clear()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var pen = session.Pen;
        session.ActiveTool = pen;

        Click(pen, session, 20, 20);
        pen.OnPointerDown(Ev(100, 20), session);
        pen.OnPointerMove(Ev(120, 40), session);
        pen.OnPointerUp(Ev(120, 40), session);
        Click(pen, session, 100, 120);

        // 拖第二個錨點
        pen.OnPointerDown(Ev(100, 20), session);
        pen.OnPointerMove(Ev(110, 25), session);
        pen.OnPointerUp(Ev(110, 25), session);
        var moved = session.PenPath!.Anchors[1];
        Assert.Equal(new SKPoint(110, 25), moved.Point);
        Assert.Equal(new SKPoint(130, 45), moved.HandleOut);
        Assert.Equal(3, session.PenPath.Count);

        PenCommands.RemoveLast(session);
        Assert.Equal(2, session.PenPath!.Count);
        Assert.Equal(1, session.PenPath.Active);
        PenCommands.Clear(session);
        Assert.Null(session.PenPath);
    }

    [Fact]
    public void MakeSelection_FromOpenPath_ClosesWithStraightLine_KeepsPath()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var pen = session.Pen;
        session.ActiveTool = pen;
        Click(pen, session, 20, 20);
        Click(pen, session, 200, 20);
        Click(pen, session, 200, 200);
        Click(pen, session, 20, 200);

        Assert.True(PenCommands.MakeSelection(session));
        var sel = session.Selection;
        Assert.NotNull(sel);
        Assert.False(sel!.IsEmpty);
        Assert.Equal(255, sel.CoverageAt(100, 100));
        Assert.Equal(0, sel.CoverageAt(10, 10));
        Assert.NotNull(session.PenPath); // 工作路徑保留
        Assert.False(session.PenPath!.IsAppendable);
        Assert.Equal("路徑轉選取", session.History.UndoLabel);
    }

    [Fact]
    public void StrokeAndFill_RasterizeIntoActiveLayer_WithUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var pen = session.Pen;
        session.ActiveTool = pen;
        session.Foreground = new SKColor(255, 0, 0);
        Click(pen, session, 20, 20);
        Click(pen, session, 200, 20);
        Click(pen, session, 200, 200);

        Assert.True(PenCommands.StrokePath(session, 6f));
        Assert.True(LayerPx(layer, 100, 20).Alpha > 200); // 上邊線上
        Assert.Equal(0, LayerPx(layer, 100, 100).Alpha);  // 三角形內部沒填
        Assert.Equal("描邊路徑", session.History.UndoLabel);

        Assert.True(PenCommands.FillPath(session));
        Assert.True(LayerPx(layer, 150, 100).Alpha > 200); // 內部（直線封回）
        Assert.Equal("填滿路徑", session.History.UndoLabel);

        session.Undo();
        Assert.Equal(0, LayerPx(layer, 150, 100).Alpha);
        session.Undo();
        Assert.Equal(0, LayerPx(layer, 100, 20).Alpha);
    }

    [Fact]
    public void Stroke_RefusedOnTextLayer()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var doc = session.Document;
        var text = new RasterLayer { Name = "T" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(text);
            text.AddElement(new TextElement { Text = "Hi", Position = new SKPoint(40, 80), FontSize = 32 });
            doc.ActiveLayer = text;
        }
        var pen = session.Pen;
        session.ActiveTool = pen;
        Click(pen, session, 20, 20);
        Click(pen, session, 200, 20);
        Assert.False(PenCommands.StrokePath(session, 4f));
        Assert.False(text.ViolatesTextLayerInvariant);
    }
}
