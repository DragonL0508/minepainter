using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class ExportReproTests
{
    [Fact]
    public void Export_Png_ContainsTextAndPixels()
    {
        using var doc = ImageCodec.CreateBlankDocument(400, 300, SKColors.White);
        var layer = (RasterLayer)doc.Root.Children[0];
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(20, 20, 120, 120), new SKColor(255, 0, 0));
            layer.AddElement(new TextElement
            {
                Text = "測試 Test",
                FontFamily = "Arial", // 逼出 CJK 後備
                FontSize = 36,
                FontWeight = 900,
                Alignment = TextAlign.Center,
                Position = new SKPoint(140, 40),
                Color = SKColors.Black,
            });
            layer.AddElement(new TextElement
            {
                Text = "旋轉",
                FontFamily = "Microsoft JhengHei",
                FontSize = 32,
                Rotation = 30f,
                Position = new SKPoint(160, 150),
                Color = new SKColor(0, 0, 200),
            });
        }

        var dir = Path.Combine(Path.GetTempPath(), "minepainter-export-repro");
        Directory.CreateDirectory(dir);
        var pngPath = Path.Combine(dir, "repro.png");
        var scaledPath = Path.Combine(dir, "repro-scaled.png");
        var jpgPath = Path.Combine(dir, "repro.jpg");

        MppFormat.Export(doc, pngPath);
        MppFormat.Export(doc, scaledPath, width: 200, height: 150);
        MppFormat.Export(doc, jpgPath, jpegQuality: 90);

        using var loaded = SKBitmap.Decode(pngPath);
        Assert.Equal(400, loaded.Width);
        Assert.Equal(300, loaded.Height);
        Assert.Equal(new SKColor(255, 0, 0), loaded.GetPixel(70, 70));   // 紅色方塊
        Assert.Equal(SKColors.White, loaded.GetPixel(390, 290));          // 背景

        // 文字區域應該有黑色墨水
        var ink = 0;
        for (var y = 30; y < 100; y++)
            for (var x = 130; x < 390; x++)
                if (loaded.GetPixel(x, y) is { Red: < 100, Green: < 100, Blue: < 100 }) ink++;
        Assert.True(ink > 50, $"文字墨水像素只有 {ink}");

        using var scaled = SKBitmap.Decode(scaledPath);
        Assert.Equal(200, scaled.Width);
        Assert.Equal(150, scaled.Height);
    }
}
