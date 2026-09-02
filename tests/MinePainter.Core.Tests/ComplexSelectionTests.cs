using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MinePainter.Core.Tests;

/// <summary>多區域與不規則選取搭配移動工具的行為。</summary>
public class ComplexSelectionTests(ITestOutputHelper output)
{
    private static SKColor Px(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var off = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[off + 2], s[off + 1], s[off + 0], s[off + 3]);
    }

    [Fact]
    public void TwoDisjointRegions_MaskKeepsBothAndGapStaysUnselected()
    {
        var docBounds = new SKRectI(0, 0, 400, 400);
        using var p1 = new SKPath();
        p1.AddRect(SKRect.Create(50, 50, 60, 60));
        using var p2 = new SKPath();
        p2.AddRect(SKRect.Create(250, 250, 60, 60));

        var a = SelectionMask.FromPath(p1, docBounds);
        var b = SelectionMask.FromPath(p2, docBounds);
        var combined = SelectionMask.Combine(a, b, SelectionCombineMode.Add)!;

        Assert.Equal(255, combined.CoverageAt(80, 80));    // 區域 A
        Assert.Equal(255, combined.CoverageAt(280, 280));  // 區域 B
        Assert.Equal(0, combined.CoverageAt(180, 180));    // 中間的空隙不該被選到
    }

    [Fact]
    public void TwoDisjointRegions_TransformKeepsGapUnselected()
    {
        // 縮放/移動多區域選取後，中間的空隙仍然不能變成被選取
        var docBounds = new SKRectI(0, 0, 400, 400);
        using var p1 = new SKPath();
        p1.AddRect(SKRect.Create(50, 50, 60, 60));
        using var p2 = new SKPath();
        p2.AddRect(SKRect.Create(250, 250, 60, 60));

        var combined = SelectionMask.Combine(
            SelectionMask.FromPath(p1, docBounds),
            SelectionMask.FromPath(p2, docBounds),
            SelectionCombineMode.Add)!;

        var src = combined.Bounds;
        output.WriteLine($"原始 bounds: {src}");

        // 原地變換（不動），結果應該完全一樣
        var same = combined.TransformedTo(new SKRect(src.Left, src.Top, src.Right, src.Bottom), docBounds);
        Assert.NotNull(same);
        Assert.Equal(255, same!.CoverageAt(80, 80));
        Assert.Equal(255, same.CoverageAt(280, 280));
        Assert.Equal(0, same.CoverageAt(180, 180)); // 空隙不能被填滿
    }

    [Fact]
    public void LassoTriangle_TransformKeepsShape()
    {
        // 不規則（三角形）選取：變換後仍要是三角形，不能變成外接矩形
        var docBounds = new SKRectI(0, 0, 400, 400);
        using var tri = new SKPath();
        tri.MoveTo(200, 50);
        tri.LineTo(350, 300);
        tri.LineTo(50, 300);
        tri.Close();

        var mask = SelectionMask.FromPath(tri, docBounds);
        Assert.Equal(255, mask.CoverageAt(200, 250));  // 三角形內
        Assert.Equal(0, mask.CoverageAt(60, 80));      // 左上角在三角形外

        var src = mask.Bounds;
        var moved = mask.TransformedTo(new SKRect(src.Left, src.Top, src.Right, src.Bottom), docBounds);
        Assert.NotNull(moved);
        Assert.Equal(255, moved!.CoverageAt(200, 250));
        Assert.Equal(0, moved.CoverageAt(60, 80));     // 變換後左上角仍在外面
    }

    [Fact]
    public void MagicWandSelection_TransformKeepsShape()
    {
        // 魔術棒的輪廓是從遮罩重建的（marching squares 式），變換後要維持形狀
        using var doc = ImageCodec.CreateBlankDocument(400, 400, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));

        var mask = FloodFiller.Fill(layer, new SKPointI(150, 150), 0, doc.Bounds);
        Assert.Equal(255, mask.CoverageAt(150, 150));
        Assert.Equal(0, mask.CoverageAt(350, 350));

        var src = mask.Bounds;
        var moved = mask.TransformedTo(new SKRect(src.Left, src.Top, src.Right, src.Bottom), doc.Bounds);
        Assert.NotNull(moved);
        Assert.Equal(255, moved!.CoverageAt(150, 150));
        Assert.Equal(0, moved.CoverageAt(350, 350));
    }

    [Fact]
    public void MoveTool_TwoDisjointRegions_MovesBothAndLeavesGapAlone()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(50, 50, 110, 110), new SKColor(255, 0, 0));
        layer.Surface.Fill(new SKRectI(250, 250, 310, 310), new SKColor(0, 0, 255));
        layer.Surface.Fill(new SKRectI(180, 180, 200, 200), new SKColor(0, 255, 0)); // 空隙裡的內容

        var docBounds = session.Document.Bounds;
        using var p1 = new SKPath();
        p1.AddRect(SKRect.Create(50, 50, 60, 60));
        using var p2 = new SKPath();
        p2.AddRect(SKRect.Create(250, 250, 60, 60));
        var combined = SelectionMask.Combine(
            SelectionMask.FromPath(p1, docBounds),
            SelectionMask.FromPath(p2, docBounds),
            SelectionCombineMode.Add)!;
        SelectionCommands.SetSelection(session, combined, "多區域選取");

        // 提起 → 往右下移 20,20 → 落地
        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(80, 80), 1f), session);
        Assert.NotNull(session.Floating);

        // 空隙裡的綠色不該被提起（原地要還在）
        Assert.Equal(new SKColor(0, 255, 0), Px(layer, 190, 190));

        session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        session.CommitFloating();

        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 100, 100));  // A 搬到新位置
        Assert.Equal(new SKColor(0, 0, 255), Px(layer, 300, 300));  // B 也搬了
        Assert.Equal(new SKColor(0, 255, 0), Px(layer, 190, 190));  // 空隙內容原封不動
    }

    [Fact]
    public void MoveTool_LassoSelection_OnlyMovesInsideShape()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, 400, 400), new SKColor(255, 0, 0));

        using var tri = new SKPath();
        tri.MoveTo(200, 50);
        tri.LineTo(350, 300);
        tri.LineTo(50, 300);
        tri.Close();
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(tri, session.Document.Bounds), "套索");

        session.Move.OnPointerDown(new ToolPointerEvent(new SKPoint(200, 250), 1f), session);
        Assert.NotNull(session.Floating);

        // 三角形外的角落像素不該被挖走
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 60, 80));
        // 三角形內被挖空
        Assert.Equal(0, Px(layer, 200, 250).Alpha);

        session.Move.OnPointerUp(new ToolPointerEvent(new SKPoint(200, 250), 1f), session);
        session.CommitFloating();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 200, 250)); // 原地落回
    }
}
