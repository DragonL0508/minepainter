using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 文字圖層不變式：有物件的圖層永遠沒有像素。任何會把像素寫進圖層的入口
/// （貼上、向下合併、破壞性效果…）都不得在文字圖層上留下像素。
/// </summary>
public class TextLayerInvariantTests
{
    private static SKImage MakeImage(int w, int h, SKColor color)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static unsafe SKColor Px(RasterLayer layer, int x, int y)
    {
        var tile = layer.Surface.GetTileForRead(TileIndex.FromPixel(x - layer.Offset.X, y - layer.Offset.Y));
        if (tile == null) return SKColors.Transparent;
        var lx = x - layer.Offset.X;
        var ly = y - layer.Offset.Y;
        var p = ((uint*)tile.Pixels)[(ly & 255) * Tile.Size + (lx & 255)];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    private static (EditorSession Session, RasterLayer Pixels, RasterLayer Text) NewDocWithText()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var session = new EditorSession(doc);
        var pixels = (RasterLayer)doc.ActiveLayer!;
        var text = VectorCommands.CreateTextLayerSilently(doc);
        var element = new TextElement { Text = "Hi", Position = new SKPoint(40, 40), FontSize = 60, Color = SKColors.Red };
        lock (doc.SyncRoot) text.AddElement(element);
        VectorCommands.CommitNewTextLayer(doc, session.History, text, element, "新增文字");
        Assert.True(text.IsTextLayer);
        Assert.Same(text, doc.ActiveLayer);
        return (session, pixels, text);
    }

    // ---- 像素選取在文字圖層上沒有意義 ----

    [Fact]
    public void SwitchingToTextLayer_DropsPixelSelection()
    {
        var (session, pixels, text) = NewDocWithText();
        var doc = session.Document;

        lock (doc.SyncRoot) doc.ActiveLayer = pixels;
        EditCommands.SelectAll(session);
        Assert.NotNull(session.Selection);

        lock (doc.SyncRoot) doc.ActiveLayer = text;
        // 留著只會是一圈沒有任何操作會理它、也清不掉的螞蟻線
        Assert.Null(session.Selection);

        lock (doc.SyncRoot) doc.ActiveLayer = pixels;
        Assert.Null(session.Selection); // 換回來也不會自己冒出來
    }

    [Fact]
    public void SelectionTools_DoNothingOnTextLayer()
    {
        var (session, _, text) = NewDocWithText();
        Assert.Same(text, session.Document.ActiveLayer);

        var down = new ToolPointerEvent(new SKPoint(10, 10), 1f);
        var up = new ToolPointerEvent(new SKPoint(200, 200), 1f);

        session.RectSelect.OnPointerDown(down, session);
        session.RectSelect.OnPointerMove(up, session);
        session.RectSelect.OnPointerUp(up, session);
        Assert.Null(session.Selection);
        Assert.Null(session.Preview); // 連拖曳中的虛線框都不該出現

        session.Lasso.OnPointerDown(down, session);
        session.Lasso.OnPointerMove(new ToolPointerEvent(new SKPoint(60, 60), 1f), session);
        session.Lasso.OnPointerMove(up, session);
        session.Lasso.OnPointerUp(up, session);
        Assert.Null(session.Selection);
        Assert.Null(session.Preview);

        session.Wand.OnPointerDown(down, session);
        Assert.Null(session.Selection);
    }

    [Fact]
    public void SelectionTools_StillWorkOnNormalLayer()
    {
        var (session, pixels, _) = NewDocWithText();
        lock (session.Document.SyncRoot) session.Document.ActiveLayer = pixels;

        session.RectSelect.OnPointerDown(new ToolPointerEvent(new SKPoint(10, 10), 1f), session);
        session.RectSelect.OnPointerUp(new ToolPointerEvent(new SKPoint(60, 70), 1f), session);

        Assert.NotNull(session.Selection);
        Assert.Equal(new SKRectI(10, 10, 60, 70), session.Selection!.Bounds);
    }

    [Fact]
    public void Paste_OnTextLayer_GoesToNewLayer_OneUndoStep()
    {
        var (session, pixels, text) = NewDocWithText();
        var doc = session.Document;
        var stepsBefore = session.History.UndoStack.Count;

        Assert.True(session.PasteImage(MakeImage(50, 50, new SKColor(0, 200, 0)), new SKPointI(10, 20)));
        var pasted = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        Assert.NotSame(text, pasted);
        Assert.Equal(doc.Root.IndexOf(text) + 1, doc.Root.IndexOf(pasted)); // 插在文字圖層上方

        session.CommitFloating();
        Assert.Null(doc.FindMixedLayer());
        Assert.False(text.ViolatesTextLayerInvariant);
        Assert.Equal(new SKColor(0, 200, 0), Px(pasted, 20, 30));
        Assert.Equal(SKColors.White, Px(pixels, 20, 30)); // 底層沒被動到

        // 一步 undo：像素與新圖層一起消失
        Assert.Equal(stepsBefore + 1, session.History.UndoStack.Count);
        Assert.True(session.Undo());
        Assert.DoesNotContain(pasted, doc.Root.Children);
        Assert.Same(text, doc.ActiveLayer);
        Assert.True(session.Redo());
        Assert.Contains(pasted, doc.Root.Children);
        Assert.Equal(new SKColor(0, 200, 0), Px(pasted, 20, 30));
        Assert.Null(doc.FindMixedLayer());
        session.Dispose();
    }

