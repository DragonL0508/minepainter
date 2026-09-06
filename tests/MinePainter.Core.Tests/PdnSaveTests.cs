using System.Text;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .pdn 匯出：整份文件合成後寫成單一圖層，物件圖照 paint.net 5.1 的樣子；
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

    [Fact]
    public void 多層文件_合併成單一圖層_像素與尺寸正確()
    {
        using var doc = new Document(40, 30);
        var bottom = new RasterLayer { Name = "底" };
        lock (doc.SyncRoot) bottom.Surface.Fill(new SKRectI(0, 0, 40, 30), SKColors.White);
        doc.Root.Add(bottom);
        var top = new RasterLayer { Name = "半透明紅" };
        lock (doc.SyncRoot) top.Surface.Fill(new SKRectI(10, 10, 20, 20), new SKColor(255, 0, 0, 128));
        doc.Root.Add(top);
        var text = new RasterLayer { Name = "字" };
        text.AddElement(new TextElement { Text = "Hi", FontSize = 16, Position = new SKPoint(22, 5), Color = SKColors.Black });
        doc.Root.Add(text);

        var stream = new MemoryStream();
        PdnFormat.Save(doc, stream, null, out var warnings);
        var bytes = stream.ToArray();
        Assert.Equal("PDN3"u8.ToArray(), bytes[..4]);
        var headerLength = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16);
        var header = Encoding.UTF8.GetString(bytes, 7, headerLength);
        Assert.StartsWith("<pdnImage width=\"40\" height=\"30\" layers=\"1\"", header);
        Assert.Contains("<thumb png=\"", header);
        Assert.Equal(0x00, bytes[7 + headerLength]);
        Assert.Equal(0x01, bytes[8 + headerLength]);

        var warning = Assert.Single(warnings);
        Assert.Contains("單一圖層", warning);

        stream.Position = 0;
        using var loaded = PdnFormat.Load(stream, out var loadWarnings);
        Assert.Empty(loadWarnings);
        Assert.Equal(40, loaded.Width);
        Assert.Equal(30, loaded.Height);
        var layer = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        Assert.Equal("背景", layer.Name);
        Assert.True(layer.IsVisible);
        Assert.Equal(BlendMode.Normal, layer.BlendMode);
        Assert.Equal(SKColors.White, Pixel(layer, 2, 2));
        var blended = Pixel(layer, 15, 15);   // 白底上半透明紅 → 粉紅
        Assert.Equal(255, blended.Alpha);
        Assert.Equal(255, blended.Red);
        Assert.InRange(blended.Green, 120, 135);
        var inked = false;
        for (var y = 5; y < 22 && !inked; y++)
            for (var x = 22; x < 40 && !inked; x++)
                inked = Pixel(layer, x, y).Red < 200;
        Assert.True(inked, "文字沒有烙進合成影像");
    }

    [Fact]
    public void 單層無效果_不提示_透明度保留為直通alpha()
    {
        using var doc = new Document(16, 16);
        var layer = new RasterLayer { Name = "只有一層" };
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(4, 4, 8, 8), new SKColor(0, 0, 255, 64));
        doc.Root.Add(layer);

        var stream = new MemoryStream();
        PdnFormat.Save(doc, stream, null, out var warnings);
        Assert.Empty(warnings);

        stream.Position = 0;
        using var loaded = PdnFormat.Load(stream, out _);
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        var p = Pixel(back, 5, 5);
        Assert.InRange(p.Alpha, 62, 66);
        Assert.Equal(255, p.Blue);
        Assert.Equal(SKColors.Transparent, Pixel(back, 1, 1));
    }

    [Fact]
    public void 快速模式_以輸出解析度合成()
    {
        using var doc = new Document(20, 10);
        doc.SetOutputSize(60, 30);
        var layer = new RasterLayer { Name = "底" };
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, 10, 10), SKColors.Green);
        doc.Root.Add(layer);

        var stream = new MemoryStream();
        PdnFormat.Save(doc, stream, null, out _);
        stream.Position = 0;
        using var loaded = PdnFormat.Load(stream, out _);
        Assert.Equal(60, loaded.Width);
        Assert.Equal(30, loaded.Height);
        var back = (RasterLayer)loaded.Root.Children[0];
        Assert.Equal(SKColors.Green, Pixel(back, 5, 5));
        Assert.Equal(SKColors.Transparent, Pixel(back, 50, 5));
    }
}
