using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class ImageCommandTests
{
    private static unsafe SKColor PixelAt(RasterLayer layer, int docX, int docY)
    {
        var x = docX - layer.Offset.X;
        var y = docY - layer.Offset.Y;
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(x, y));
        if (tile == null) return SKColors.Transparent;
        var p = ((uint*)tile.Pixels)[(y & 255) * Tile.Size + (x & 255)];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    [Fact]
    public void ResizeImage_ScalesPixelsAndTextAndUndoes()
    {
        using var doc = ImageCodec.CreateBlankDocument(100, 50, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, 50, 50), SKColors.Red);   // 左半紅
        layer.Surface.Fill(new SKRectI(50, 0, 100, 50), SKColors.Blue); // 右半藍
        layer.AddElement(new TextElement { Text = "hi", Position = new SKPoint(50, 25), FontSize = 20 });
        using var session = new EditorSession(doc);

        ImageCommands.ResizeImage(session, 200, 100);
        Assert.Equal(200, doc.Width);
        Assert.Equal(100, doc.Height);
        Assert.Equal(255, PixelAt(layer, 20, 50).Red);
        Assert.Equal(255, PixelAt(layer, 180, 50).Blue);
        var text = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal(100f, text.Position.X, 1);
        Assert.Equal(40f, text.FontSize, 1);

        Assert.True(session.Undo());
        Assert.Equal(100, doc.Width);
        Assert.Equal(255, PixelAt(layer, 20, 25).Red);
        Assert.Equal(20f, Assert.IsType<TextElement>(layer.Elements[0]).FontSize, 1);

        Assert.True(session.Redo());
        Assert.Equal(200, doc.Width);
        Assert.Equal(255, PixelAt(layer, 180, 50).Blue);
    }

    [Fact]
    public void ResizeCanvas_AnchorCenterShiftsContent()
    {
        using var doc = ImageCodec.CreateBlankDocument(100, 100, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, 10, 10), SKColors.Lime); // 左上角 10×10
        using var session = new EditorSession(doc);

        ImageCommands.ResizeCanvas(session, 200, 200, 0.5f, 0.5f);
        Assert.Equal(200, doc.Width);
        Assert.Equal(new SKPointI(50, 50), layer.Offset);
        Assert.Equal(255, PixelAt(layer, 55, 55).Green);
        Assert.Equal(0, PixelAt(layer, 5, 5).Alpha);

        Assert.True(session.Undo());
        Assert.Equal(100, doc.Width);
        Assert.Equal(SKPointI.Empty, layer.Offset);
        Assert.Equal(255, PixelAt(layer, 5, 5).Green);
    }

    [Fact]
    public void ResizeCanvas_AnchorTopLeftKeepsOffset()
    {
        using var doc = ImageCodec.CreateBlankDocument(100, 100, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var session = new EditorSession(doc);
        ImageCommands.ResizeCanvas(session, 150, 120, 0f, 0f);
        Assert.Equal(SKPointI.Empty, layer.Offset);
        Assert.Equal(150, doc.Width);
    }

    [Fact]
    public void FlipLayer_OnlyAffectsThatLayer()
    {
        using var doc = ImageCodec.CreateBlankDocument(100, 40, SKColors.Transparent);
        var a = (RasterLayer)doc.ActiveLayer!;
        a.Surface.Fill(new SKRectI(0, 0, 10, 40), SKColors.Red);
        var b = new RasterLayer { Name = "b" };
        b.Surface.Fill(new SKRectI(0, 0, 10, 40), SKColors.Blue);
        lock (doc.SyncRoot) doc.Root.Add(b);
        using var session = new EditorSession(doc);

        ImageCommands.FlipLayer(session, a, GeometryOp.FlipHorizontal, "水平翻轉圖層");
        Assert.Equal(255, PixelAt(a, 95, 20).Red);
        Assert.Equal(0, PixelAt(a, 5, 20).Alpha);
        Assert.Equal(255, PixelAt(b, 5, 20).Blue); // 另一層不動

        Assert.True(session.Undo());
        Assert.Equal(255, PixelAt(a, 5, 20).Red);
    }

    [Fact]
    public void ImportImageLayer_InsertsAboveActiveAndUndoes()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.White);
        using var session = new EditorSession(doc);
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Magenta);

        var layer = ImageCommands.ImportImageLayer(session, bitmap, "匯入");
        Assert.Equal(2, doc.Root.Children.Count);
        Assert.Same(layer, doc.ActiveLayer);
        Assert.Equal(255, PixelAt(layer, 3, 3).Red);
        Assert.Equal(255, PixelAt(layer, 3, 3).Blue);

        Assert.True(session.Undo());
        Assert.Single(doc.Root.Children);
    }

    [Fact]
    public void LoadBitmap_DecodesPng()
    {
        using var src = new SKBitmap(8, 8);
        src.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(src);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        using var bitmap = ImageCodec.LoadBitmap(stream);
        Assert.Equal(8, bitmap.Width);
        Assert.Equal(SKColorType.Bgra8888, bitmap.ColorType);
        Assert.Equal(255, bitmap.GetPixel(2, 2).Red);
    }
}

