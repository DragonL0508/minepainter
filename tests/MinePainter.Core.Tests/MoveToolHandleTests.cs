using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>移動工具的把手（2026-09-17 使用者回報的兩個 UX 問題）。</summary>
public class MoveToolHandleTests
{
    private static ToolPointerEvent At(float x, float y) => new(new SKPoint(x, y), 1f);

    /// <summary>「把圖片部分框選後，使用移動工具只能移動，不能變換大小」。</summary>
    [Fact]
    public void 框選後用移動工具拖角_縮放的是選取的內容()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(100, 100, 200, 200), SKColors.Red);
        layer.InvalidateAll();

        using var path = new SKPath();
        path.AddRect(new SKRect(100, 100, 200, 200));
        session.Selection = SelectionMask.FromPath(path, doc.Bounds);
        session.ActiveTool = session.Move;

        // 抓右下角往外拖 100px
        session.Move.OnPointerDown(At(200, 200), session);
        Assert.NotNull(session.Floating); // 提起了像素，不是只在改螞蟻線
        session.Move.OnPointerMove(At(300, 300), session);
        session.Move.OnPointerUp(At(300, 300), session);
        Assert.Equal(new SKRect(100, 100, 300, 300), session.Floating!.TargetRect);

        session.CommitFloating();
        using (var bmp = ImageCommands.ReadRegion(layer.Surface, new SKRectI(0, 0, 400, 400)))
            Assert.True(bmp.GetPixel(280, 280).Alpha > 200, "內容應該被放大到 (300,300)");
        Assert.Equal(new SKRectI(100, 100, 300, 300), session.Selection!.Bounds);

        session.Undo();
        using var undone = ImageCommands.ReadRegion(layer.Surface, new SKRectI(0, 0, 400, 400));
        Assert.Equal(0, undone.GetPixel(280, 280).Alpha);
    }

    /// <summary>選取類工具下拖角照舊只改選取範圍本身（不動像素）。</summary>
    [Fact]
    public void 選取工具下拖角_仍然只縮放選取範圍()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 400, SKColors.White));
        using var path = new SKPath();
        path.AddRect(new SKRect(100, 100, 200, 200));
        session.Selection = SelectionMask.FromPath(path, session.Document.Bounds);
        session.ActiveTool = session.RectSelect;

        var handles = new HandleDragController();
        Assert.True(handles.TryBegin(session, new SKPoint(200, 200), 6f));
        handles.Continue(session, new SKPoint(260, 260), ToolModifiers.None);
        handles.End(session);
        Assert.Null(session.Floating);
        Assert.Equal(new SKRectI(100, 100, 260, 260), session.Selection!.Bounds);
    }

    /// <summary>
    /// 「把一個新素材拉進去的時候，放大的時候會跳一下」：快速模式匯入的素材帶原始高清來源，
    /// 來源記的框是整張原圖（含四周透明邊），使用者抓的是貼著內容的框。
    /// 按下把手、滑鼠還沒動的那一刻，內容不能動。
    /// </summary>
    [Fact]
    public void 帶透明邊的素材_開始拖角的那一刻框不跳()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent));
        var doc = session.Document;
        doc.SetOutputSize(1024, 1024);
        Assert.True(doc.IsFastMode);

        // 800×800 的圖，內容只在中間 400×400（四周是透明邊）
        using var asset = new SKBitmap(new SKImageInfo(800, 800, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(asset))
        {
            c.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Blue };
            c.DrawRect(SKRect.Create(200, 200, 400, 400), paint);
        }
        var layer = ImageCommands.ImportImageLayer(session, asset, "素材");
        Assert.NotNull(layer.ValidPixelSource);
        session.ActiveTool = session.Move;
        session.RefreshSelectionHandles();
        var shown = session.SelectionHandles!.Value; // 貼著內容：(50,50)-(150,150)
        Assert.Equal(new SKRect(50, 50, 150, 150), shown);

        // 抓右下角，故意偏 3px（把手有命中容差）
        session.Move.HandleTolerance = 6f;
        session.Move.OnPointerDown(At(153, 152), session);
        Assert.NotNull(session.Transform);
        Assert.Equal(shown, session.Transform!.TargetRect); // 框還是使用者看到的那個

        // 第一步只動 1px：框也只該變 1px，不是先補上 3px 的偏差、更不是跳成整張原圖的框
        session.Move.OnPointerMove(At(154, 153), session);
        Assert.Equal(new SKRect(50, 50, 151, 151), session.Transform!.TargetRect);

        // 拖到兩倍大落地：內容就是兩倍大，原始高清來源還在（輸出從原圖重畫）
        session.Move.OnPointerMove(At(253, 252), session);
        session.Move.OnPointerUp(At(253, 252), session);
        session.CommitTransform();
        Assert.NotNull(layer.ValidPixelSource);
        lock (doc.SyncRoot)
        {
            var b = layer.Surface.ExactContentBounds();
            Assert.Equal(new SKRectI(50, 50, 250, 250), new SKRectI(
                b.Left + layer.Offset.X, b.Top + layer.Offset.Y, b.Right + layer.Offset.X, b.Bottom + layer.Offset.Y));
        }
    }
}