    [Fact]
    public void Paste_OnTextLayer_Cancel_DropsTemporaryLayer()
    {
        var (session, _, text) = NewDocWithText();
        var doc = session.Document;
        var count = doc.Root.Children.Count;
        var steps = session.History.UndoStack.Count;

        Assert.True(session.PasteImage(MakeImage(50, 50, SKColors.Blue), new SKPointI(0, 0)));
        Assert.Equal(count + 1, doc.Root.Children.Count);
        session.CancelFloating();

        Assert.Equal(count, doc.Root.Children.Count);
        Assert.Equal(steps, session.History.UndoStack.Count);
        Assert.Same(text, doc.ActiveLayer);
        Assert.Null(doc.FindMixedLayer());
        session.Dispose();
    }

    [Fact]
    public void MergeDown_PixelsOntoText_YieldsPurePixelLayer()
    {
        var (session, pixels, text) = NewDocWithText();
        var doc = session.Document;

        // 文字圖層上方再放一層像素
        var top = new RasterLayer { Name = "上層" };
        lock (doc.SyncRoot) top.Surface.Fill(new SKRectI(0, 150, 60, 200), SKColors.Blue);
        LayerCommands.InsertLayer(doc, session.History, doc.Root, doc.Root.IndexOf(text) + 1, top);

        Assert.True(LayerCommands.MergeLayerDown(doc, session.History, top));
        Assert.DoesNotContain(top, doc.Root.Children);
        Assert.False(text.HasElements);            // 文字烙成像素
        Assert.Null(doc.FindMixedLayer());
        Assert.Equal(SKColors.Blue, Px(text, 10, 160)); // 上層像素進來了
        var textArea = new SKRectI(40, 40, 120, 100);
        var hasRed = false;
        for (var y = textArea.Top; y < textArea.Bottom && !hasRed; y++)
        for (var x = textArea.Left; x < textArea.Right; x++)
            if (Px(text, x, y) is { Red: > 200, Green: < 50, Alpha: > 200 }) { hasRed = true; break; }
        Assert.True(hasRed);

        // undo：文字物件回來、像素清空、上層回來
        Assert.True(session.Undo());
        Assert.True(text.IsTextLayer);
        Assert.False(text.ViolatesTextLayerInvariant);
        Assert.Contains(top, doc.Root.Children);
        Assert.Null(doc.FindMixedLayer());
        session.Dispose();
    }

    [Fact]
    public void MergeDown_TextOntoPixels_RasterizesText_NoElementsMoved()
    {
        var (session, pixels, text) = NewDocWithText();
        var doc = session.Document;

        Assert.True(LayerCommands.MergeLayerDown(doc, session.History, text));
        Assert.DoesNotContain(text, doc.Root.Children);
        Assert.False(pixels.HasElements); // 物件不搬家，烙成像素
        Assert.Null(doc.FindMixedLayer());
        var hasRed = false;
        for (var y = 40; y < 100 && !hasRed; y++)
        for (var x = 40; x < 120; x++)
            if (Px(pixels, x, y) is { Red: > 200, Green: < 50 }) { hasRed = true; break; }
        Assert.True(hasRed);

        Assert.True(session.Undo());
        Assert.Contains(text, doc.Root.Children);
        Assert.Equal(SKColors.White, Px(pixels, 60, 60));
        Assert.Null(doc.FindMixedLayer());
        session.Dispose();
    }

    [Fact]
    public void MergeDown_TextOntoText_YieldsPurePixelLayer()
    {
        var (session, _, lower) = NewDocWithText();
        var doc = session.Document;
        var upper = VectorCommands.CreateTextLayerSilently(doc);
        var yo = new TextElement { Text = "Yo", Position = new SKPoint(40, 140), FontSize = 60, Color = SKColors.Blue };
        lock (doc.SyncRoot) upper.AddElement(yo);
        VectorCommands.CommitNewTextLayer(doc, session.History, upper, yo, "新增文字");

        Assert.True(LayerCommands.MergeLayerDown(doc, session.History, upper));
        Assert.False(lower.HasElements);
        Assert.Null(doc.FindMixedLayer());
        Assert.True(lower.Surface.TileCount > 0);
        session.Dispose();
    }
}
