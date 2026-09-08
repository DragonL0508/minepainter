using MinePainter.App.Rendering;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class GpuImageCacheTests
{
    [Fact]
    public void LodRebuild_CopiesOnlyChangedSourceAndSurvivesZoomRoundTrip()
    {
        using var doc = ImageCodec.CreateBlankDocument(2048, 2048, SKColors.Red);
        var session = new EditorSession(doc);
        using var compositor = session.Compositor;
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        void Draw(double scale)
        {
            target.Canvas.Clear(SKColors.Transparent);
            target.Canvas.Save();
            target.Canvas.Scale((float)scale);
            lock (doc.SyncRoot) Assert.True(renderer.TryDraw(target.Canvas, session, doc.Bounds, scale));
            target.Canvas.Restore();
        }

        Draw(0.125);
        Assert.Equal(64, renderer.LastSourceCopies);
        Assert.Equal(1, renderer.LastLodBuilds);
        Draw(0.125);
        Assert.Equal(0, renderer.LastSourceCopies);
        Assert.Equal(0, renderer.LastLodBuilds);
        for (var i = 0; i < 8; i++) Draw(1);
        Draw(0.125);
        Assert.Equal(0, renderer.LastLodBuilds);
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 256, 256), SKColors.Blue);
        Draw(0.125);
        Assert.Equal(1, renderer.LastSourceCopies);
        Assert.Equal(1, renderer.LastLodBuilds);
        using var image = target.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(10, 10));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(100, 100));
        lock (doc.SyncRoot) layer.Surface.RemoveTile(new TileIndex(0, 0));
        Draw(0.125);
        Assert.Equal(1, renderer.LastLodBuilds);
        using var removed = target.Snapshot();
        using var removedBitmap = SKBitmap.FromImage(removed);
        Assert.Equal(0, removedBitmap.GetPixel(10, 10).Alpha);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void CacheBudget_EvictsWithoutChangingRenderedPixels(int tiles)
    {
        using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.Red);
        var session = new EditorSession(doc);
        using var compositor = session.Compositor;
        using var renderer = new GpuLayerRenderer { ImageCacheBudgetBytes = tiles * Tile.BytesPerTile };
        using var target = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        target.Canvas.Scale(0.5f);
        for (var frame = 0; frame < 3; frame++)
        {
            target.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot) Assert.True(renderer.TryDraw(target.Canvas, session, doc.Bounds, 0.5));
            Assert.InRange(renderer.CachedImageBytes, 0, renderer.ImageCacheBudgetBytes);
            using var image = target.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            Assert.Equal(SKColors.Red, bitmap.GetPixel(128, 128));
        }
        lock (doc.SyncRoot) doc.Root.Remove(doc.ActiveLayer!);
        lock (doc.SyncRoot) Assert.True(renderer.TryDraw(target.Canvas, session, doc.Bounds, 0.5));
        Assert.Equal(0, renderer.CachedImageBytes);
    }
}