public class TextTransformResetTests
{
    [Fact]
    public void TransformedBy_RecordsBaseFontSize_AndResetRestoresIt()
    {
        var text = new TextElement { Text = "abc", FontSize = 40, LetterSpacing = 2 };
        var scaled = (TextElement)text.TransformedBy(SKMatrix.CreateScale(2, 2), 2, 2, 30);
        Assert.Equal(80f, scaled.FontSize);
        Assert.Equal(40f, scaled.BaseFontSize);
        Assert.True(scaled.IsTransformed);

        var reset = scaled.WithTransformReset();
        Assert.Equal(40f, reset.FontSize);
        Assert.Equal(0f, reset.Rotation);
        Assert.Equal(2f, reset.LetterSpacing, 3);
        Assert.Null(reset.BaseFontSize);
        Assert.False(reset.IsTransformed);
    }

    [Fact]
    public void ScaleOnly_CountsAsTransformed_AndExplicitFontSizeClearsBase()
    {
        var text = new TextElement { Text = "abc", FontSize = 40 };
        var scaled = (TextElement)text.TransformedBy(SKMatrix.CreateScale(1.5f, 1.5f), 1.5f, 1.5f, 0);
        Assert.True(scaled.IsTransformed); // 沒轉、沒拉歪，只有放大，也要能重設

        var explicitSize = scaled.WithFontSize(72);
        Assert.Null(explicitSize.BaseFontSize);
        Assert.False(explicitSize.IsTransformed);
    }

    [Fact]
    public void ShiftResize_KeepsCurrentScaleX()
    {
        using var doc = ImageCodec.CreateBlankDocument(400, 400, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "Hello", FontSize = 40, ScaleX = 1.5f, Position = new SKPoint(50, 50) };
        layer.AddElement(text);
        using var session = new EditorSession(doc);
        ElementDragHelper.SetSelected(session, layer, text);

        var frame = text.FrameBounds;
        var helper = new ElementDragHelper();
        Assert.True(helper.TryBegin(session, new SKPoint(frame.Right, frame.Bottom), 6f, allowInsideMove: false));
        helper.Continue(session, new SKPoint(frame.Right + 60, frame.Bottom + 60), ToolModifiers.Shift);
        var resized = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal(1.5f, resized.ScaleX, 3); // Shift 維持目前比例，而不是硬歸 1
        Assert.True(resized.FontSize > 40f);
        Assert.Equal(40f, resized.BaseFontSize);
        helper.End(session);
    }
}

public class ElementDragOverlayTests
{
    [Fact]
    public void MoveText_UsesOverlayDuringDrag_AndCommitsOnEnd()
    {
        using var doc = ImageCodec.CreateBlankDocument(300, 200, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "Drag", FontSize = 32, Position = new SKPoint(40, 40) };
        lock (doc.SyncRoot) layer.AddElement(text);
        using var session = new EditorSession(doc);

        var helper = new ElementDragHelper();
        lock (doc.SyncRoot) helper.BeginMoveLocked(session, layer, text, new SKPoint(50, 50));
        Assert.NotNull(session.ElementOverlay);
        Assert.Equal(text.Id, layer.HiddenElementId);

        helper.Continue(session, new SKPoint(80, 70));
        Assert.Equal(40f, ((TextElement)layer.FindElement(text.Id)!).Position.X); // 拖曳中原件不動
        Assert.Equal(30f, session.ElementOverlay!.OffsetX, 3);
        var handles = session.SelectionHandles!.Value;
        Assert.True(handles.Left > text.FrameBounds.Left + 25); // 把手跟著覆疊走

        helper.End(session);
        Assert.Null(session.ElementOverlay);
        Assert.Null(layer.HiddenElementId);
        Assert.NotNull(session.Ghost); // 殘影等合成器追上
        var moved = (TextElement)layer.FindElement(text.Id)!;
        Assert.Equal(70f, moved.Position.X, 3);
        Assert.Equal(60f, moved.Position.Y, 3);
        Assert.True(session.History.CanUndo);
        Assert.True(session.Undo());
        Assert.Equal(40f, ((TextElement)layer.FindElement(text.Id)!).Position.X, 3);
    }

    [Fact]
    public void ChangingSelection_DuringDrag_RestoresHiddenElement()
    {
        using var doc = ImageCodec.CreateBlankDocument(300, 200, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "A", FontSize = 32, Position = new SKPoint(40, 40) };
        lock (doc.SyncRoot) layer.AddElement(text);
        using var session = new EditorSession(doc);
        var helper = new ElementDragHelper();
        lock (doc.SyncRoot) helper.BeginMoveLocked(session, layer, text, new SKPoint(50, 50));
        session.SelectedElement = null;
        Assert.Null(session.ElementOverlay);
        Assert.Null(layer.HiddenElementId);
    }
}
