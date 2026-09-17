using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 2026-09-17 使用者回報：「放大後去背，畫布外面沒被清除掉的地方也會留著，然後影響到後面加外框或是光暈，
/// 用 delete 還清不掉」。選取永遠夾在畫布內，清除要能延伸到畫布外：選取貼到哪一邊，那一邊外面就一起清。
/// </summary>
public class EraseBeyondCanvasTests
{
    private static (EditorSession Session, RasterLayer Layer) OversizedLayer()
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(-150, -150, 350, 350), SKColors.Red); // 四邊都超出畫布
        layer.InvalidateAll();
        return (session, layer);
    }

    private static SelectionMask Rect(EditorSession session, SKRect r)
    {
        using var path = new SKPath();
        path.AddRect(r);
        return SelectionMask.FromPath(path, session.Document.Bounds);
    }

    private static byte AlphaAt(RasterLayer layer, int x, int y)
    {
        using var bmp = ImageCommands.ReadRegion(layer.Surface, new SKRectI(x, y, x + 1, y + 1));
        return bmp.GetPixel(0, 0).Alpha;
    }

    [Fact]
    public void 沒有選取_清掉整層_包含畫布外()
    {
        var (session, layer) = OversizedLayer();
        EditCommands.EraseSelection(session);
        lock (session.Document.SyncRoot)
            Assert.Equal(0, layer.Surface.ExactContentBounds().Width);

        session.Undo();
        Assert.Equal(255, AlphaAt(layer, -100, -100)); // 畫布外的也要還原得回來
        session.Dispose();
    }

    [Fact]
    public void 全選後清除_畫布外也清掉()
    {
        var (session, layer) = OversizedLayer();
        EditCommands.SelectAll(session);
        EditCommands.EraseSelection(session);
        lock (session.Document.SyncRoot)
            Assert.Equal(0, layer.Surface.ExactContentBounds().Width);
        session.Dispose();
    }

    [Fact]
    public void 選取貼著右邊_只清右邊外面那一段()
    {
        var (session, layer) = OversizedLayer();
        session.Selection = Rect(session, new SKRect(150, 50, 200, 100)); // 貼右緣、y 50..100
        EditCommands.EraseSelection(session);

        Assert.Equal(0, AlphaAt(layer, 170, 70));    // 畫布內、選取內
        Assert.Equal(0, AlphaAt(layer, 300, 70));    // 右邊畫布外、同一段 y：一起清
        Assert.Equal(255, AlphaAt(layer, 300, 120)); // 右邊畫布外、選取的 y 範圍之外：留著
        Assert.Equal(255, AlphaAt(layer, -100, 70)); // 左邊畫布外：選取沒貼到左緣，留著
        Assert.Equal(255, AlphaAt(layer, 100, 70));  // 畫布內、選取外
        session.Dispose();
    }

    [Fact]
    public void 選取沒碰到畫布邊_畫布外完全不動()
    {
        var (session, layer) = OversizedLayer();
        session.Selection = Rect(session, new SKRect(50, 50, 100, 100));
        EditCommands.EraseSelection(session);
        Assert.Equal(0, AlphaAt(layer, 70, 70));
        Assert.Equal(255, AlphaAt(layer, 300, 70));
        Assert.Equal(255, AlphaAt(layer, -100, -100));
        session.Dispose();
    }
}
