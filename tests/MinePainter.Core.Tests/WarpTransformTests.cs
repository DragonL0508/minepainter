using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>扭曲（彎曲）變形：貝茲網格、session 整合、文字圖層自動平面化。</summary>
public class WarpTransformTests
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
    public void FlatMesh_IsIdentity_CornersMapExactly()
    {
        var frame = new SKRect(10, 20, 110, 220);
        var mesh = WarpMesh.Flat(frame);
        Assert.True(mesh.IsFlat);
        var mid = mesh.Evaluate(0.5f, 0.5f);
        Assert.Equal(60, mid.X, 3);
        Assert.Equal(120, mid.Y, 3);
        var c = mesh.Evaluate(1, 1);
        Assert.Equal(110, c.X, 3);
        Assert.Equal(220, c.Y, 3);

        var dragged = WarpMesh.Drag(mesh, 0, new SKPoint(5, 5)); // 角點帶著兩個把手
        Assert.Equal(new SKPoint(15, 25), dragged.Points[0]);
        Assert.Equal(mesh.Points[1].X + 5, dragged.Points[1].X, 3);
        Assert.Equal(mesh.Points[4].Y + 5, dragged.Points[4].Y, 3);
        Assert.Equal(mesh.Points[5], dragged.Points[5]); // 內點不動
        Assert.False(dragged.IsFlat);
    }

    [Fact]
    public void WarpSession_BendsPixels_SingleUndo_ResetRestores()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 300, 300), new SKColor(255, 0, 0));
        }
        session.ActiveTool = session.Move;
        session.Move.TransformMode = TransformMode.Warp;
        session.RefreshSelectionHandles();

        var t = session.EnterTransformMode(TransformMode.Warp);
        Assert.NotNull(t);
        Assert.NotNull(t!.Warp);
        Assert.True(t.IsIdentity);
        Assert.NotNull(session.SelectionHandlesWarp);

        // 把上邊中間兩個把手往下壓 80px：上緣變成向下彎的弧，原本 (200,105) 的紅色應該不在了
        var mesh = WarpMesh.Drag(t.Warp!, 1, new SKPoint(0, 80));
        mesh = WarpMesh.Drag(mesh, 2, new SKPoint(0, 80));
        Assert.True(t.SetWarp(mesh));
        Assert.False(t.IsIdentity);
        t.Apply(preview: false);

        var undoBefore = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count);
        Assert.Equal(0, LayerPx(layer, 200, 105).Alpha);        // 上緣中段被壓下去了
        Assert.True(LayerPx(layer, 200, 200).Alpha > 200);      // 中間仍是紅
        Assert.True(LayerPx(layer, 103, 250).Alpha > 200);      // 左邊沒動

        session.Undo();
        Assert.Equal(255, LayerPx(layer, 200, 105).Alpha);
    }

    [Fact]
    public void WarpSession_BackToFlat_IsIdentity_NoHistory()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(20, 20, 120, 120), new SKColor(0, 128, 255, 200));
        }
        session.ActiveTool = session.Move;
        var original = LayerPx(layer, 60, 60);
        var t = session.EnterTransformMode(TransformMode.Warp)!;
        Assert.True(t.SetWarp(WarpMesh.Drag(t.Warp!, 5, new SKPoint(20, 20))));
        t.Apply(preview: true);
        Assert.True(session.CanResetTransform);
        Assert.True(session.ResetTransform());
        Assert.True(t.IsIdentity);

        var undoBefore = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
        Assert.Equal(original, LayerPx(layer, 60, 60));
    }

    [Fact]
    public void TextLayer_EnterMeshMode_AutoFlattens_CancelRestoresText()
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

        var t = session.EnterTransformMode(TransformMode.Perspective);
        Assert.NotNull(t);
        Assert.NotNull(t!.Quad);
        Assert.False(text.HasElements);              // 已自動平面化
        Assert.True(text.Surface.TileCount > 0);     // 文字變成像素
        Assert.Equal("平面化文字", session.History.UndoLabel);

        session.CancelTransform();                   // Esc：連平面化一起退回
        Assert.True(text.HasElements);
        Assert.Null(session.Transform);

        // 再來一次並落地：平面化與變形各記一步
        var t2 = session.EnterTransformMode(TransformMode.Warp)!;
        Assert.NotNull(t2.Warp);
        Assert.True(t2.SetWarp(WarpMesh.Drag(t2.Warp!, 0, new SKPoint(10, 10))));
        t2.Apply(preview: false);
        var before = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(before + 1, session.History.UndoStack.Count);
        Assert.False(text.HasElements);
        Assert.False(text.ViolatesTextLayerInvariant);
    }

    [Fact]
    public void HandleDrag_InWarpMode_DragsControlPoint()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(100, 100, 300, 300), new SKColor(0, 255, 0));
        }
        session.ActiveTool = session.Move;
        session.Move.TransformMode = TransformMode.Warp;
        session.RefreshSelectionHandles();
        var frame = session.SelectionHandles!.Value;

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(frame.Left, frame.Top), 6f));
        Assert.NotNull(session.Transform?.Warp);
        handles.Continue(session, new SKPoint(frame.Left + 30, frame.Top + 20), ToolModifiers.None);
        var pts = session.Transform!.Warp!.Points;
        Assert.Equal(frame.Left + 30, pts[0].X, 1);
        Assert.Equal(frame.Top + 20, pts[0].Y, 1);
        Assert.Equal(frame.Top + 20, pts[1].Y, 1); // 切線把手跟著角點
        handles.End(session);
        session.CommitTransform();
    }
}

