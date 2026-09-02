using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class DocumentCommandTests
{
    private static SKColor Px(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x - layer.Offset.X, y - layer.Offset.Y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var off = ((y - layer.Offset.Y - rect.Top) * Tile.Size + (x - layer.Offset.X - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[off + 2], s[off + 1], s[off + 0], s[off + 3]);
    }

    /// <summary>200×100 的文件，左上角有紅點方便辨識方向。</summary>
    private static EditorSession OrientedSession()
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 100, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, 20, 20), new SKColor(255, 0, 0));   // 左上：紅
        layer.Surface.Fill(new SKRectI(180, 0, 200, 20), new SKColor(0, 255, 0)); // 右上：綠
        return session;
    }

    [Fact]
    public void FlipHorizontal_MirrorsAndIsSelfInverse()
    {
        using var session = OrientedSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        DocumentCommands.ApplyGeometry(session, GeometryOp.FlipHorizontal, "水平翻轉");

        Assert.Equal(200, session.Document.Width); // 尺寸不變
        Assert.Equal(new SKColor(0, 255, 0), Px(layer, 10, 10));  // 綠跑到左邊
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 190, 10)); // 紅跑到右邊

        session.History.Undo();
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 10, 10));  // 翻回來
    }

    [Fact]
    public void Rotate90CW_SwapsDimensionsAndUndoes()
    {
        using var session = OrientedSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        DocumentCommands.ApplyGeometry(session, GeometryOp.Rotate90CW, "順時針旋轉 90°");

        Assert.Equal(100, session.Document.Width);  // 寬高互換
        Assert.Equal(200, session.Document.Height);
        // 原本左上的紅，順時針轉後跑到右上
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 90, 10));

        session.History.Undo();
        Assert.Equal(200, session.Document.Width);
        Assert.Equal(100, session.Document.Height);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 10, 10));
    }

    [Fact]
    public void Rotate90_TwiceEqualsRotate180()
    {
        using var a = OrientedSession();
        using var b = OrientedSession();

        DocumentCommands.ApplyGeometry(a, GeometryOp.Rotate90CW, "轉");
        DocumentCommands.ApplyGeometry(a, GeometryOp.Rotate90CW, "轉");
        DocumentCommands.ApplyGeometry(b, GeometryOp.Rotate180, "轉");

        var la = (RasterLayer)a.Document.ActiveLayer!;
        var lb = (RasterLayer)b.Document.ActiveLayer!;
        Assert.Equal(a.Document.Width, b.Document.Width);
        Assert.Equal(Px(la, 10, 10), Px(lb, 10, 10));
        Assert.Equal(Px(la, 190, 90), Px(lb, 190, 90));
    }

    [Fact]
    public void Geometry_TransformsSelectionToo()
    {
        using var session = OrientedSession();
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, 40, 40)); // 左上角
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");

        DocumentCommands.ApplyGeometry(session, GeometryOp.FlipHorizontal, "水平翻轉");

        // 選取範圍跟著鏡射到右邊（Pinta 是直接丟棄，我們保留）
        Assert.NotNull(session.Selection);
        Assert.Equal(255, session.Selection!.CoverageAt(180, 20));
        Assert.Equal(0, session.Selection.CoverageAt(20, 20));
    }

    [Fact]
    public void CropToSelection_ResizesDocumentAndUndoes()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(50, 50, 150, 150), new SKColor(0, 0, 255));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(50, 50, 100, 100));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");

        DocumentCommands.CropToSelection(session);

        Assert.Equal(100, session.Document.Width);
        Assert.Equal(100, session.Document.Height);
        Assert.Equal(new SKColor(0, 0, 255), Px(layer, 50, 50)); // 內容搬到新原點
        Assert.Null(session.Selection);

        session.History.Undo();
        Assert.Equal(200, session.Document.Width);
        Assert.Equal(new SKColor(0, 0, 255), Px(layer, 100, 100));
    }

    [Fact]
    public void SelectAll_AndInvert()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));

        EditCommands.SelectAll(session);
        Assert.Equal(255, session.Selection!.CoverageAt(100, 100));
        Assert.Equal(255, session.Selection.CoverageAt(5, 5));

        // 反轉全選 → 什麼都不選
        EditCommands.InvertSelection(session);
        Assert.True(session.Selection is null or { IsEmpty: true });
    }

    [Fact]
    public void InvertSelection_FlipsInsideOutside()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));
        using var path = new SKPath();
        path.AddRect(SKRect.Create(50, 50, 100, 100));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");

        EditCommands.InvertSelection(session);

        Assert.Equal(0, session.Selection!.CoverageAt(100, 100));  // 原本選中的變成沒選
        Assert.Equal(255, session.Selection.CoverageAt(10, 10));   // 原本沒選的變成選中
    }

    [Fact]
    public void EraseAndFillSelection()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        using var path = new SKPath();
        path.AddRect(SKRect.Create(50, 50, 100, 100));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");

        EditCommands.EraseSelection(session);
        Assert.Equal(0, Px(layer, 100, 100).Alpha);           // 選取內清空
        Assert.Equal(SKColors.White, Px(layer, 10, 10));      // 選取外不動

        session.Foreground = new SKColor(255, 0, 0);
        EditCommands.FillSelection(session);
        Assert.Equal(new SKColor(255, 0, 0), Px(layer, 100, 100));
        Assert.Equal(SKColors.White, Px(layer, 10, 10));

        session.History.Undo(); // 還原填色
        Assert.Equal(0, Px(layer, 100, 100).Alpha);
    }

    [Fact]
    public void DuplicateLayer_CopiesPixelsAndProperties()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(100, 100, SKColors.White));
        var source = (RasterLayer)session.Document.ActiveLayer!;
        source.Opacity = 0.5f;
        source.BlendMode = BlendMode.Multiply;

        var copy = LayerCommands.DuplicateLayer(session.Document, session.History, source);

        Assert.NotNull(copy);
        Assert.Equal(2, session.Document.Root.Children.Count);
        Assert.Equal(0.5f, copy!.Opacity, 2);
        Assert.Equal(BlendMode.Multiply, copy.BlendMode); // Pinta 漏了這個，我們沒漏
        Assert.Equal(SKColors.White, Px(copy, 50, 50));

        session.History.Undo();
        Assert.Single(session.Document.Root.Children);
    }

    [Fact]
    public void MergeLayerDown_BakesUpperOpacityIntoLower()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(100, 100, SKColors.White));
        var bottom = (RasterLayer)session.Document.ActiveLayer!;
        var top = new RasterLayer { Name = "上層", Opacity = 0.5f };
        top.Surface.Fill(new SKRectI(0, 0, 100, 100), SKColors.Black);
        lock (session.Document.SyncRoot) session.Document.Root.Add(top);

        Assert.True(LayerCommands.MergeLayerDown(session.Document, session.History, top));

        Assert.Single(session.Document.Root.Children);
        var px = Px(bottom, 50, 50);
        Assert.InRange(px.Red, 125, 130); // 白底 + 50% 黑 = 中灰

        session.History.Undo();
        Assert.Equal(2, session.Document.Root.Children.Count);
        Assert.Equal(SKColors.White, Px(bottom, 50, 50));
    }

    [Fact]
    public void Flatten_CollapsesToSingleLayer()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(100, 100, SKColors.White));
        var top = new RasterLayer { Name = "上層" };
        top.Surface.Fill(new SKRectI(0, 0, 50, 50), new SKColor(255, 0, 0));
        lock (session.Document.SyncRoot) session.Document.Root.Add(top);

        Assert.True(LayerCommands.Flatten(session.Document, session.History));

        Assert.Single(session.Document.Root.Children);
        var flat = (RasterLayer)session.Document.Root.Children[0];
        Assert.Equal(new SKColor(255, 0, 0), Px(flat, 25, 25));
        Assert.Equal(SKColors.White, Px(flat, 75, 75));

        session.History.Undo();
        Assert.Equal(2, session.Document.Root.Children.Count);
    }
}
