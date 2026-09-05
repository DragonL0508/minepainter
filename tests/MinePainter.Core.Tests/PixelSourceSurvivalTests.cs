using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 快速模式的命脈是圖層的「原始高清來源」：常見操作之後它要還在、還對得上，
/// 輸出大圖時才是從原圖重畫。這裡逐一守住翻轉、裁切、清除選取、複製圖層、貼上縮小。
/// </summary>
public class PixelSourceSurvivalTests
{
    private const int Original = 512, Proxy = 128;

    /// <summary>原圖：左上四分之一紅、其餘白（方向與位置都看得出來）。</summary>
    private static SKBitmap Original512()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Original, Original, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(SKRect.Create(0, 0, Original / 2, Original / 2), paint);
        canvas.Flush();
        return bitmap;
    }

    private static (EditorSession Session, RasterLayer Layer) ProxyDoc()
    {
        using var original = Original512();
        var session = new EditorSession(ImageCodec.CreateBlankDocument(Proxy, Proxy, SKColors.Transparent));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot)
        {
            using var small = original.Resize(new SKImageInfo(Proxy, Proxy, SKColorType.Bgra8888, SKAlphaType.Premul),
                SKFilterQuality.High);
            using var pixmap = small.PeekPixels();
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
            var scale = Proxy / (float)Original;
            layer.SetPixelSource(new LayerPixelSource(SKImage.FromBitmap(original),
                new SKRectI(0, 0, Original, Original), SKMatrix.CreateScale(scale, scale), SKPointI.Empty,
                SKRect.Create(0, 0, Proxy, Proxy), 0f, new SKSize(Original, Original), layer.Surface.Revision));
        }
        doc.SetOutputSize(Original, Original);
        return (session, layer);
    }

    private static SKColor OutputPixel(Document doc, int x, int y)
    {
        using var output = OutputRender.Render(doc);
        using var bitmap = SKBitmap.FromImage(output);
        return bitmap.GetPixel(x, y);
    }

    private static SKColor LayerPixel(RasterLayer layer, int x, int y)
    {
        using var bitmap = ImageCommands.ReadRegion(layer.Surface, new SKRectI(x, y, x + 1, y + 1));
        return bitmap.GetPixel(0, 0);
    }

    /// <summary>輸出的某點與圖層代理上的對應點顏色一致（放大 4 倍）。</summary>
    private static void AssertOutputMatchesLayer(Document doc, RasterLayer layer, int px, int py)
    {
        var expected = LayerPixel(layer, px, py);
        var actual = OutputPixel(doc, px * 4 + 2, py * 4 + 2);
        Assert.Equal(expected.Alpha, actual.Alpha);
        if (expected.Alpha > 0) Assert.Equal(expected.Red > 128, actual.Red > 128);
    }

    [Theory]
    [InlineData(GeometryOp.FlipHorizontal)]
    [InlineData(GeometryOp.FlipVertical)]
    [InlineData(GeometryOp.Rotate90CW)]
    [InlineData(GeometryOp.Rotate90CCW)]
    [InlineData(GeometryOp.Rotate180)]
    public void 翻轉旋轉之後_原始來源跟著轉_輸出對得上(GeometryOp op)
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            var doc = session.Document;
            var before = layer.ValidPixelSource;
            DocumentCommands.ApplyGeometry(session, op, "翻轉");

            var after = layer.ValidPixelSource;
            Assert.NotNull(after);
            Assert.Same(before!.Pixels, after.Pixels); // 同一張原圖，只是矩陣多串一段

            // 四個角落：紅塊轉到哪，輸出就得紅到哪；其餘是白
            AssertOutputMatchesLayer(doc, layer, 16, 16);
            AssertOutputMatchesLayer(doc, layer, 110, 16);
            AssertOutputMatchesLayer(doc, layer, 16, 110);
            AssertOutputMatchesLayer(doc, layer, 110, 110);

            session.History.Undo();
            Assert.NotNull(layer.ValidPixelSource);
            AssertOutputMatchesLayer(doc, layer, 16, 16);
            Assert.True(LayerPixel(layer, 16, 16).Red > 200 && LayerPixel(layer, 16, 16).Green < 50);
        }
    }

    [Fact]
    public void 單一圖層翻轉_原始來源跟著轉()
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            ImageCommands.FlipLayer(session, layer, GeometryOp.FlipHorizontal, "水平翻轉圖層");
            Assert.NotNull(layer.ValidPixelSource);
            Assert.True(OutputPixel(session.Document, Original - 20, 20).Red > 200);
            Assert.True(OutputPixel(session.Document, Original - 20, 20).Green < 50);
            Assert.True(OutputPixel(session.Document, 20, 20).Green > 200); // 左上變白
        }
    }

    [Fact]
    public void 裁切到選取_原始來源裁掉範圍外並平移()
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            var doc = session.Document;
            // 選右半邊的一個圓（紅塊完全在外）
            using var path = new SKPath();
            path.AddCircle(96, 64, 24);
            session.ApplySelection(SelectionMask.FromPath(path, doc.Bounds));
            DocumentCommands.CropToSelection(session);

            Assert.Equal(48, doc.Width);
            var source = layer.ValidPixelSource;
            Assert.NotNull(source);

            // 輸出 4 倍（192×192）：圓心白、圓外透明、沒有任何紅
            var center = OutputPixel(doc, 96, 96);
            Assert.Equal(255, center.Alpha);
            Assert.True(center.Green > 200);
            Assert.Equal(0, OutputPixel(doc, 4, 4).Alpha);

            session.History.Undo();
            Assert.Equal(Proxy, doc.Width);
            Assert.NotNull(layer.ValidPixelSource);
            Assert.True(OutputPixel(doc, 20, 20).Red > 200 && OutputPixel(doc, 20, 20).Green < 50);
        }
    }

    [Fact]
    public void 清除選取範圍_原始來源同一塊挖掉_可復原()
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            var doc = session.Document;
            var before = layer.ValidPixelSource;
            using var path = new SKPath();
            path.AddRect(SKRect.Create(0, 0, 64, 64)); // 整塊紅
            session.ApplySelection(SelectionMask.FromPath(path, doc.Bounds));
            EditCommands.EraseSelection(session);

            var after = layer.ValidPixelSource;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.Equal(0, OutputPixel(doc, 100, 100).Alpha);   // 紅塊沒了
            Assert.Equal(255, OutputPixel(doc, 400, 400).Alpha); // 白的還在
            // 邊界銳利：從原圖重畫的邊在 256 附近一格內過渡，不是 128 放大來的軟邊
            Assert.Equal(0, OutputPixel(doc, 250, 100).Alpha);
            Assert.Equal(255, OutputPixel(doc, 262, 100).Alpha);

            session.History.Undo();
            Assert.Same(before, layer.ValidPixelSource);
            Assert.True(OutputPixel(doc, 100, 100).Red > 200);
        }
    }

    [Fact]
    public void 填滿選取範圍_原始來源作廢()
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            EditCommands.FillSelection(session);
            Assert.Null(layer.ValidPixelSource);
        }
    }

    [Fact]
    public void 複製圖層_複本有自己的原始來源()
    {
        var (session, layer) = ProxyDoc();
        using (session)
        {
            var doc = session.Document;
            var copy = LayerCommands.DuplicateLayer(doc, session.History, layer);
            Assert.NotNull(copy);
            var source = copy.ValidPixelSource;
            Assert.NotNull(source);
            Assert.NotSame(layer.ValidPixelSource!.Pixels, source.Pixels);
            Assert.Equal(layer.ValidPixelSource.Bounds, source.Bounds);
        }
    }

    [Fact]
    public void 貼上大圖縮小落地_原圖留成原始來源()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(Proxy, Proxy, SKColors.Transparent));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        doc.SetOutputSize(Original, Original);

        using var original = Original512();
        Assert.True(session.PasteImage(SKImage.FromBitmap(original), SKPointI.Empty));
        session.Floating!.TargetRect = SKRect.Create(0, 0, Proxy, Proxy); // 縮到畫布大小
        session.CommitFloating();

        var source = layer.ValidPixelSource;
        Assert.NotNull(source);
        Assert.Equal(Original, source.Bounds.Width);
        Assert.True(OutputPixel(doc, 100, 100).Red > 200 && OutputPixel(doc, 100, 100).Green < 50);
        // 邊界銳利
        Assert.True(OutputPixel(doc, 250, 100).Green < 50);
        Assert.True(OutputPixel(doc, 262, 100).Green > 200);

        session.History.Undo();
        Assert.Null(layer.ValidPixelSource);
        session.History.Redo();
        Assert.Same(source, layer.ValidPixelSource);
    }
}
