using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class CompositorTests
{
    private static SKImage? WaitForTile(Compositor compositor, TileIndex idx, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (compositor.TryGetTile(idx, out var img)) return img;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"tile {idx} 合成逾時");
    }

    [Fact]
    public void SingleLayer_CompositesIdentity()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, new SKColor(10, 20, 30));
        using var compositor = new Compositor(doc);

        var img = WaitForTile(compositor, new TileIndex(0, 0));
        Assert.NotNull(img);

        using var raster = new SKBitmap(Tile.Info);
        Assert.True(img!.ReadPixels(Tile.Info, raster.GetPixels(), Tile.RowBytes, 0, 0));
        var px = raster.GetPixel(128, 128);
        Assert.Equal(new SKColor(10, 20, 30), px);
    }

    [Fact]
    public void EmptyRegion_ReportsTransparent()
    {
        using var doc = new Document(512, 512); // 沒有任何圖層
        using var compositor = new Compositor(doc);

        var img = WaitForTile(compositor, new TileIndex(1, 1));
        Assert.Null(img); // found 但 null = 全透明
    }

    [Fact]
    public void LayerEdit_InvalidatesAndRecomposites()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        using var compositor = new Compositor(doc);

        WaitForTile(compositor, new TileIndex(0, 0));

        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(0, 0, 256, 256), new SKColor(200, 0, 0));
        }
        layer.Invalidate(new SKRectI(0, 0, 256, 256));

        // 等待重新合成出紅色
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (compositor.TryGetTile(new TileIndex(0, 0), out var img) && img != null)
            {
                using var raster = new SKBitmap(Tile.Info);
                img.ReadPixels(Tile.Info, raster.GetPixels(), Tile.RowBytes, 0, 0);
                if (raster.GetPixel(10, 10) == new SKColor(200, 0, 0)) return; // 成功
            }
            Thread.Sleep(10);
        }
        Assert.Fail("編輯後未在時限內重新合成");
    }

    [Fact]
    public void TwoLayers_TopCoversBottom()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var top = new RasterLayer { Name = "top" };
        top.Surface.Fill(new SKRectI(0, 0, 100, 100), new SKColor(0, 0, 255));
        lock (doc.SyncRoot) doc.Root.Add(top);

        using var compositor = new Compositor(doc);
        var img = WaitForTile(compositor, new TileIndex(0, 0));

        using var raster = new SKBitmap(Tile.Info);
        img!.ReadPixels(Tile.Info, raster.GetPixels(), Tile.RowBytes, 0, 0);
        Assert.Equal(new SKColor(0, 0, 255), raster.GetPixel(50, 50));   // 上層覆蓋
        Assert.Equal(SKColors.White, raster.GetPixel(200, 200));         // 上層以外露出底層
    }

    [Fact]
    public void LayerOpacity_Blends()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var top = new RasterLayer { Name = "top", Opacity = 0.5f };
        top.Surface.Fill(new SKRectI(0, 0, 256, 256), new SKColor(0, 0, 0));
        lock (doc.SyncRoot) doc.Root.Add(top);

        using var compositor = new Compositor(doc);
        var img = WaitForTile(compositor, new TileIndex(0, 0));

        using var raster = new SKBitmap(Tile.Info);
        img!.ReadPixels(Tile.Info, raster.GetPixels(), Tile.RowBytes, 0, 0);
        var px = raster.GetPixel(50, 50);
        // 白底 + 50% 黑 ≈ 中灰（容許 ±2 誤差）
        Assert.InRange(px.Red, 126, 130);
        Assert.InRange(px.Green, 126, 130);
        Assert.InRange(px.Blue, 126, 130);
    }

    [Fact]
    public void RasterOffset_ShiftsContent()
    {
        using var doc = new Document(512, 512);
        var layer = new RasterLayer();
        layer.Surface.Fill(new SKRectI(0, 0, 50, 50), new SKColor(0, 255, 0));
        layer.Offset = new SKPointI(300, 300);
        lock (doc.SyncRoot) { doc.Root.Add(layer); doc.ActiveLayer = layer; }

        using var compositor = new Compositor(doc);

        // 內容應該出現在 tile(1,1)（300..350 落在 256..512）
        var img = WaitForTile(compositor, new TileIndex(1, 1));
        Assert.NotNull(img);
        using var raster = new SKBitmap(Tile.Info);
        img!.ReadPixels(Tile.Info, raster.GetPixels(), Tile.RowBytes, 0, 0);
        Assert.Equal(new SKColor(0, 255, 0), raster.GetPixel(320 - 256, 320 - 256));
        Assert.Equal(SKColors.Empty, raster.GetPixel(200, 200)); // 未覆蓋處透明
    }
}
