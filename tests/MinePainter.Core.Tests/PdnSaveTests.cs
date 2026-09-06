using System.Text;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .pdn 匯出：每層一層、群組拆開、效果與文字烙成像素，物件圖照 paint.net 5.1 的樣子；
/// 寫出去要能被自己的讀取器讀回來、像素（直通 alpha）要對。
/// </summary>
public class PdnSaveTests
{
    private static SKColor Pixel(RasterLayer layer, int x, int y)
    {
        var px = LayerEffectRenderer.ReadPixels(layer.Surface, new SKRectI(x, y, x + 1, y + 1))[0];
        var a = (byte)(px >> 24);
        if (a == 0) return SKColors.Transparent;
        return new SKColor((byte)(((px >> 16) & 0xFF) * 255 / a), (byte)(((px >> 8) & 0xFF) * 255 / a), (byte)((px & 0xFF) * 255 / a), a);
    }

    private static RasterLayer Filled(Document doc, string name, SKRectI rect, SKColor color)
    {
        var layer = new RasterLayer { Name = name };
        lock (doc.SyncRoot) layer.Surface.Fill(rect, color);
        return layer;
    }

    private static Document RoundTrip(Document doc, out IReadOnlyList<string> warnings, out byte[] bytes)
    {
        var stream = new MemoryStream();
        PdnFormat.Save(doc, stream, null, out warnings);
        bytes = stream.ToArray();
        stream.Position = 0;
        return PdnFormat.Load(stream, out _);
    }

    [Fact]
    public void 每層一層_群組拆開_屬性與像素保留()
    {
        using var doc = new Document(40, 30);
        doc.Root.Add(Filled(doc, "底", new SKRectI(0, 0, 40, 30), SKColors.White));
        var group = new GroupLayer { Name = "群組" };
        var red = Filled(doc, "紅", new SKRectI(10, 10, 20, 20), new SKColor(255, 0, 0, 128));
        red.BlendMode = BlendMode.Multiply;
        red.Opacity = 0.5f;
        group.Add(red);
        var hidden = Filled(doc, "藏起來", new SKRectI(0, 0, 5, 5), SKColors.Blue);
        hidden.IsVisible = false;
        group.Add(hidden);
        doc.Root.Add(group);
        var text = new RasterLayer { Name = "字" };
        text.AddElement(new TextElement { Text = "Hi", FontSize = 16, Position = new SKPoint(22, 5), Color = SKColors.Black });
        doc.Root.Add(text);

        using var loaded = RoundTrip(doc, out var warnings, out var bytes);

        Assert.Empty(warnings);
        Assert.Equal("PDN3"u8.ToArray(), bytes[..4]);
        var headerLength = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16);
        var header = Encoding.UTF8.GetString(bytes, 7, headerLength);
        Assert.StartsWith("<pdnImage width=\"40\" height=\"30\" layers=\"4\"", header);
        Assert.Contains("<thumb png=\"", header);

        Assert.Equal(4, loaded.Root.Children.Count);
        var names = loaded.Root.Children.Select(n => n.Name).ToList();
        Assert.Equal(["底", "紅", "藏起來", "字"], names);

        var bottom = (RasterLayer)loaded.Root.Children[0];
        Assert.Equal(SKColors.White, Pixel(bottom, 2, 2));

        var redBack = (RasterLayer)loaded.Root.Children[1];
        Assert.Equal(BlendMode.Multiply, redBack.BlendMode);
        Assert.Equal(0.5f, redBack.Opacity, 2);
        var p = Pixel(redBack, 15, 15);
        Assert.Equal(255, p.Red);
        Assert.InRange(p.Alpha, 126, 130);
        Assert.Equal(SKColors.Transparent, Pixel(redBack, 2, 2));   // 不是合成的：底層的白沒混進來

        Assert.False(loaded.Root.Children[2].IsVisible);
        Assert.Equal(SKColors.Blue, Pixel((RasterLayer)loaded.Root.Children[2], 2, 2));

