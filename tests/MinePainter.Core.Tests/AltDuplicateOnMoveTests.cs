using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 移動工具按住 Alt 拖曳＝複製到「原圖層上面一格」的新圖層並切過去
/// （使用者 2026-09-05 明示：物件、選取像素、整層都比照）。
/// </summary>
public class AltDuplicateOnMoveTests
{
    private static SKColor Px(RasterLayer layer, int x, int y)
    {
        var lx = x - layer.Offset.X;
        var ly = y - layer.Offset.Y;
        var idx = TileIndex.FromPixel(lx, ly);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Transparent;
        var rect = idx.ToPixelRect();
        using var pm = tile.AsPixmap();
        return pm.GetPixelColor(lx - rect.Left, ly - rect.Top);
    }

    private static void Drag(EditorSession session, SKPoint from, SKPoint to, ToolModifiers mods)
    {
        session.Move.OnPointerDown(new ToolPointerEvent(from, 1f, mods), session);
        session.Move.OnPointerMove(new ToolPointerEvent(to, 1f, mods), session);
        session.Move.OnPointerUp(new ToolPointerEvent(to, 1f, mods), session);
    }

    /// <summary>圖層在父群組裡的索引（越大越上面）。</summary>
    private static int IndexOf(LayerNode node) => node.Parent!.IndexOf(node);

    [Fact]
    public void Alt_拖物件_複製到上面一格的新圖層並切過去()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 300, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement
        {
            Text = "MinePainter",
            FontSize = 48,
            Color = SKColors.Black,
            Position = new SKPoint(60, 120),
        };
        lock (doc.SyncRoot) source.AddElement(text);
        var layersBefore = doc.Root.Children.Count;

        Drag(session, new SKPoint(90, 100), new SKPoint(190, 160), ToolModifiers.Alt);

        Assert.Equal(layersBefore + 1, doc.Root.Children.Count);

        var copyLayer = (RasterLayer)doc.ActiveLayer!;
        Assert.NotSame(source, copyLayer);                       // 切到新圖層了
        Assert.Equal(IndexOf(source) + 1, IndexOf(copyLayer));   // 就在原圖層上面一格

        // 原件沒動、複本被拖走了
        var original = (TextElement)source.Elements.Single();
        var copy = (TextElement)copyLayer.Elements.Single();
        Assert.Equal(text.Position, original.Position);
        Assert.NotEqual(text.Position, copy.Position);
        Assert.NotEqual(original.Id, copy.Id);
    }

    [Fact]
    public void 沒按_Alt_拖物件就是搬走原件()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 300, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            source.AddElement(new TextElement
            {
                Text = "MinePainter",
                FontSize = 48,
                Color = SKColors.Black,
                Position = new SKPoint(60, 120),
            });
        }
        var layersBefore = doc.Root.Children.Count;

        Drag(session, new SKPoint(90, 100), new SKPoint(190, 160), ToolModifiers.None);

        Assert.Equal(layersBefore, doc.Root.Children.Count);
        Assert.Same(source, doc.ActiveLayer);
        Assert.NotEqual(new SKPoint(60, 120), ((TextElement)source.Elements.Single()).Position);
    }

    [Fact]
    public void Alt_拖選取像素_原圖層不動_複製落在新圖層()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        source.Surface.Fill(new SKRectI(50, 50, 110, 110), new SKColor(255, 0, 0));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(50, 50, 60, 60));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, doc.Bounds), "選取");
        var layersBefore = doc.Root.Children.Count;

        Drag(session, new SKPoint(80, 80), new SKPoint(280, 280), ToolModifiers.Alt);
        session.CommitFloating();

        Assert.Equal(layersBefore + 1, doc.Root.Children.Count);
        var copyLayer = (RasterLayer)doc.ActiveLayer!;
        Assert.NotSame(source, copyLayer);
        Assert.Equal(IndexOf(source) + 1, IndexOf(copyLayer));

        Assert.Equal(new SKColor(255, 0, 0), Px(source, 80, 80));      // 原處一個像素都沒少
        Assert.Equal(new SKColor(255, 0, 0), Px(copyLayer, 280, 280)); // 複本落在新位置
    }

    [Fact]
    public void Alt_拖整層_複製一層再拖複本()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        source.Surface.Fill(new SKRectI(50, 50, 110, 110), new SKColor(0, 0, 255));
        var layersBefore = doc.Root.Children.Count;

        Drag(session, new SKPoint(80, 80), new SKPoint(120, 80), ToolModifiers.Alt);

        Assert.Equal(layersBefore + 1, doc.Root.Children.Count);
        var copyLayer = (RasterLayer)doc.ActiveLayer!;
        Assert.NotSame(source, copyLayer);
        Assert.Equal(IndexOf(source) + 1, IndexOf(copyLayer));

        Assert.Equal(SKPointI.Empty, source.Offset);                  // 原圖層沒被搬
        Assert.Equal(new SKPointI(40, 0), copyLayer.Offset);          // 複本跟著游標走
    }

    [Fact]
    public void Alt_只是點一下沒拖_不會留下空圖層()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        source.Surface.Fill(new SKRectI(50, 50, 110, 110), new SKColor(255, 0, 0));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(50, 50, 60, 60));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, doc.Bounds), "選取");
        var layersBefore = doc.Root.Children.Count;

        // 按下去、原地放開（沒有位移）：複製出來的暫定圖層要整個收掉
        Drag(session, new SKPoint(80, 80), new SKPoint(80, 80), ToolModifiers.Alt);
        session.CommitFloating();

        Assert.Equal(layersBefore, doc.Root.Children.Count);
        Assert.Same(source, doc.ActiveLayer);
    }

    [Fact]
    public void 文字工具_Alt_拖文字也一樣複製到新圖層()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 300, SKColors.White));
        var doc = session.Document;
        var source = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement
        {
            Text = "MinePainter",
            FontSize = 48,
            Color = SKColors.Black,
            Position = new SKPoint(60, 120),
        };
        lock (doc.SyncRoot) source.AddElement(text);
        var layersBefore = doc.Root.Children.Count;

        // 先點一下選起來（最常見的情境：拖的就是現在選著的那個字）
        session.Text.OnPointerDown(new ToolPointerEvent(new SKPoint(90, 100), 1f), session);
        session.Text.OnPointerUp(new ToolPointerEvent(new SKPoint(90, 100), 1f), session);

        session.Text.OnPointerDown(new ToolPointerEvent(new SKPoint(90, 100), 1f, ToolModifiers.Alt), session);
        session.Text.OnPointerMove(new ToolPointerEvent(new SKPoint(190, 160), 1f, ToolModifiers.Alt), session);
        session.Text.OnPointerUp(new ToolPointerEvent(new SKPoint(190, 160), 1f, ToolModifiers.Alt), session);

        Assert.Equal(layersBefore + 1, doc.Root.Children.Count);
        var copyLayer = (RasterLayer)doc.ActiveLayer!;
        Assert.NotSame(source, copyLayer);
        Assert.Equal(IndexOf(source) + 1, IndexOf(copyLayer));

        Assert.Equal(text.Position, ((TextElement)source.Elements.Single()).Position);      // 原件沒動
        Assert.NotEqual(text.Position, ((TextElement)copyLayer.Elements.Single()).Position); // 複本被拖走
    }
}
