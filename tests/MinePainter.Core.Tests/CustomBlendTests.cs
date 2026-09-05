using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

/// <summary>Photoshop 專有、Skia 沒有的混合模式：公式、合成器整合、向下合併、.mpp 往返。</summary>
public class CustomBlendTests
{
    [Theory]
    [InlineData(BlendMode.LinearBurn, 0.6f, 0.7f, 0.3f)]
    [InlineData(BlendMode.LinearLight, 0.4f, 0.6f, 0.6f)]
    [InlineData(BlendMode.Subtract, 0.8f, 0.3f, 0.5f)]
    [InlineData(BlendMode.Divide, 0.25f, 0.5f, 0.5f)]
    [InlineData(BlendMode.PinLight, 0.8f, 0.2f, 0.4f)]   // s<0.5 → min(b, 2s)
    [InlineData(BlendMode.HardMix, 0.9f, 0.9f, 1f)]
    [InlineData(BlendMode.HardMix, 0.1f, 0.1f, 0f)]
    public void Channel_MatchesPhotoshopFormulas(BlendMode mode, float backdrop, float source, float expected)
    {
        Assert.Equal(expected, CustomBlend.Channel(backdrop, source, mode), 3);
    }

    [Fact]
    public void VividLight_BurnsBelowHalf_DodgesAboveHalf()
    {
        Assert.True(CustomBlend.Channel(0.5f, 0.25f, BlendMode.VividLight) < 0.5f);
        Assert.True(CustomBlend.Channel(0.5f, 0.75f, BlendMode.VividLight) > 0.5f);
        Assert.Equal(0.5f, CustomBlend.Channel(0.5f, 0.5f, BlendMode.VividLight), 2);
    }

    [Fact]
    public void Blend_DarkerColorPicksTheDarkerPixel_AndAlphaComposites()
    {
        var dark = Premul(20, 20, 20, 255);
        var light = Premul(200, 200, 200, 255);
        Assert.Equal(dark, CustomBlend.Blend(light, dark, BlendMode.DarkerColor));
        Assert.Equal(light, CustomBlend.Blend(light, dark, BlendMode.LighterColor));

        // 來源半透明：混合色與底色各佔一半
        var half = Premul(0, 0, 0, 128);   // 黑，alpha 128
        var over = CustomBlend.Blend(half, Premul(200, 200, 200, 255), BlendMode.Subtract);
        Assert.Equal(255, A(over));
        Assert.InRange(R(over), 195, 201);   // 減去 0 沒變，只是 alpha 合成

        Assert.Equal(dark, CustomBlend.Blend(0, dark, BlendMode.LinearBurn));   // 透明來源不改變底
        Assert.Equal(light, CustomBlend.Blend(light, 0, BlendMode.LinearBurn)); // 透明底：來源直接落下
    }

    private static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bmp = SKBitmap.FromImage(image);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Compositor_AppliesCustomBlendToLayerAndGroup()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, new SKColor(200, 200, 200));
        var top = new RasterLayer { Name = "top", BlendMode = BlendMode.LinearBurn };
        var group = new GroupLayer { Name = "g", BlendMode = BlendMode.Subtract, Opacity = 0.5f };
        var inGroup = new RasterLayer { Name = "in" };
        lock (doc.SyncRoot)
        {
            top.Surface.Fill(new SKRectI(0, 0, 32, 64), new SKColor(100, 100, 100));
            inGroup.Surface.Fill(new SKRectI(32, 0, 64, 64), new SKColor(100, 100, 100));
            doc.Root.Add(top);
            group.Add(inGroup);
            doc.Root.Add(group);
        }

        using var image = OutputRender.Render(doc);
        // 線性加深：200/255 + 100/255 − 1 ≈ 0.176 → 45
        Assert.InRange(Pixel(image, 10, 10).Red, 43, 47);
        // 群組減去，不透明度 50%：底 200 − 100 = 100，與 200 各一半 → 150
        Assert.InRange(Pixel(image, 50, 10).Red, 147, 153);
    }

    [Fact]
    public void MergeDown_UsesCustomBlend()
    {
        using var doc = ImageCodec.CreateBlankDocument(32, 32, new SKColor(200, 200, 200));
        using var session = new EditorSession(doc);
        var top = new RasterLayer { Name = "top", BlendMode = BlendMode.Subtract };
        lock (doc.SyncRoot)
        {
            top.Surface.Fill(new SKRectI(0, 0, 32, 32), new SKColor(50, 50, 50));
            doc.Root.Add(top);
        }

        Assert.True(LayerCommands.MergeLayerDown(doc, session.History, top));
        var background = (RasterLayer)doc.Root.Children[0];
        var p = BackgroundRemovalCommand.ReadRegion(background.Surface, new SKRectI(5, 5, 6, 6))[0];
        Assert.InRange(R(p), 148, 152);
    }

    [Fact]
    public void Mpp_RoundTripsCustomBlendNames()
    {
        using var doc = ImageCodec.CreateBlankDocument(4, 4, SKColors.White);
        lock (doc.SyncRoot) doc.Root.Children[0].BlendMode = BlendMode.VividLight;
        var path = Path.Combine(Path.GetTempPath(), $"blend_{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, path);
            using var loaded = MppFormat.Load(path);
            Assert.Equal(BlendMode.VividLight, loaded.Root.Children[0].BlendMode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
