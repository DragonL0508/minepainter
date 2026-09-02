using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class BackgroundEraserTests
{
    private static EditorSession NewSession(SKColor background, int w = 256, int h = 256)
    {
        var doc = ImageCodec.CreateBlankDocument(w, h, background);
        return new EditorSession(doc);
    }

    private static byte AlphaAt(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x - layer.Offset.X, y - layer.Offset.Y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return 0;
        var rect = idx.ToPixelRect();
        var offset = ((y - layer.Offset.Y - rect.Top) * Tile.Size + (x - layer.Offset.X - rect.Left)) * 4;
        return tile.PixelSpan[offset + 3];
    }

    /// <summary>在圖層上畫一個實心矩形（模擬前景物件）。</summary>
    private static void FillRect(EditorSession session, SKRectI r, SKColor color)
    {
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        lock (session.Document.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(r))
            {
                var tile = layer.Surface.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
                var tr = idx.ToPixelRect();
                surface.Canvas.Translate(-tr.Left, -tr.Top);
                using var paint = new SKPaint { Color = color };
                surface.Canvas.DrawRect(r, paint);
                surface.Canvas.Flush();
            }
        }
        layer.Invalidate(r);
    }

    private static void Stroke(EditorSession session, SKPoint from, SKPoint to)
    {
        var tool = session.BackgroundEraser;
        tool.OnPointerDown(new ToolPointerEvent(from, 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(to, 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(to, 1f), session);
    }

    [Fact]
    public void ErasesBackground_KeepsForegroundObject()
    {
        using var session = NewSession(new SKColor(40, 160, 60)); // 綠幕
        FillRect(session, new SKRectI(100, 60, 140, 200), new SKColor(220, 30, 30)); // 紅色物件
        session.BackgroundEraser.Settings.Radius = 30;
        session.BackgroundEraser.Settings.Tolerance = 32;

        // 從物件左側掃到右側：圈掃過物件，但熱點取樣到的是綠色
        Stroke(session, new SKPoint(70, 130), new SKPoint(90, 130));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(0, AlphaAt(layer, 75, 130));     // 背景被擦掉
        Assert.Equal(255, AlphaAt(layer, 110, 130));  // 物件在圈內但顏色差很多 → 保留
        Assert.Equal(255, AlphaAt(layer, 200, 130));  // 圈外不動
    }

    [Fact]
    public void Contiguous_OnlyErasesRegionConnectedToHotspot()
    {
        using var session = NewSession(new SKColor(40, 160, 60));
        // 一道紅牆把圈內的綠分成左右兩半
        FillRect(session, new SKRectI(100, 0, 104, 256), new SKColor(220, 30, 30));
        var s = session.BackgroundEraser.Settings;
        s.Radius = 30; s.Tolerance = 32; s.Contiguous = true;

        Stroke(session, new SKPoint(90, 128), new SKPoint(90, 128));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(0, AlphaAt(layer, 90, 128));    // 熱點這側被擦
        Assert.Equal(255, AlphaAt(layer, 112, 128)); // 牆另一側的綠雖在圈內但不相連 → 保留

        session.History.Undo();
        s.Contiguous = false;
        Stroke(session, new SKPoint(90, 128), new SKPoint(90, 128));
        Assert.Equal(0, AlphaAt(layer, 112, 128));   // 不連續限制 → 圈內所有相近色都擦
    }

    [Fact]
    public void ProtectForeground_KeepsPixelsNearForegroundColor()
    {
        using var session = NewSession(new SKColor(40, 160, 60));
        var hair = new SKColor(60, 40, 30);
        FillRect(session, new SKRectI(120, 120, 130, 130), hair);
        session.Foreground = hair;
        var s = session.BackgroundEraser.Settings;
        s.Radius = 40; s.Tolerance = 200; s.ProtectForeground = true; s.Contiguous = false;

        Stroke(session, new SKPoint(100, 125), new SKPoint(100, 125));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(0, AlphaAt(layer, 100, 125));
        Assert.Equal(255, AlphaAt(layer, 125, 125)); // 容許度極大也擦不到前景色
    }

    [Fact]
    public void Softness_GivesPartialAlphaOnMixedPixels()
    {
        var bg = new SKColor(40, 160, 60);
        using var session = NewSession(bg);
        // 背景與物件之間的混合像素（模擬髮絲邊緣）
        var mixed = new SKColor(130, 95, 45);
        FillRect(session, new SKRectI(110, 100, 112, 160), mixed);
        FillRect(session, new SKRectI(112, 100, 160, 160), new SKColor(220, 30, 30));
        var s = session.BackgroundEraser.Settings;
        s.Radius = 25; s.Tolerance = 40; s.Softness = 1f; s.Contiguous = false;

        Stroke(session, new SKPoint(100, 130), new SKPoint(100, 130));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var a = AlphaAt(layer, 111, 130);
        Assert.InRange(a, 1, 254); // 半透明，而不是全擦或全留
        Assert.Equal(255, AlphaAt(layer, 120, 130));
    }

    [Fact]
    public void StartingOnTransparent_DoesNothing()
    {
        using var session = NewSession(SKColors.Transparent);
        FillRect(session, new SKRectI(50, 50, 100, 100), SKColors.Red);
        var s = session.BackgroundEraser.Settings;
        s.Radius = 60; s.Sampling = BackgroundSampling.Once;

        Stroke(session, new SKPoint(20, 20), new SKPoint(60, 60));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(255, AlphaAt(layer, 75, 75));
        Assert.False(session.History.CanUndo);
    }

    [Fact]
    public void Undo_RestoresErasedPixels()
    {
        using var session = NewSession(SKColors.Blue);
        session.BackgroundEraser.Settings.Radius = 20;
        Stroke(session, new SKPoint(50, 50), new SKPoint(80, 50));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(0, AlphaAt(layer, 60, 50));
        session.History.Undo();
        Assert.Equal(255, AlphaAt(layer, 60, 50));
    }
}