        var textBack = (RasterLayer)loaded.Root.Children[3];
        Assert.False(textBack.IsTextLayer);
        var inked = false;
        for (var y = 5; y < 22 && !inked; y++)
            for (var x = 22; x < 40 && !inked; x++)
                inked = Pixel(textBack, x, y).Alpha > 0;
        Assert.True(inked, "文字沒有烙進圖層");
        Assert.Equal(SKColors.Transparent, Pixel(textBack, 2, 2));
    }

    [Fact]
    public void 群組有不透明度或效果_整組合成一層並提示_調整圖層略過()
    {
        using var doc = new Document(30, 30);
        doc.Root.Add(Filled(doc, "底", new SKRectI(0, 0, 30, 30), SKColors.White));
        var group = new GroupLayer { Name = "半透明組", Opacity = 0.5f };
        group.Add(Filled(doc, "甲", new SKRectI(0, 0, 10, 10), SKColors.Red));
        group.Add(Filled(doc, "乙", new SKRectI(20, 20, 30, 30), SKColors.Blue));
        doc.Root.Add(group);
        doc.Root.Add(new AdjustmentLayer(new InvertAdjustment()) { Name = "負片" });
        var hiddenGroup = new GroupLayer { Name = "藏著的組", IsVisible = false };
        hiddenGroup.Add(Filled(doc, "丙", new SKRectI(0, 20, 10, 30), SKColors.Green));
        doc.Root.Add(hiddenGroup);
        var effects = Filled(doc, "糊", new SKRectI(12, 12, 18, 18), SKColors.Black);
        effects.SetEffects([LayerEffect.Create(new GaussianBlurEffect { Radius = 3 })]);
        effects.BlendMode = BlendMode.Hue;
        doc.Root.Add(effects);

        using var loaded = RoundTrip(doc, out var warnings, out _);

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("半透明組") && w.Contains("合成一層"));
        Assert.Contains(warnings, w => w.Contains("負片") && w.Contains("略過"));
        Assert.Contains(warnings, w => w.Contains("糊") && w.Contains("混合模式"));

        Assert.Equal(["底", "半透明組", "丙", "糊"], loaded.Root.Children.Select(n => n.Name).ToList());
        var merged = (RasterLayer)loaded.Root.Children[1];
        Assert.Equal(0.5f, merged.Opacity, 2);
        Assert.Equal(SKColors.Red, Pixel(merged, 2, 2));
        Assert.Equal(SKColors.Blue, Pixel(merged, 25, 25));
        Assert.False(loaded.Root.Children[2].IsVisible);   // 群組藏著 → 子層藏著
        var blurred = (RasterLayer)loaded.Root.Children[3];
        Assert.Equal(BlendMode.Normal, blurred.BlendMode);
        Assert.True(Pixel(blurred, 10, 15).Alpha > 0, "模糊效果沒有烙進像素");
    }

    [Fact]
    public void 單層無效果_不提示_透明度保留為直通alpha()
    {
        using var doc = new Document(16, 16);
        var layer = new RasterLayer { Name = "只有一層" };
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(4, 4, 8, 8), new SKColor(0, 0, 255, 64));
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings, out _);
        Assert.Empty(warnings);
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        Assert.Equal("只有一層", back.Name);
        var p = Pixel(back, 5, 5);
        Assert.InRange(p.Alpha, 62, 66);
        Assert.Equal(255, p.Blue);
        Assert.Equal(SKColors.Transparent, Pixel(back, 1, 1));
    }

    [Fact]
    public void 快速模式_以輸出解析度寫()
    {
        using var doc = new Document(20, 10);
        doc.SetOutputSize(60, 30);
        var layer = new RasterLayer { Name = "底" };
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 10, 10), SKColors.Green);
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out _, out _);
        Assert.Equal(60, loaded.Width);
        Assert.Equal(30, loaded.Height);
        var back = (RasterLayer)loaded.Root.Children[0];
        Assert.Equal(SKColors.Green, Pixel(back, 5, 5));
        Assert.Equal(SKColors.Transparent, Pixel(back, 50, 5));
    }
}
