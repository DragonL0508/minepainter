using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class LayerMaskRenderingTests
{
    [Fact]
    public void ReplacingMaskSourceDoesNotReuseDerivedCoverage()
    {
        var mask = new LayerMask(new SKRectI(0, 0, 1, 1), [0], 255) { Density = .5f };
        Assert.Equal(128, mask.At(0, 0));
        var moved = mask.Translated(5, 0);
        Assert.Equal(255, moved.At(0, 0));
        Assert.Equal(128, moved.At(5, 0));
        Assert.Equal(255, (mask with { Alpha = [255] }).At(0, 0));
        Assert.Equal(128, (mask with { DefaultValue = 0 }).At(2, 0));
    }

    [Fact]
    public void DensityAndInversionRespectOutsideCoverage()
    {
        var mask = new LayerMask(new SKRectI(0, 0, 2, 1), [0, 255], 0) { Density = .5f };
        Assert.Equal(128, mask.At(0, 0));
        Assert.Equal(255, mask.At(1, 0));
        Assert.Equal(128, mask.At(4, 0));
        var inverted = mask with { Inverted = true };
        Assert.Equal(255, inverted.At(0, 0));
        Assert.Equal(128, inverted.At(1, 0));
        Assert.Equal(255, inverted.At(4, 0));
    }

    [Fact]
    public void FeatherSpreadsCoverageSmoothlyBeyondOriginalBounds()
    {
        var mask = new LayerMask(new SKRectI(0, 0, 5, 5), Enumerable.Repeat((byte)255, 25).ToArray(), 0) { Feather = 1 };
        var center = mask.At(2, 2);
        var edge = mask.At(0, 2);
        var outside = mask.At(-1, 2);
        Assert.InRange(center, 220, 255);
        Assert.True(center > edge && edge > outside && outside > 0);
        Assert.Equal(mask.At(-1, 2), mask.At(5, 2));
        Assert.Equal(0, mask.At(-5, 2));
    }

    [Fact]
    public void DisabledMaskRetainsEditableRasterPixels()
    {
        using var doc = new Document(4, 4);
        var layer = new RasterLayer { Mask = new LayerMask(doc.Bounds, new byte[16], 0) };
        layer.Surface.Fill(doc.Bounds, SKColors.Red);
        doc.Root.Add(layer);
        using (var hidden = Compositor.RenderComposite(doc))
        using (var bitmap = SKBitmap.FromImage(hidden)) Assert.Equal(0, bitmap.GetPixel(1, 1).Alpha);
        layer.Mask = layer.Mask with { Enabled = false };
        layer.InvalidateAll();
        using var visible = Compositor.RenderComposite(doc);
        using var restored = SKBitmap.FromImage(visible);
        Assert.Equal(SKColors.Red, restored.GetPixel(1, 1));
    }
}
