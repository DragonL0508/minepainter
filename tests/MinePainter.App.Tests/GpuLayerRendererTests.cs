using MinePainter.App.Rendering;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// GPU 圖層渲染路徑：畫出來的東西要跟合成器一致。
///
/// 這裡守的重點是**貼圖快取的失效**。這條路每格 tile 存一張 SKImage，靠 Tile.Version 判斷
/// 要不要重傳；版本號一旦「不同內容卻同號」，畫面就會停在上一份 —— 提起選取後原處的洞不見、
/// undo 之後畫面不跟著回去，都是這個病徵（實際發生過）。
/// </summary>
public class GpuLayerRendererTests : IDisposable
{
    private const int Size = 256;

    private readonly SKSurface _surface = SKSurface.Create(
        new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));

    private readonly GpuLayerRenderer _renderer = new();

    public void Dispose()
    {
        _renderer.Dispose();
        _surface.Dispose();
    }

    private static (EditorSession Session, RasterLayer Layer) NewDoc()
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, Size, Size), SKColors.Red);
        return (new EditorSession(doc) { LiveElementRendering = true }, layer);
    }

    private SKColor Draw(EditorSession session, int x, int y)
    {
        _surface.Canvas.Clear(SKColors.Transparent);
        lock (session.Document.SyncRoot)
        {
            Assert.True(_renderer.TryDraw(_surface.Canvas, session, new SKRectI(0, 0, Size, Size)));
        }
        _surface.Canvas.Flush();
        using var image = _surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public void 提起選取後原處要是洞()
    {
        var (session, _) = NewDoc();
        Assert.Equal(SKColors.Red, Draw(session, 30, 30)); // 先畫一次，讓快取存下這格

        using var path = new SKPath();
        path.AddRect(new SKRect(20, 20, 100, 100));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, session.Document.Bounds), "選取");
        var floating = session.LiftSelection();
        Assert.NotNull(floating);
        floating!.TargetRect = SKRect.Create(150, 150, 80, 80);

        Assert.Equal(0, Draw(session, 30, 30).Alpha);        // 原處挖空
        Assert.Equal(SKColors.Red, Draw(session, 180, 180)); // 浮動內容照層序畫在新位置
    }

    [Fact]
    public void undo之後畫面要跟著回去()
    {
        var (session, layer) = NewDoc();
        Assert.Equal(SKColors.Red, Draw(session, 30, 30));

        var before = layer.Surface.Snapshot();
        lock (session.Document.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 64, 64), SKColors.Blue);
        layer.Invalidate(new SKRectI(0, 0, 64, 64));
        Assert.Equal(SKColors.Blue, Draw(session, 30, 30));

        // undo：整格換回快照裡的那一份（RestoreTile）
        lock (session.Document.SyncRoot)
        {
            foreach (var (idx, tile) in before.Tiles) layer.Surface.RestoreTile(idx, tile);
        }
        layer.Invalidate(new SKRectI(0, 0, 64, 64));
        Assert.Equal(SKColors.Red, Draw(session, 30, 30));
        before.Dispose();
    }

    [Fact]
    public void 進行中的筆劃要畫得出來()
    {
        var (session, layer) = NewDoc();
        Assert.Equal(SKColors.Red, Draw(session, 128, 128));

        session.StrokeBuffer.Begin(layer.Id, SKColors.Blue, 1f, isEraser: false);
        lock (session.Document.SyncRoot)
        {
            var tile = session.StrokeBuffer.Mask.GetForWrite(TileIndex.FromPixel(128, 128));
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                tile.Alpha[y * MaskTile.Size + x] = 255;
            session.StrokeBuffer.Mask.ExtendBounds(new SKRectI(0, 0, 64, 64));
        }

        Assert.Equal(SKColors.Blue, Draw(session, 30, 30)); // 筆劃蓋在圖層上
        Assert.Equal(SKColors.Red, Draw(session, 200, 200)); // 沒畫到的地方不動
    }
}
