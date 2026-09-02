using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class BrushAndHistoryTests
{
    private static EditorSession NewSession(int w = 512, int h = 512)
    {
        var doc = ImageCodec.CreateBlankDocument(w, h, SKColors.White);
        return new EditorSession(doc);
    }

    private static SKColor GetLayerPixel(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;

        var rect = idx.ToPixelRect();
        var offset = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        // premul BGRA → SKColor（未反premul，測試用已知 alpha=255 的情況）
        return new SKColor(s[offset + 2], s[offset + 1], s[offset + 0], s[offset + 3]);
    }

    private static void PaintStroke(EditorSession session, SKPoint from, SKPoint to)
    {
        var tool = session.ActiveTool;
        tool.OnPointerDown(new ToolPointerEvent(from, 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(to, 1f), session);
        tool.OnPointerUp(new ToolPointerEvent(to, 1f), session);
    }

    [Fact]
    public void BrushStroke_CommitsToLayer()
    {
        using var session = NewSession();
        session.Foreground = new SKColor(255, 0, 0);
        session.Brush.Settings.Radius = 20;
        session.Brush.Settings.Hardness = 1f;

        PaintStroke(session, new SKPoint(100, 100), new SKPoint(200, 100));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(new SKColor(255, 0, 0), GetLayerPixel(layer, 150, 100)); // 筆劃中心
        Assert.Equal(SKColors.White, GetLayerPixel(layer, 150, 200));          // 筆劃外
    }

    [Fact]
    public void BrushOpacity_IsWholeStroke_NotPerDab()
    {
        using var session = NewSession();
        session.Foreground = SKColors.Black;
        session.Brush.Settings.Radius = 20;
        session.Brush.Settings.Hardness = 1f;
        session.Brush.Settings.Opacity = 0.5f;

        // 來回塗同一段：dab 大量重疊，wash 語意下不透明度仍應為 50%
        var tool = session.Brush;
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        for (var i = 0; i < 6; i++)
        {
            tool.OnPointerMove(new ToolPointerEvent(new SKPoint(200, 100), 1f), session);
            tool.OnPointerMove(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);
        }
        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(100, 100), 1f), session);

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        var px = GetLayerPixel(layer, 150, 100);
        // 白底 + 50% 黑 = 中灰；若 per-dab 累積會遠低於 120
        Assert.InRange(px.Red, 120, 136);
    }

    [Fact]
    public void Eraser_ClearsPixels_AndBlankTilesRecycled()
    {
        using var session = NewSession(256, 256);
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(1, layer.Surface.TileCount);

        session.ActiveTool = session.Eraser;
        session.Eraser.Settings.Radius = 300; // 蓋住整張 256²
        session.Eraser.Settings.Hardness = 1f;
        PaintStroke(session, new SKPoint(128, 128), new SKPoint(128, 128));

        Assert.Equal(0, layer.Surface.TileCount); // 擦光的 tile 被回收
    }

    [Fact]
    public void UndoRedo_RoundTrips()
    {
        using var session = NewSession();
        session.Foreground = new SKColor(0, 0, 255);
        session.Brush.Settings.Radius = 20;
        session.Brush.Settings.Hardness = 1f;

        PaintStroke(session, new SKPoint(100, 100), new SKPoint(200, 100));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(new SKColor(0, 0, 255), GetLayerPixel(layer, 150, 100));
        Assert.True(session.History.CanUndo);

        Assert.True(session.History.Undo());
        Assert.Equal(SKColors.White, GetLayerPixel(layer, 150, 100));

        Assert.True(session.History.Redo());
        Assert.Equal(new SKColor(0, 0, 255), GetLayerPixel(layer, 150, 100));
    }

    [Fact]
    public void Undo_MemoryCost_IsTileGranular()
    {
        using var session = NewSession(2048, 2048);
        session.Brush.Settings.Radius = 10;
        session.Brush.Settings.Hardness = 1f;

        // 小筆劃只碰 1-4 個 tile，成本應遠小於整層 64 tiles
        PaintStroke(session, new SKPoint(100, 100), new SKPoint(120, 100));

        var entry = session.History.UndoStack[^1];
        Assert.True(entry.MemoryCost <= 4 * 2 * Tile.BytesPerTile,
            $"MemoryCost {entry.MemoryCost} 應為 tile 級而非整層");
    }

    [Fact]
    public void HistoryEviction_RespectsMemoryLimit()
    {
        using var session = NewSession();
        session.History.MemoryLimit = 3 * Tile.BytesPerTile; // 極小上限逼出淘汰
        session.Brush.Settings.Radius = 10;
        session.Brush.Settings.Hardness = 1f;

        for (var i = 0; i < 10; i++)
            PaintStroke(session, new SKPoint(50 + i * 20, 50), new SKPoint(60 + i * 20, 50));

        Assert.True(session.History.TotalMemoryCost <= 3 * Tile.BytesPerTile
                    || session.History.UndoStack.Count == 1);
    }

    [Fact]
    public void StrokeAcrossTileBoundary_Works()
    {
        using var session = NewSession();
        session.Foreground = new SKColor(0, 128, 0);
        session.Brush.Settings.Radius = 10;
        session.Brush.Settings.Hardness = 1f;

        // 跨 (0,0)/(1,0) tile 邊界（x=256）
        PaintStroke(session, new SKPoint(240, 100), new SKPoint(272, 100));

        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.Equal(new SKColor(0, 128, 0), GetLayerPixel(layer, 250, 100));
        Assert.Equal(new SKColor(0, 128, 0), GetLayerPixel(layer, 264, 100));
    }

    [Fact]
    public void BrushDoesNotPaintOutsideCanvas()
    {
        // 文件 300×300 不是 tile(256) 的整數倍：最後一排 tile 涵蓋到 512，
        // 畫布外那截不該能被塗到。
        using var session = NewSession(300, 300);
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        session.Foreground = new SKColor(255, 0, 0);
        session.Brush.Settings.Radius = 40;
        session.Brush.Settings.Hardness = 1f;

        // 從畫布內畫到畫布外
        PaintStroke(session, new SKPoint(280, 150), new SKPoint(450, 150));

        Assert.Equal(new SKColor(255, 0, 0), GetLayerPixel(layer, 290, 150)); // 畫布內有畫到
        Assert.Equal(0, GetLayerPixel(layer, 320, 150).Alpha);                // 畫布外（同一個 tile）沒有
        Assert.Equal(0, GetLayerPixel(layer, 400, 150).Alpha);
    }

    [Fact]
    public void BrushOnOffsetLayer_LandsAtDocPosition()
    {
        using var session = NewSession();
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        lock (session.Document.SyncRoot) layer.Offset = new SKPointI(100, 100);

        session.Foreground = new SKColor(255, 0, 255);
        session.Brush.Settings.Radius = 10;
        session.Brush.Settings.Hardness = 1f;
        PaintStroke(session, new SKPoint(300, 300), new SKPoint(300, 300));

        // doc(300,300) → layer(200,200)
        Assert.Equal(new SKColor(255, 0, 255), GetLayerPixel(layer, 200, 200));
    }
}
