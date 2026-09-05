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
    public void ShiftResize_RestoresOriginalScaleX()
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
        // 手勢中只動覆疊圖（帶效果的文字每步重算太慢），原件放開才改
        Assert.Equal(1.5f, ((TextElement)layer.Elements[0]).ScaleX, 3);
        Assert.NotNull(session.ElementOverlay);
        Assert.True(session.ElementOverlay!.CurrentRect.Width > session.ElementOverlay.Bounds.Width);

        helper.End(session);
        var resized = Assert.IsType<TextElement>(layer.Elements[0]);
        Assert.Equal(1f, resized.ScaleX, 3); // Shift＝原始比例：ScaleX 歸 1，字級等比
        Assert.True(resized.FontSize > 40f);
        Assert.Equal(40f, resized.BaseFontSize);
    }
}

public class ElementDragOverlayTests
{
    [Fact]
    public void HugeText_StillGetsAVisibleOverlay_AtReducedResolution()
    {
        // GPU 貼圖有尺寸上限，整張畫不出來時畫面上就是「拖曳大物件時物件整個消失」。
        // 寧可解析度低也要看得到（使用者 2026-09-04 回報）。
        using var doc = ImageCodec.CreateBlankDocument(1920, 1080, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "很長的一段標題文字", FontSize = 3000, Position = new SKPoint(0, 0) };
        lock (doc.SyncRoot) layer.AddElement(text);
        using var session = new EditorSession(doc);

        lock (doc.SyncRoot) session.BeginElementOverlayLocked(layer, text);

        var overlay = session.ElementOverlay ?? throw new Xunit.Sdk.XunitException("沒有建立覆疊");
        var image = overlay.Image ?? throw new Xunit.Sdk.XunitException("覆疊沒有影像");
        Assert.True(overlay.Bounds.Width > 20000, "這個測試要的就是超大物件");
        Assert.True(image.Width <= 4096 && image.Height <= 4096,
            $"覆疊圖 {image.Width}x{image.Height} 超過貼圖上限，畫面上會整個看不到");
        Assert.True(image.Width > 0 && image.Height > 0);
        // 框仍然是原尺寸：畫的時候會拉回去，位置與大小都對
        Assert.Equal(overlay.Bounds.Width, overlay.CurrentRect.Width);
    }

    [Fact]
    public void RotateText_UsesOverlayDuringGesture_AndCommitsOnEnd()
    {
        using var doc = ImageCodec.CreateBlankDocument(300, 200, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "Spin", FontSize = 32, Position = new SKPoint(40, 40) };
        lock (doc.SyncRoot) layer.AddElement(text);
        using var session = new EditorSession(doc);
        ElementDragHelper.SetSelected(session, layer, text);

        var frame = text.FrameBounds;
        var center = new SKPoint(frame.MidX, frame.MidY);
        var helper = new ElementDragHelper();
        Assert.True(helper.TryBeginRotate(session, new SKPoint(center.X + 50, center.Y)));
        Assert.NotNull(session.ElementOverlay); // 手勢一開始就換成覆疊圖

        helper.ContinueRotate(session, new SKPoint(center.X, center.Y + 50)); // +90°
        Assert.Equal(0f, ((TextElement)layer.FindElement(text.Id)!).Rotation, 3); // 原件還沒動
        Assert.Equal(90f, session.ElementOverlay!.Rotation, 1);                   // 只有覆疊圖在轉
        Assert.Equal(90f, session.SelectionHandlesRotation, 1);                   // 把手框跟著轉

        helper.End(session);
        Assert.Null(session.ElementOverlay);
        Assert.Equal(90f, ((TextElement)layer.FindElement(text.Id)!).Rotation, 1); // 放開才真的改
        Assert.NotNull(session.Ghost);
        Assert.Equal(90f, session.Ghost!.Rotation, 1); // 殘影同姿態，放開不會閃回原角度
        Assert.True(session.History.CanUndo);
        Assert.True(session.Undo());
        Assert.Equal(0f, ((TextElement)layer.FindElement(text.Id)!).Rotation, 3);
    }

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
        Assert.Equal(session.ElementOverlay!.Bounds.Left + 30f, session.ElementOverlay.CurrentRect.Left, 3);
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
