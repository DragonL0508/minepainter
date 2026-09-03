using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>複製（CopyToImage）／貼上（PasteImage）／延展畫布（ResizeCanvas）的核心語意。</summary>
public class ClipboardOpsTests
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

    private static SKImage MakeImage(int w, int h, SKColor color)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static EditorSession NewSession(int w = 512, int h = 512) =>
        new(ImageCodec.CreateBlankDocument(w, h, SKColors.White));

    // ---- 貼上 ----

    [Fact]
    public void Paste_CreatesPastedFloating_WithoutTouchingLayer()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        Assert.True(session.PasteImage(MakeImage(50, 50, new SKColor(0, 200, 0)), new SKPointI(10, 20)));

        Assert.NotNull(session.Floating);
        Assert.True(session.Floating!.IsPasted);
        Assert.Equal(SKColors.White, Px(layer, 30, 40)); // 圖層本身還沒被動到（內容在浮動層）
        Assert.Equal(255, session.Selection!.CoverageAt(30, 40)); // 選取框 = 貼上矩形
        Assert.Equal(0, session.Selection.CoverageAt(100, 100));
    }

    [Fact]
    public void Paste_CommitWithoutMoving_StillStampsPixels()
    {
        // Lift 的「沒動過＝取消」捷徑不適用於貼上 —— 沒動過也要落地，否則貼的東西直接消失
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var undoBefore = session.History.UndoStack.Count;

        session.PasteImage(MakeImage(50, 50, new SKColor(0, 200, 0)), new SKPointI(10, 20));
        session.CommitFloating();

        Assert.Null(session.Floating);
        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 30, 40));
        Assert.Equal(undoBefore + 1, session.History.UndoStack.Count); // 貼上 = 一步 undo

        session.History.Undo();
        Assert.Equal(SKColors.White, Px(layer, 30, 40));
    }

    [Fact]
    public void Paste_Cancel_DiscardsContent()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var undoBefore = session.History.UndoStack.Count;

        session.PasteImage(MakeImage(50, 50, new SKColor(0, 200, 0)), new SKPointI(10, 20));
        session.CancelFloating();

        Assert.Null(session.Floating);
        Assert.Equal(SKColors.White, Px(layer, 30, 40));            // 什麼都沒留下
        Assert.Null(session.Selection);                              // 貼上時建的選取框也清掉
        Assert.Equal(undoBefore, session.History.UndoStack.Count);   // 不記歷史
    }

    [Fact]
    public void Paste_LargerThanCanvas_SingleUnifiedFrameWhileFloating()
    {
        // 「維持畫布大小」路徑：螞蟻線（選取遮罩）與把手框（TargetRect）必須是同一個矩形。
        // 遮罩若在貼上當下就被裁到畫布，畫面上會出現兩個分開的框 —— 這曾是實際的 bug。
        using var session = NewSession();

        session.PasteImage(MakeImage(600, 600, new SKColor(0, 200, 0)), new SKPointI(0, 0));

        var full = new SKRectI(0, 0, 600, 600);
        Assert.Equal(full, session.Selection!.Bounds);                        // 遮罩 = 完整貼上矩形
        Assert.Equal(SKRect.Create(0, 0, 600, 600), session.Floating!.TargetRect);
        Assert.Equal(session.Floating.TargetRect, session.SelectionHandles);  // 把手框同一個矩形
        Assert.Equal(255, session.Selection.CoverageAt(550, 550));            // 超出畫布的部分也在選取內
    }

    [Fact]
    public void Paste_LargerThanCanvas_SelectionStaysClippedOnCommit()
    {
        // 落地是唯一的裁切點：commit 後進 session／history 的選取一律在畫布內
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.PasteImage(MakeImage(600, 600, new SKColor(0, 200, 0)), new SKPointI(0, 0));
        session.CommitFloating();

        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 500, 500));
        Assert.Equal(session.Document.Bounds, session.Selection!.Bounds); // 選取仍是整個畫布，沒被拉錯比例
        Assert.Equal(255, session.Selection.CoverageAt(511, 511));

        // undo 還原的選取也要是裁切過的版本，超出畫布的遮罩不得漏進 committed 狀態
        session.History.Undo();
        Assert.Equal(session.Document.Bounds, session.Selection!.Bounds);
    }

    [Fact]
    public void Paste_LargerThanCanvas_OutOfCanvasPixelsSurviveCommit()
    {
        // 圖層刻意可持有畫布外像素（見 DocumentCommands.ResizeCanvas 的註解）：
        // 「維持畫布大小」落地後，超出的部分只是看不到，延展畫布就自然露出來。
        // 這是與 paint.net/Pinta（畫布外即銷毀）刻意不同的資料模型。
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.PasteImage(MakeImage(600, 600, new SKColor(0, 200, 0)), new SKPointI(0, 0));
        session.CommitFloating();

        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 550, 550)); // 畫布(512)外的像素還在圖層裡

        DocumentCommands.ResizeCanvas(session, 600, 600, "延展畫布");
        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 550, 550)); // 延展後自然可見、可再編輯
    }

    [Fact]
    public void Paste_IsSinglePendingEdit_CommittedByUndo()
    {
        // 貼上後直接 Ctrl+Z：先落地再復原 = 貼上被取消，且可 redo
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;

        session.PasteImage(MakeImage(50, 50, new SKColor(0, 200, 0)), new SKPointI(10, 20));
        session.Undo();

        Assert.Null(session.Floating);
        Assert.Equal(SKColors.White, Px(layer, 30, 40));
        Assert.True(session.History.CanRedo);

        session.History.Redo();
        Assert.Equal(new SKColor(0, 200, 0), Px(layer, 30, 40));
    }

    // ---- 複製 ----

    [Fact]
    public void Copy_WithSelection_ReturnsMaskedPixels()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(100, 100, 200, 200), new SKColor(255, 0, 0));

        using var path = new SKPath();
        path.AddRect(SKRect.Create(150, 150, 100, 100)); // 一半紅一半白
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        using var image = session.CopyToImage();
        Assert.NotNull(image);
        Assert.Equal(100, image!.Width);
        Assert.Equal(100, image.Height);

        using var bmp = SKBitmap.FromImage(image);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(10, 10));   // doc(160,160) = 紅
        Assert.Equal(SKColors.White, bmp.GetPixel(80, 80));           // doc(230,230) = 白底
    }

    [Fact]
    public void Copy_WithoutSelection_ReturnsWholeCanvas()
    {
        using var session = NewSession(300, 200);
        using var image = session.CopyToImage();
        Assert.NotNull(image);
        Assert.Equal(300, image!.Width);
        Assert.Equal(200, image.Height);
    }

    [Fact]
    public void Copy_ReportsSelectionOrigin_ForPasteInPlace()
    {
        using var session = NewSession(300, 200);
        using var path = new SKPath();
        path.AddRect(SKRect.Create(40, 60, 50, 30));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        using var image = session.CopyToImage(out var origin);
        Assert.NotNull(image);
        Assert.Equal(new SKPointI(40, 60), origin); // 貼上要貼回這裡，不是可視範圍左上角

        session.Selection = null;
        using var whole = session.CopyToImage(out var wholeOrigin);
        Assert.Equal(new SKPointI(0, 0), wholeOrigin); // 無選取＝整個畫布，原點就是 (0,0)
    }

    [Fact]
    public void Copy_IncludesRenderedEffects()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        lock (session.Document.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 64, 64), new SKColor(100, 100, 100));
        layer.InvalidateAll();

        LayerEffectCommands.Add(session.Document, session.History, layer,
            LayerEffect.Create(new AdjustmentEffect(new InvertAdjustment())));

        using var image = session.CopyToImage();
        Assert.NotNull(image);
        using var bmp = SKBitmap.FromImage(image);
        // 複製出去的是眼睛看到的樣子（已反相），不是圖層底下那份原始像素
        Assert.Equal(155, bmp.GetPixel(10, 10).Red);
        Assert.Equal(100, Px(layer, 10, 10).Red); // 基底像素完全沒被動到
    }

    [Fact]
    public void Copy_OnTextLayer_ReturnsTheRenderedText()
    {
        using var session = NewSession();
        var doc = session.Document;
        var text = VectorCommands.CreateTextLayerSilently(doc);
        var element = new TextElement
        {
            Text = "AB", Position = new SKPoint(20, 20), FontSize = 80, Color = SKColors.Red,
        };
        lock (doc.SyncRoot) text.AddElement(element);
        VectorCommands.CommitNewTextLayer(doc, session.History, text, element, "新增文字");

        using var image = session.CopyToImage();
        Assert.NotNull(image);
        using var bmp = SKBitmap.FromImage(image!);
        var painted = 0;
        for (var y = 0; y < bmp.Height; y += 2)
        for (var x = 0; x < bmp.Width; x += 2)
            if (bmp.GetPixel(x, y).Alpha > 0) painted++;
        Assert.True(painted > 0, "文字圖層沒有像素，只取基底會複製到一張空白");
    }

    [Fact]
    public void Copy_OnGroup_ReturnsTheComposite()
    {
        using var session = NewSession();
        var doc = session.Document;
        var group = new GroupLayer { Name = "群組" };
        var child = new RasterLayer { Name = "內容" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(child);
            child.Surface.Fill(new SKRectI(0, 0, 40, 40), SKColors.Blue);
            doc.ActiveLayer = group;
        }
        child.InvalidateAll();

        using var image = session.CopyToImage();
        Assert.NotNull(image);
        using var bmp = SKBitmap.FromImage(image!);
        Assert.Equal(SKColors.Blue, bmp.GetPixel(10, 10)); // 以前選群組時複製不到東西
    }

    [Fact]
    public void Copy_DoesNotModifyLayerOrHistory()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var undoBefore = session.History.UndoStack.Count;

        using var path = new SKPath();
        path.AddRect(SKRect.Create(100, 100, 50, 50));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);

        using var image = session.CopyToImage();

        Assert.Equal(SKColors.White, Px(layer, 120, 120)); // 不像 Lift，原處不挖空
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
    }

    // ---- 延展畫布 ----

    [Fact]
    public void ResizeCanvas_Expands_AndUndoRestores()
    {
        using var session = NewSession();
        var doc = session.Document;

        DocumentCommands.ResizeCanvas(session, 800, 700, "延展畫布（貼上）");
        Assert.Equal(800, doc.Width);
        Assert.Equal(700, doc.Height);

        session.History.Undo();
        Assert.Equal(512, doc.Width);
        Assert.Equal(512, doc.Height);

        session.History.Redo();
        Assert.Equal(800, doc.Width);
        Assert.Equal(700, doc.Height);
    }

    [Fact]
    public void ResizeCanvas_SameSize_IsNoOp()
    {
        using var session = NewSession();
        var undoBefore = session.History.UndoStack.Count;
        DocumentCommands.ResizeCanvas(session, 512, 512);
        Assert.Equal(undoBefore, session.History.UndoStack.Count);
    }
}
