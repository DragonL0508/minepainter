using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 遮色片／限制通道／直通群組的合成走的是快速路徑（整格預設值不逐像素算、整列一次取覆蓋值）。
/// 這裡用「數學上等價的另一種做法」當對照：遮色片＝把圖層 alpha 乘上覆蓋值，
/// 結果必須一樣；快速路徑的分類（整格 255／整格 0／部分）錯了，跨 tile 的格子就會對不上。
/// 文件刻意大於一格（256）：讓同一張遮色片在不同格子分別落入三種情況。
/// </summary>
public class MaskCompositingFastPathTests
{
    private const int Size = 400; // 跨 2×2 格

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    [InlineData((byte)90)]
    public void 遮色片等於把圖層alpha乘上覆蓋值(byte defaultValue)
    {
        var mask = GradientMask(new SKRectI(180, 60, 330, 300), defaultValue);
        using var masked = new Document(Size, Size);
        masked.Root.Add(Solid(masked.Bounds, new SKColor(40, 90, 200)));
        var top = Solid(masked.Bounds, new SKColor(230, 120, 30, 200));
        top.Mask = mask;
        top.Opacity = .8f;
        masked.Root.Add(top);

        using var scaled = new Document(Size, Size);
        scaled.Root.Add(Solid(scaled.Bounds, new SKColor(40, 90, 200)));
        var prescaled = AlphaScaled(scaled.Bounds, new SKColor(230, 120, 30, 200), mask);
        prescaled.Opacity = .8f;
        scaled.Root.Add(prescaled);

        AssertSameComposite(masked, scaled, tolerance: 2);
    }

    [Fact]
    public void 限制通道保留畫之前的通道值()
    {
        using var doc = new Document(Size, Size);
        doc.Root.Add(Solid(doc.Bounds, new SKColor(40, 90, 200)));
        var top = Solid(doc.Bounds, new SKColor(100, 100, 100));
        top.BlendMode = BlendMode.Additive;
        top.RestrictedChannels = 6; // bit1＝G、bit2＝B 不參與：只有 R 會被加亮
        doc.Root.Add(top);

        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        foreach (var (x, y) in new[] { (10, 10), (300, 20), (20, 300), (390, 390) })
        {
            var c = bitmap.GetPixel(x, y);
            Assert.Equal(140, c.Red);   // 40 + 100
            Assert.Equal(90, c.Green);  // 還原
            Assert.Equal(200, c.Blue);  // 還原
        }
    }

    [Fact]
    public void 限制通道加遮色片只在覆蓋處生效()
    {
        using var doc = new Document(Size, Size);
        doc.Root.Add(Solid(doc.Bounds, new SKColor(40, 90, 200)));
        var top = Solid(doc.Bounds, new SKColor(100, 100, 100));
        top.BlendMode = BlendMode.Additive;
        top.RestrictedChannels = 6;
        top.Mask = new LayerMask(new SKRectI(300, 300, 350, 350), Filled(50 * 50, 255), 0); // 只有右下一小塊
        doc.Root.Add(top);

        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(new SKColor(140, 90, 200), bitmap.GetPixel(320, 320));
        Assert.Equal(new SKColor(40, 90, 200), bitmap.GetPixel(10, 10));   // 整格 0：這層在這格根本沒畫
        Assert.Equal(new SKColor(40, 90, 200), bitmap.GetPixel(290, 290)); // 同一格內、遮色片外
    }

    [Fact]
    public void 直通群組的遮色片與不透明度作用在整組差異上()
    {
        using var doc = new Document(Size, Size);
        doc.Root.Add(Solid(doc.Bounds, new SKColor(40, 90, 200)));
        var group = new GroupLayer
        {
            IsPassThrough = true,
            Opacity = .5f,
            Mask = new LayerMask(new SKRectI(0, 0, 100, 100), Filled(100 * 100, 0), 255), // 左上挖洞
        };
        group.Add(Solid(doc.Bounds, new SKColor(240, 240, 240)));
        doc.Root.Add(group);

        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(new SKColor(40, 90, 200), bitmap.GetPixel(50, 50)); // 洞裡＝畫之前
        var outside = bitmap.GetPixel(300, 300);                            // 洞外＝一半
        Assert.InRange(outside.Red, 139, 141);
        Assert.InRange(outside.Green, 164, 166);
        Assert.InRange(outside.Blue, 219, 221);
        var edge = bitmap.GetPixel(120, 50);                                // 同一格、洞外
        Assert.InRange(edge.Red, 139, 141);
    }

    [Fact]
    public void 整格被遮掉的直通群組不會留下任何痕跡()
    {
        using var doc = new Document(Size, Size);
        doc.Root.Add(Solid(doc.Bounds, new SKColor(40, 90, 200)));
        var group = new GroupLayer { IsPassThrough = true, Mask = new LayerMask(SKRectI.Empty, [], 0) };
        group.Add(Solid(doc.Bounds, SKColors.White));
        doc.Root.Add(group);

        using var image = Compositor.RenderComposite(doc);
        using var bitmap = SKBitmap.FromImage(image);
        Assert.Equal(new SKColor(40, 90, 200), bitmap.GetPixel(10, 10));
        Assert.Equal(new SKColor(40, 90, 200), bitmap.GetPixel(390, 390));
    }

    private static void AssertSameComposite(Document a, Document b, int tolerance)
    {
        using var ia = Compositor.RenderComposite(a);
        using var ib = Compositor.RenderComposite(b);
        using var ba = SKBitmap.FromImage(ia);
        using var bb = SKBitmap.FromImage(ib);
        var pa = ba.Bytes;
        var pb = bb.Bytes;
        var worst = 0;
        var worstAt = -1;
        for (var i = 0; i < pa.Length; i++)
        {
            var d = Math.Abs(pa[i] - pb[i]);
            if (d > worst) { worst = d; worstAt = i; }
        }
        Assert.True(worst <= tolerance,
            $"遮色片路徑與 alpha 預乘路徑不一致：位元組 {worstAt}（像素 ({worstAt / 4 % Size}, {worstAt / 4 / Size})）差 {worst}");
    }

    private static RasterLayer Solid(SKRectI bounds, SKColor color)
    {
        var layer = new RasterLayer();
        layer.Surface.Fill(bounds, color);
        return layer;
    }

    /// <summary>同色、但每個像素的 alpha 先乘上遮色片覆蓋值（premul 一併縮）。</summary>
    private static RasterLayer AlphaScaled(SKRectI bounds, SKColor color, LayerMask mask)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
        {
            var coverage = mask.At(x, y);
            // SetPixel 吃直通色，寫進 premul 點陣圖時 Skia 自己預乘
            bitmap.SetPixel(x, y, color.WithAlpha((byte)Math.Round(color.Alpha * coverage / 255.0)));
        }
        var layer = new RasterLayer();
        using var pixmap = bitmap.PeekPixels();
        layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
        return layer;
    }

    private static LayerMask GradientMask(SKRectI bounds, byte defaultValue)
    {
        var alpha = new byte[bounds.Width * bounds.Height];
        for (var y = 0; y < bounds.Height; y++)
        for (var x = 0; x < bounds.Width; x++)
            alpha[y * bounds.Width + x] = (byte)((x * 255 / (bounds.Width - 1) + y * 255 / (bounds.Height - 1)) / 2);
        return new LayerMask(bounds, alpha, defaultValue);
    }

    private static byte[] Filled(int count, byte value)
    {
        var data = new byte[count];
        Array.Fill(data, value);
        return data;
    }
}
