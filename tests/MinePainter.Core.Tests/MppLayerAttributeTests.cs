using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>圖層合成屬性（可見性／不透明度／混合模式）的 .mpp 往返。</summary>
public class MppLayerAttributeTests
{
    [Fact]
    public void RoundTrip_PreservesVisibilityOpacityAndBlend()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpp-attr-{Guid.NewGuid():N}.mpp");
        try
        {
            using (var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White))
            {
                var glow = new RasterLayer { Name = "光暈", Opacity = 0.45f, BlendMode = BlendMode.Screen };
                glow.Surface.Fill(new SKRectI(0, 0, 128, 128), SKColors.Yellow);
                doc.Root.Add(glow);

                var hidden = new RasterLayer { Name = "隱藏", IsVisible = false };
                hidden.Surface.Fill(new SKRectI(0, 0, 64, 64), SKColors.Purple);
                doc.Root.Add(hidden);

                MppFormat.Save(doc, path);
            }

            using var loaded = MppFormat.Load(path);
            var layers = loaded.Root.Children.OfType<RasterLayer>().ToDictionary(l => l.Name);

            var glowBack = layers["光暈"];
            Assert.True(glowBack.IsVisible, "半透明圖層讀回來應仍為可見");
            Assert.Equal(0.45f, glowBack.Opacity, 3);
            Assert.Equal(BlendMode.Screen, glowBack.BlendMode);

            var hiddenBack = layers["隱藏"];
            Assert.False(hiddenBack.IsVisible);
            Assert.Equal(1f, hiddenBack.Opacity, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
