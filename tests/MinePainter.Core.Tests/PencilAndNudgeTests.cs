using MinePainter.Core.IO;
using MinePainter.Core.Tiles;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>鉛筆（無反鋸齒硬邊）與方向鍵微調（選取像素）的行為。</summary>
public class PencilAndNudgeTests
{
    private static byte Alpha(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return 0;
        var rect = idx.ToPixelRect();
        var offset = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        return tile.PixelSpan[offset + 3];
    }

    private static SelectionMask RectSelection(SKRectI rect, SKRectI docBounds)
    {
        using var path = new SKPath();
        path.AddRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height));
        return SelectionMask.FromPath(path, docBounds);
    }

    private static void Stroke(EditorSession session, ITool tool, params SKPoint[] points)
    {
        tool.OnPointerDown(new ToolPointerEvent(points[0], 1f), session);
        for (var i = 1; i < points.Length; i++)
            tool.OnPointerMove(new ToolPointerEvent(points[i], 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(points[^1], 1f), session);
    }

    [Fact]
    public void Pencil_LeavesOnlyFullyOpaqueOrEmptyPixels()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        session.Foreground = SKColors.Red;

        Stroke(session, session.Pencil, new SKPoint(8.5f, 8.5f), new SKPoint(40.5f, 24.5f));

        var bounds = layer.Surface.ExactContentBounds();
        Assert.False(bounds.IsEmpty);
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var a = Alpha(layer, x, y);
                Assert.True(a is 0 or 255, $"({x},{y}) alpha = {a}：鉛筆不該有半透明邊緣");
            }
        }
    }

    [Fact]
    public void Pencil_DiagonalLine_HasNoGaps()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(32, 32, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        session.Foreground = SKColors.Black;

        // 45° —— 一般筆刷用「圓形覆蓋 + 半徑 0.5」會斷成一顆顆
        Stroke(session, session.Pencil, new SKPoint(4.5f, 4.5f), new SKPoint(20.5f, 20.5f));

        for (var i = 4; i <= 20; i++)
            Assert.True(Alpha(layer, i, i) == 255, $"({i},{i}) 沒畫到：斜線斷了");
    }

    [Fact]
    public void Brush_KeepsAntialiasedEdges()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        session.Foreground = SKColors.Blue;

        Stroke(session, session.Brush, new SKPoint(10f, 10f), new SKPoint(40f, 30f));

        var bounds = layer.Surface.ExactContentBounds();
        var partial = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
            for (var x = bounds.Left; x < bounds.Right; x++)
                if (Alpha(layer, x, y) is > 0 and < 255) partial++;

        Assert.True(partial > 0, "筆刷應該有半透明的柔邊（對照鉛筆）");
    }

    [Fact]
    public void Nudge_WithSelection_MovesSelectedPixels_NotWholeLayer()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, 40, 40), SKColors.Green); // 整層一大塊
        session.ActiveTool = session.Move;

        // 只選中左上角一小塊
        session.Selection = RectSelection(new SKRectI(4, 4, 12, 12), session.Document.Bounds);
        Assert.True(MoveTool.HasNudgeTarget(session));
        Assert.False(MoveTool.NudgePushesHistory(session)); // 提起的像素在 session 裡動，落地才記一步

        Assert.True(MoveTool.Nudge(session, 5, 0));

        Assert.NotNull(session.Floating); // 第一次按就提起成浮動內容
        Assert.Equal(9, session.Floating!.TargetRect.Left);
        Assert.Equal(SKPointI.Empty, layer.Offset); // 圖層本身沒被搬走
    }

    [Fact]
    public void Nudge_WithoutSelection_MovesLayer_AndIsNotSmoothed()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(10, 10, 20, 20), SKColors.Green);
        session.ActiveTool = session.Move;

        Assert.True(MoveTool.NudgePushesHistory(session)); // 圖層路徑每步壓一筆 undo（滑行結束時併回一步）
        Assert.True(MoveTool.Nudge(session, 3, -2));
        Assert.Equal(new SKPointI(3, -2), layer.Offset);
    }

    [Fact]
    public void CollapseLast_MergesGlideSteps_IntoOneUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(10, 10, 20, 20), SKColors.Green);
        session.ActiveTool = session.Move;

        var before = session.History.UndoStack.Count;
        for (var i = 0; i < 5; i++) MoveTool.Nudge(session, 2, 0); // 滑行的五幀
        Assert.Equal(before + 5, session.History.UndoStack.Count);
        Assert.Equal(10, layer.Offset.X);

        session.History.CollapseLast(session.History.UndoStack.Count - before);
        Assert.Equal(before + 1, session.History.UndoStack.Count); // 併成一步

        session.Undo();
        Assert.Equal(0, layer.Offset.X); // 一次回到滑行前
        session.Redo();
        Assert.Equal(10, layer.Offset.X);
    }
}
