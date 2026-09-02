using MinePainter.Core.Adjustments;
using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class MppFormatTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"mpp_test_{Guid.NewGuid():N}.mpp");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static SKColor GetLayerPixel(RasterLayer layer, int x, int y)
    {
        var idx = TileIndex.FromPixel(x, y);
        var tile = layer.Surface.GetTileForRead(idx);
        if (tile == null) return SKColors.Empty;
        var rect = idx.ToPixelRect();
        var offset = ((y - rect.Top) * Tile.Size + (x - rect.Left)) * 4;
        var s = tile.PixelSpan;
        return new SKColor(s[offset + 2], s[offset + 1], s[offset + 0], s[offset + 3]);
    }

    [Fact]
    public void ComplexDocument_RoundTrips()
    {
        // 建一份含群組 + 調整 + 向量 + offset raster 的文件
        using var doc = ImageCodec.CreateBlankDocument(512, 384, SKColors.White);
        var background = (RasterLayer)doc.Root.Children[0];

        var group = new GroupLayer { Name = "我的群組", Opacity = 0.8f, BlendMode = BlendMode.Multiply };
        var red = new RasterLayer { Name = "紅色", Offset = new SKPointI(50, 60) };
        red.Surface.Fill(new SKRectI(0, 0, 100, 100), new SKColor(255, 0, 0, 200));

        var adj = new AdjustmentLayer(new BrightnessContrastAdjustment(0.25f, -0.1f)) { Opacity = 0.7f };
        var hueAdj = new AdjustmentLayer(new HueSaturationAdjustment(45f, 0.5f, -0.2f)) { IsVisible = false };

        var vector = new RasterLayer { Name = "向量" };
        var text = new TextElement
        {
            Text = "多行\n測試",
            FontFamily = "Arial",
            FontSize = 36,
            Color = new SKColor(0, 100, 200),
            Position = new SKPoint(30, 40),
            ScaleX = 1.5f,
            Rotation = 30f,
            FontWeight = 900,
            Bold = true,
            Italic = true,
            Underline = true,
            Strikethrough = true,
            Alignment = TextAlign.Center,
        };
        var shape = new ShapeElement
        {
            Kind = ShapeKind.Ellipse,
            Rect = new SKRect(200, 100, 350, 220),
            FillColor = new SKColor(10, 220, 30, 128),
            StrokeColor = new SKColor(1, 2, 3),
            StrokeWidth = 5.5f,
        };

        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(red);
            group.Add(adj);
            doc.Root.Add(hueAdj);
            doc.Root.Add(vector);
            vector.AddElement(text);
            vector.AddElement(shape);
        }

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        // 文件與樹結構
        Assert.Equal(512, loaded.Width);
        Assert.Equal(384, loaded.Height);
        Assert.Equal(5, loaded.Root.Children.Count); // 文字一定自己一層：向量層的文字被拆出來

        var loadedBg = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
        Assert.Equal(background.Name, loadedBg.Name);
        Assert.Equal(SKColors.White, GetLayerPixel(loadedBg, 256, 192));

        var loadedGroup = Assert.IsType<GroupLayer>(loaded.Root.Children[1]);
        Assert.Equal("我的群組", loadedGroup.Name);
        Assert.Equal(0.8f, loadedGroup.Opacity, 2);
        Assert.Equal(BlendMode.Multiply, loadedGroup.BlendMode);
        Assert.Equal(2, loadedGroup.Children.Count);

        var loadedRed = Assert.IsType<RasterLayer>(loadedGroup.Children[0]);
        Assert.Equal(new SKPointI(50, 60), loadedRed.Offset);
        // 與存檔前的原始 premul 像素完全一致
        Assert.Equal(GetLayerPixel(red, 50, 50), GetLayerPixel(loadedRed, 50, 50));
        Assert.Equal(200, GetLayerPixel(loadedRed, 50, 50).Alpha);

        var loadedAdj = Assert.IsType<AdjustmentLayer>(loadedGroup.Children[1]);
        var bc = Assert.IsType<BrightnessContrastAdjustment>(loadedAdj.Adjustment);
        Assert.Equal(0.25f, bc.Brightness, 3);
        Assert.Equal(-0.1f, bc.Contrast, 3);
        Assert.Equal(0.7f, loadedAdj.Opacity, 2);

        var loadedHue = Assert.IsType<AdjustmentLayer>(loaded.Root.Children[2]);
        Assert.False(loadedHue.IsVisible);
        var hs = Assert.IsType<HueSaturationAdjustment>(loadedHue.Adjustment);
        Assert.Equal(45f, hs.Hue, 2);
        Assert.Equal(0.5f, hs.Saturation, 3);
        Assert.Equal(-0.2f, hs.Lightness, 3);

        var loadedShapes = Assert.IsType<RasterLayer>(loaded.Root.Children[3]);
        var loadedVector = Assert.IsType<RasterLayer>(loaded.Root.Children[4]);
        Assert.True(loadedVector.IsTextLayer);
        var loadedText = Assert.IsType<TextElement>(loadedVector.Elements[0]);
        Assert.Equal("多行\n測試", loadedText.Text);
        Assert.Equal("Arial", loadedText.FontFamily);
        Assert.Equal(36, loadedText.FontSize, 2);
        Assert.Equal(new SKColor(0, 100, 200), loadedText.Color);
        Assert.Equal(30, loadedText.Position.X, 2);
        Assert.Equal(1.5f, loadedText.ScaleX, 3);
        Assert.Equal(30f, loadedText.Rotation, 2);
        Assert.Equal(900, loadedText.FontWeight);
        Assert.True(loadedText.Bold);
        Assert.True(loadedText.Italic);
        Assert.True(loadedText.Underline);
        Assert.True(loadedText.Strikethrough);
        Assert.Equal(TextAlign.Center, loadedText.Alignment);

        var loadedShape = Assert.IsType<ShapeElement>(loadedShapes.Elements[0]);
        Assert.Equal(ShapeKind.Ellipse, loadedShape.Kind);
        Assert.Equal(new SKColor(10, 220, 30, 128), loadedShape.FillColor);
        Assert.Equal(new SKColor(1, 2, 3), loadedShape.StrokeColor);
        Assert.Equal(5.5f, loadedShape.StrokeWidth, 2);
        Assert.Equal(350, Math.Max(loadedShape.Rect.Left, loadedShape.Rect.Right), 1);

        // 向量元素保留可編輯（Id 一致）
        Assert.Equal(text.Id, loadedText.Id);
    }

    [Fact]
    public void Export_Png_MatchesComposite()
    {
        var exportPath = Path.ChangeExtension(_tempPath, ".png");
        try
        {
            using var doc = ImageCodec.CreateBlankDocument(128, 128, new SKColor(0, 128, 255));
            MppFormat.Export(doc, exportPath);

            using var decoded = SKBitmap.Decode(exportPath);
            Assert.Equal(128, decoded.Width);
            var px = decoded.GetPixel(64, 64);
            Assert.Equal(new SKColor(0, 128, 255), px);
        }
        finally
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    [Fact]
    public void EmptyRasterLayer_RoundTrips()
    {
        using var doc = new Document(256, 256);
        lock (doc.SyncRoot)
        {
            doc.Root.Add(new RasterLayer { Name = "空" });
        }

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);
        var layer = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
        Assert.Equal(0, layer.Surface.TileCount);
    }
}