public class TransformModeHandlePreviewTests
{
    [Fact]
    public void SwitchingMode_WithoutSession_ShowsModeHandles_AndDragStartsSession()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(100, 100, 300, 300), new SKColor(0, 255, 0));
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        Assert.Null(session.SelectionHandlesQuad);
        Assert.Null(session.SelectionHandlesWarp);

        session.Move.TransformMode = TransformMode.Perspective;
        session.RefreshSelectionHandles();
        Assert.Null(session.Transform);                        // 還沒開 session
        Assert.NotNull(session.SelectionHandlesQuad);           // 但已是 4 角把手
        Assert.Null(session.SelectionHandlesWarp);

        session.Move.TransformMode = TransformMode.Warp;
        session.RefreshSelectionHandles();
        Assert.NotNull(session.SelectionHandlesWarp);           // 16 控制點
        Assert.Null(session.SelectionHandlesQuad);
        Assert.Equal(16, session.SelectionHandlesWarp!.Points.Length);

        // 拖第 5 號控制點（上邊第二個把手）→ 開 session 並沿用同一個索引
        var p = session.SelectionHandlesWarp.Points[1];
        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, p, 6f));
        Assert.NotNull(session.Transform?.Warp);
        handles.Continue(session, new SKPoint(p.X, p.Y + 40), ToolModifiers.None);
        Assert.Equal(p.Y + 40, session.Transform!.Warp!.Points[1].Y, 1);
        handles.End(session);
        session.CancelTransform();

        session.Move.TransformMode = TransformMode.Free;
        session.RefreshSelectionHandles();
        Assert.Null(session.SelectionHandlesQuad);
        Assert.Null(session.SelectionHandlesWarp);
    }
}

public class TransformResetTests
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
    public void Reset_AfterCommittedScaleAndPerspective_ReturnsToOriginalSize()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 512, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();

        // 第一輪：放大兩倍落地
        var t1 = session.BeginTransform()!;
        t1.TargetRect = new SKRect(100, 100, 300, 300);
        t1.Apply(preview: false);
        session.CommitTransform();

        // 第二輪：透視拉一角落地
        var t2 = session.EnterTransformMode(TransformMode.Perspective)!;
        Assert.True(t2.IsResumed);
        Assert.True(t2.SetQuad(QuadGeometry.PerspectiveDrag(t2.Quad!, 0, new SKPoint(30, 0))));
        t2.Apply(preview: false);
        session.CommitTransform();

        // 第三輪：什麼都沒動，但重置鈕要亮（上一輪的變形也算）→ 重設回 100×100、無透視
        var t3 = session.BeginTransform()!;
        Assert.True(t3.IsResumed);
        Assert.True(session.CanResetTransform);
        Assert.True(session.ResetTransform());
        Assert.Null(t3.Quad);
        Assert.Equal(100, t3.TargetRect.Width, 1);
        Assert.Equal(100, t3.TargetRect.Height, 1);
        Assert.Equal(0f, t3.RotationDeg);
        Assert.False(session.CanResetTransform);

        var before = session.History.UndoStack.Count;
        session.CommitTransform();
        Assert.Equal(before + 1, session.History.UndoStack.Count);
        // 原始 100×100 的方塊回來了（中心維持在 200,200 附近）
        var rect = t3.TargetRect;
        Assert.True(LayerPx(layer, (int)rect.MidX, (int)rect.MidY).Alpha > 200);
        Assert.Equal(0, LayerPx(layer, (int)rect.Right + 5, (int)rect.MidY).Alpha);
        Assert.Equal(0, LayerPx(layer, (int)rect.Left - 5, (int)rect.MidY).Alpha);
    }
}
