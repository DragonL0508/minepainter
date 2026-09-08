using MinePainter.App.Rendering;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

public class AdjustmentPipelineTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(10, false)]
    [InlineData(5, true)]
    public void AdjustmentStack_DrawsSourceOnceAndMatchesExport(int count, bool nested)
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent);
        var bottom = (RasterLayer)doc.ActiveLayer!;
        var group = doc.Root;
        if (nested)
        {
            bottom.Surface.Fill(doc.Bounds, SKColors.DarkBlue);
            group = new GroupLayer { Opacity = 0.7f };
            doc.Root.Add(group);
            bottom = new RasterLayer();
            group.Add(bottom);
        }
        bottom.Surface.Fill(new SKRectI(4, 7, 55, 49), new SKColor(100, 60, 190, 137));
        for (var i = 0; i < count; i++)
            group.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(0.03f, -0.04f))
            { Opacity = i % 2 == 0 ? 0.4f : 0.7f });
        var session = new EditorSession(doc);
        using var compositor = session.Compositor;
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        lock (doc.SyncRoot)
            Assert.True(renderer.TryDraw(target.Canvas, session, doc.Bounds));
        Assert.Equal(nested ? 2 : 1, renderer.LastTiles);
        using var actual = target.Snapshot();
        using var expected = Compositor.RenderComposite(doc);
        using var a = SKBitmap.FromImage(actual);
        using var b = SKBitmap.FromImage(expected);
        Assert.Equal(b.Bytes, a.Bytes);
    }

    [Fact]
    public void AdjustmentStack_PreservesViewportTransformAndClip()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Red);
        doc.Root.Add(new AdjustmentLayer(new BrightnessContrastAdjustment()) { Opacity = 0.5f });
        var session = new EditorSession(doc);
        using var compositor = session.Compositor;
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(160, 160, SKColorType.Bgra8888, SKAlphaType.Premul));
        target.Canvas.Clear(SKColors.Blue);
        target.Canvas.ClipRect(new SKRect(20, 25, 95, 100));
        target.Canvas.Translate(11, 13);
        target.Canvas.Scale(2);
        var matrix = target.Canvas.TotalMatrix;
        lock (doc.SyncRoot)
            Assert.True(renderer.TryDraw(target.Canvas, session, doc.Bounds, 2));
        Assert.Equal(matrix, target.Canvas.TotalMatrix);
        using var image = target.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(30, 30));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(19, 30));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(95, 30));
    }
}
