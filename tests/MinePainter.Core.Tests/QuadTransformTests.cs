using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>透視／扭曲（四角模式）：單應矩陣、PS 式拖曳規則、與 TransformSession 的整合。</summary>
public class QuadTransformTests
{
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

    [Fact]
    public void RectToQuad_MapsCornersExactly_AndIdentityForSameCorners()
    {
        var src = new SKRect(10, 20, 110, 220);
        SKPoint[] quad = [new(0, 0), new(120, 15), new(100, 250), new(-10, 200)];
        var m = QuadGeometry.RectToQuad(src, quad);

        var corners = QuadGeometry.Corners(src);
        for (var i = 0; i < 4; i++)
        {
            var p = m.MapPoint(corners[i]);
            Assert.Equal(quad[i].X, p.X, 2);
            Assert.Equal(quad[i].Y, p.Y, 2);
        }
        Assert.NotEqual(0f, m.Persp0 + m.Persp1); // 真的是透視，不是仿射

        var id = QuadGeometry.RectToQuad(src, corners);
        var mid = id.MapPoint(new SKPoint(60, 120));
        Assert.Equal(60, mid.X, 3);
        Assert.Equal(120, mid.Y, 3);
    }

    [Fact]
    public void PerspectiveDrag_MovesNeighborsSymmetrically()
    {
        var start = QuadGeometry.Corners(new SKRect(0, 0, 100, 100));
        // 拖左上角往右下 (10, 20)：右上角往左 10、左下角往上 20，右下角不動
        var q = QuadGeometry.PerspectiveDrag(start, 0, new SKPoint(10, 20));
        Assert.Equal(new SKPoint(10, 20), q[0]);
        Assert.Equal(new SKPoint(90, 0), q[1]);
        Assert.Equal(new SKPoint(100, 100), q[2]);
        Assert.Equal(new SKPoint(0, 80), q[3]);
        Assert.True(QuadGeometry.IsConvex(q));
    }

    [Fact]
    public void DistortDrag_CornerFree_EdgeMovesBothEnds_ShiftConstrains()
    {
        var start = QuadGeometry.Corners(new SKRect(0, 0, 100, 100));
        var corner = QuadGeometry.DistortDrag(start, 2, new SKPoint(-30, 15), constrain: false);
        Assert.Equal(new SKPoint(70, 115), corner[2]);
        Assert.Equal(start[0], corner[0]);
        Assert.Equal(start[1], corner[1]);
        Assert.Equal(start[3], corner[3]);

        var edge = QuadGeometry.DistortDrag(start, 4, new SKPoint(5, -10), constrain: false); // 上邊
        Assert.Equal(new SKPoint(5, -10), edge[0]);
        Assert.Equal(new SKPoint(105, -10), edge[1]);

        var constrained = QuadGeometry.DistortDrag(start, 1, new SKPoint(30, 4), constrain: true);
        Assert.Equal(new SKPoint(130, 0), constrained[1]); // 只沿水平軸
    }

    [Fact]
    public void IsConvex_RejectsFoldedQuad()
    {
        SKPoint[] folded = [new(0, 0), new(100, 0), new(0, 100), new(100, 100)]; // 自交（蝴蝶結）
        Assert.False(QuadGeometry.IsConvex(folded));
        SKPoint[] concave = [new(0, 0), new(100, 0), new(40, 40), new(0, 100)];
        Assert.False(QuadGeometry.IsConvex(concave));
    }

