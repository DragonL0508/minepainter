using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;
using Writer = MinePainter.Core.Tests.PsdFormatTests.PsdWriter;
using Desc = MinePainter.Core.Tests.PsdStyleAndTextTests.Desc;
using MinePainter.Core.Effects;

namespace MinePainter.Core.Tests;

public class PsdRenderingRegressionTests
{
    [Fact]
    public void PhotoshopMaskBoundsDoNotReceiveLayerPositionTwice()
    {
        var layer = new Writer.Layer("offset", new SKRectI(2, 1, 4, 2))
        {
            MaskRect = new SKRectI(2, 1, 4, 2), MaskDefault = 0, MaskFlags = 1,
            Channels = { [0] = [255, 255], [1] = [0, 0], [2] = [0, 0], [-1] = [255, 255], [-2] = [255, 0] },
        };
        using var doc = PsdFormat.Load(new MemoryStream(Writer.Build(6, 4, [layer])), out _);
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(2, 1));
        Assert.Equal(SKColors.Empty, bitmap.GetPixel(3, 1));
    }

    [Theory]
    [InlineData("mpp")]
    [InlineData("psd")]
    public void AllLayerMasksPreserveRawCoverageAndEditableSettings(string extension)
    {
        using var doc = new Document(4, 4);
        LayerNode[] nodes = [Solid(SKColors.Red), new AdjustmentLayer(new InvertAdjustment()), new GroupLayer()];
        foreach (var node in nodes)
        {
            node.Mask = new LayerMask(new SKRectI(1, 1, 3, 2), [32, 192], 255)
            { Feather = 2.5f, Density = 128 / 255f, Enabled = false, Inverted = true };
            doc.Root.Add(node);
        }
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{extension}");
        try
        {
            if (extension == "mpp") MppFormat.Save(doc, path);
            else PsdFormat.Save(doc, path);
            using var restored = extension == "mpp" ? MppFormat.Load(path) : PsdFormat.Load(path);
            Assert.Equal(3, restored.Root.Children.Count);
            for (var i = 0; i < nodes.Length; i++)
            {
                var mask = restored.Root.Children[i].Mask;
                Assert.NotNull(mask);
                Assert.Equal(nodes[i].Mask!.Bounds, mask.Bounds);
                Assert.Equal(new byte[] { 32, 192 }, mask.Alpha);
                Assert.Equal(255, mask.DefaultValue);
                Assert.Equal(2.5f, mask.Feather);
                Assert.Equal(128 / 255f, mask.Density);
                Assert.False(mask.Enabled);
                Assert.True(mask.Inverted);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ZeroFillDoesNotHideStandaloneRadialGradientOverlay()
    {
        var grad = new Desc.Obj("Grdn", [("Clrs", new List<object> {
            new Desc.Obj("Clrt", [("Clr ", Desc.Rgb(0, 0, 0)), ("Lctn", 0)]),
            new Desc.Obj("Clrt", [("Clr ", Desc.Rgb(255, 255, 255)), ("Lctn", 4096)]) })]);
        var layer = new Writer.Layer("gradient", new SKRectI(0, 0, 4, 4)) { Opacity = 128,
            Channels = { [0] = new byte[16], [1] = new byte[16], [2] = new byte[16],
                [-1] = Enumerable.Repeat((byte)255, 16).ToArray() } };
        layer.Blocks["iOpa"] = [0];
        layer.Blocks["lfx2"] = Desc.Lfx2(("GrFl", Desc.Fx(("Grad", grad),
            ("Type", Desc.Enum("GrdT", "Rdl ")), ("Md  ", Desc.Enum("BlnM", "Mltp")))));
        using var stream = new MemoryStream(Writer.Build(4, 4, [layer]));
        using var doc = PsdFormat.Load(stream, out var notes);
        Assert.Empty(notes);
        var loaded = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Equal(128f / 255, loaded.Opacity);
        Assert.Equal(BlendMode.Multiply, loaded.BlendMode);
        Assert.True(Assert.IsType<ObjectGradientEffect>(Assert.Single(loaded.Effects).Effect).Radial);
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.InRange(bitmap.GetPixel(1, 1).Alpha, 127, 129);
    }

    private static RasterLayer Solid(SKColor color, BlendMode blend = BlendMode.Normal)
    {
        var layer = new RasterLayer { BlendMode = blend };
        layer.Surface.Fill(new SKRectI(0, 0, 4, 4), color);
        return layer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Curves_ReadPhotoshopHeaderAndLegacyMinePainterHeader(bool legacy)
    {
        // is-map byte, version 1, composite bitmap, three output/input pairs.
        var bytes = Convert.FromHexString("0000010000000100030000001C00550075007C00FF");
        if (legacy) bytes = bytes[1..];
        var adjustment = Assert.IsType<CurvesAdjustment>(PsdAdjustmentLayer.TryBuild(
            new Dictionary<string, byte[]> { ["curv"] = bytes }, [], out var failure));
        Assert.Null(failure);
        Assert.Equal(3, adjustment.Curves[0].Count);
        Assert.Equal(28f / 255, adjustment.Curves[0][0].X);
        Assert.Equal(124f / 255, adjustment.Curves[0][2].Y);
    }

    [Fact]
    public void Curves_TruncatedPointsAreRejected()
    {
        Assert.Null(PsdAdjustmentLayer.TryBuild(new Dictionary<string, byte[]>
            { ["curv"] = Convert.FromHexString("0000010000000100030000001C") }, [], out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void Curves_MasterAndChannelBothAffectPixelsAndSurvivePersistence()
    {
        var curves = new CurvesAdjustment { Mode = CurvesAdjustment.ModeRgb,
            MasterCurve = [(0, 0), (1, 0.5f)],
            Curves = [[(0, 0.5f), (1, 1)], CurvesAdjustment.Identity, CurvesAdjustment.Identity] };
        using var doc = new Document(4, 4);
        doc.Root.Add(Solid(new SKColor(100, 100, 100)));
        doc.Root.Add(new AdjustmentLayer(CurvesAdjustment.Load(curves.SaveParams())));
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        var p = bitmap.GetPixel(0, 0);
        Assert.InRange(p.Red, 88, 90);
        Assert.InRange(p.Green, 49, 51);
        using var stream = new MemoryStream();
        PsdFormat.Save(doc, stream, null, out _);
        stream.Position = 0;
        using var reloaded = PsdFormat.Load(stream, out var notes);
        Assert.Empty(notes);
        var restored = Assert.IsType<CurvesAdjustment>(Assert.IsType<AdjustmentLayer>(reloaded.Root.Children[1]).Adjustment);
        Assert.InRange(restored.MasterCurve[1].Y, 0.49f, 0.51f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupMaskAppliesOnceToWholeGroup(bool pass)
    {
        using var doc = new Document(4, 4);
        doc.Root.Add(Solid(SKColors.Black));
        var group = new GroupLayer { IsPassThrough = pass,
            Mask = new LayerMask(new SKRectI(0, 0, 3, 1), [0, 128, 255], 0) };
        group.Add(Solid(SKColors.Red));
        group.Add(Solid(SKColors.Blue));
        doc.Root.Add(group);
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Black, bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 128), bitmap.GetPixel(1, 0));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(2, 0));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(3, 0));
    }

    [Fact]
    public void PassThroughAdjustmentSeesExternalBackdropAndRespectsGroupOpacity()
    {
        using var doc = new Document(4, 4);
        doc.Root.Add(Solid(new SKColor(100, 100, 100)));
        var group = new GroupLayer { IsPassThrough = true, Opacity = 0.5f };
        group.Add(new AdjustmentLayer(new InvertAdjustment()));
        doc.Root.Add(group);
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.InRange(bitmap.GetPixel(0, 0).Red, 127, 128);
    }

    [Fact]
    public void PhotoshopSectionBlendOverridesOrdinaryLayerBlend()
    {
        var group = new Writer.Layer("group", SKRectI.Empty);
        group.Blocks["lsct"] = Convert.FromHexString("000000013842494D7061737300000000");
        using var stream = new MemoryStream(Writer.Build(4, 4,
            [new Writer.Layer("end", SKRectI.Empty) { SectionType = 3 }, group]));
        using var doc = PsdFormat.Load(stream, out var notes);
        Assert.Empty(notes);
        Assert.True(Assert.IsType<GroupLayer>(Assert.Single(doc.Root.Children)).IsPassThrough);
    }

    [Fact]
    public void RestrictedChannelsDoNotBecomeBrighterUnderAdditiveBlend()
    {
        using var doc = new Document(4, 4);
        doc.Root.Add(Solid(new SKColor(100, 100, 100)));
        var layer = Solid(new SKColor(100, 100, 100), BlendMode.Additive);
        layer.RestrictedChannels = 6;
        doc.Root.Add(layer);
        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(new SKColor(200, 100, 100), bitmap.GetPixel(0, 0));
    }

    [Theory]
    [InlineData("psd")]
    [InlineData("mpp")]
    public void GroupMaskPassThroughAndRestrictionsSurviveSave(string extension)
    {
        using var doc = new Document(4, 4);
        doc.Root.Add(Solid(new SKColor(100, 100, 100)));
        var group = new GroupLayer { IsPassThrough = true,
            Mask = new LayerMask(new SKRectI(0, 0, 2, 1), [0, 128], 255) };
        var layer = Solid(new SKColor(100, 100, 100), BlendMode.Additive);
        layer.RestrictedChannels = 6;
        group.Add(layer);
        doc.Root.Add(group);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{extension}");
        try
        {
            if (extension == "mpp") MppFormat.Save(doc, path);
            else { using var file = File.Create(path); PsdFormat.Save(doc, file, null, out _); }
            using var loaded = extension == "mpp" ? MppFormat.Load(path) : PsdFormat.Load(path);
            var restored = Assert.IsType<GroupLayer>(loaded.Root.Children[1]);
            Assert.True(restored.IsPassThrough);
            Assert.NotNull(restored.Mask);
            Assert.Equal(group.Mask.Alpha, restored.Mask.Alpha);
            Assert.Equal(255, restored.Mask.DefaultValue);
            Assert.Equal(6, restored.Children[0].RestrictedChannels);
            using var expected = Compositor.RenderComposite(doc);
            using var actual = Compositor.RenderComposite(loaded);
            using var a = SKBitmap.FromImage(expected);
            using var b = SKBitmap.FromImage(actual);
            Assert.Equal(a.Bytes, b.Bytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FastModeCloneScalesGroupMaskAndKeepsRestrictions()
    {
        using var doc = new Document(4, 4);
        var group = new GroupLayer { IsPassThrough = true,
            Mask = new LayerMask(new SKRectI(1, 1, 2, 2), [0], 255) };
        var layer = Solid(SKColors.Blue);
        layer.RestrictedChannels = 1;
        group.Add(layer);
        doc.Root.Add(group);
        using var clone = OutputRender.CloneScaled(doc, 8, 8);
        var copy = Assert.IsType<GroupLayer>(Assert.Single(clone.Root.Children));
        Assert.True(copy.IsPassThrough);
        Assert.Equal(new SKRectI(2, 2, 4, 4), copy.Mask!.Bounds);
        Assert.Equal(0, copy.Mask.At(3, 3));
        Assert.Equal(255, copy.Mask.At(4, 4));
        Assert.Equal(1, copy.Children[0].RestrictedChannels);
    }
}