    [Fact]
    public void QuadSession_DistortCorner_StampsPixelsUnderHomography_SingleUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 300, 300), new SKColor(255, 0, 0));
        }
        session.ActiveTool = session.Move;
        session.Move.TransformMode = TransformMode.Perspective;
        session.RefreshSelectionHandles();

        var t = session.BeginTransform();
        Assert.NotNull(t);
        Assert.True(t!.CanUseQuad);
        Assert.True(t.EnterQuadMode());
        Assert.NotNull(t.Quad);
        Assert.True(t.IsIdentity);
        session.RefreshSelectionHandles();
        Assert.NotNull(session.SelectionHandlesQuad);

        // 把右下角往右下拉 100px：仍是凸四邊形
        var quad = QuadGeometry.DistortDrag(t.Quad!, 2, new SKPoint(100, 100), constrain: false);
        Assert.True(t.SetQuad(quad));
        Assert.False(t.IsIdentity);
        Assert.True(t.IsQuadChanged);
        t.Apply(preview: false);

        // 原本 (299,299) 的紅色角落應該映射到 (399,399) 附近
        var mapped = t.Matrix.MapPoint(new SKPoint(299.5f, 299.5f));
        Assert.InRange(mapped.X, 398f, 400f);
        Assert.InRange(mapped.Y, 398f, 400f);
        var exact = t.Matrix.MapPoint(new SKPoint(300f, 300f));
        Assert.Equal(400f, exact.X, 2);
        Assert.Equal(400f, exact.Y, 2);

        var undoBefore = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count);
        Assert.Null(session.Transform);

        var c = LayerPx(layer, 395, 395);
        Assert.True(c.Alpha > 200 && c.Red > 200, $"expected red at mapped corner, got {c}");
        Assert.Equal(0, LayerPx(layer, 105, 395).Alpha); // 左下角沒動：仍在 (100..300) 之外的地方是透明

        session.Undo();
        Assert.Equal(0, LayerPx(layer, 395, 395).Alpha);
        Assert.Equal(255, LayerPx(layer, 299, 299).Alpha);
    }

    [Fact]
    public void QuadSession_BackToStart_RestoresOriginalBitwise_NoHistory()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(20, 20, 120, 120), new SKColor(0, 128, 255, 200));
        }
        session.ActiveTool = session.Move;
        session.Move.TransformMode = TransformMode.Perspective;
        session.RefreshSelectionHandles();
        var original = LayerPx(layer, 60, 60); // 預乘儲存，讀回值以它為準

        var t = session.BeginTransform()!;
        Assert.True(t.EnterQuadMode());
        var start = t.Quad!;
        Assert.True(t.SetQuad(QuadGeometry.PerspectiveDrag(start, 0, new SKPoint(15, 5))));
        t.Apply(preview: true);
        Assert.True(t.SetQuad(start)); // 拉回原位
        Assert.True(t.IsIdentity);
        t.Apply(preview: false);

        var undoBefore = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
        Assert.Equal(original, LayerPx(layer, 60, 60));
        Assert.Equal(0, LayerPx(layer, 130, 60).Alpha);
    }

    [Fact]
    public void QuadSession_PureTranslation_UsesOffsetNotResample()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(20, 20, 60, 60), new SKColor(10, 20, 30));
        }
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        var t = session.BeginTransform()!;
        Assert.True(t.EnterQuadMode());
        Assert.True(t.SetQuad(QuadGeometry.Translated(t.Quad!, 7, -3)));
        t.Apply(preview: true);
        Assert.Equal(new SKPointI(7, -3), t.OffsetDelta); // 整數平移走 Offset，像素沒重取樣
        Assert.Equal(new SKPointI(7, -3), layer.Offset);
        session.CommitTransform();
        Assert.Equal(new SKColor(10, 20, 30), LayerPx(layer, 30, 20));
    }

    [Fact]
    public void QuadMode_RefusedForTextLayer()
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
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        var t = session.BeginTransform();
        Assert.NotNull(t);
        Assert.False(t!.CanUseQuad);
        Assert.False(t.EnterQuadMode());
        Assert.Null(t.Quad);
        session.CancelTransform();
    }

    [Fact]
    public void HandleDrag_InPerspectiveMode_EntersQuadAndDragsCorner()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 300, 300), new SKColor(0, 255, 0));
        }
        session.ActiveTool = session.Move;
        session.Move.TransformMode = TransformMode.Perspective;
        session.RefreshSelectionHandles();
        var frame = session.SelectionHandles!.Value;

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(frame.Left, frame.Top), 6f));
        Assert.NotNull(session.Transform);
        Assert.NotNull(session.Transform!.Quad);

        handles.Continue(session, new SKPoint(frame.Left + 40, frame.Top + 10), ToolModifiers.None);
        var q = session.Transform.Quad!;
        Assert.Equal(frame.Left + 40, q[0].X, 1);
        Assert.Equal(frame.Top + 10, q[0].Y, 1);
        Assert.Equal(frame.Right - 40, q[1].X, 1); // 右上對稱往左
        Assert.Equal(frame.Bottom - 10, q[3].Y, 1); // 左下對稱往上
        handles.End(session);

        Assert.True(session.CanResetTransform);
        Assert.True(session.ResetTransform());
        Assert.True(session.Transform.IsIdentity);
        session.CommitTransform();
    }
}
