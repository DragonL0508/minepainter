using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 像素圖放大（Scale2x／3x）：樓梯要被削成斜線、不能產生輸入沒有的顏色、整數倍時尺寸一格不差。
/// </summary>
public class PixelArtScaleTests
{
    private const uint Black = 0xFF000000;
    private const uint White = 0xFFFFFFFF;

    /// <summary>n×n，左下三角黑（x ≤ y）、其餘白：一條 45° 的樓梯邊。</summary>
    private static unsafe SKBitmap Triangle(int n)
    {
        var bmp = new SKBitmap(new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Premul));
        var p = (uint*)bmp.GetPixels();
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                p[y * n + x] = x <= y ? Black : White;
        return bmp;
    }

    private static unsafe (int black, int white, int other) Count(SKBitmap bmp)
    {
        var p = (uint*)bmp.GetPixels();
        int black = 0, white = 0, other = 0;
        for (var i = 0; i < bmp.Width * bmp.Height; i++)
        {
            if (p[i] == Black) black++;
            else if (p[i] == White) white++;
            else other++;
        }
        return (black, white, other);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void 樓梯被削成斜線_沒有新顏色(int factor)
    {
        using var src = Triangle(8);
        using var dst = factor == 2 ? PixelArtScale.Scale2x(src) : PixelArtScale.Scale3x(src);
        Assert.Equal(8 * factor, dst.Width);

        var (black, white, other) = Count(dst);
        Assert.Equal(0, other); // 出現第三種顏色＝混色了，調色盤被污染
        Assert.True(black > 0 && white > 0);
        // 與最接近像素比：對角線兩側的角落要被順著斜線補掉（黑格的右上角變白、白格的左下角變黑）
        var changed = DiffFromNearest(src, dst, factor);
        Assert.True(changed >= 8, $"只有 {changed} 個子像素與最接近像素不同：樓梯沒有被削");
    }

    private static unsafe int DiffFromNearest(SKBitmap src, SKBitmap dst, int factor)
    {
        var s = (uint*)src.GetPixels();
        var d = (uint*)dst.GetPixels();
        var changed = 0;
        for (var y = 0; y < dst.Height; y++)
            for (var x = 0; x < dst.Width; x++)
                if (d[y * dst.Width + x] != s[(y / factor) * src.Width + x / factor]) changed++;
        return changed;
    }

    [Fact]
    public void 倍率規劃_湊最小的2與3乘積()
    {
        Assert.Equal(new[] { 2 }, PixelArtScale.PlanPasses(2f));
        Assert.Equal(new[] { 3 }, PixelArtScale.PlanPasses(3f));
        Assert.Equal(new[] { 2, 2 }, PixelArtScale.PlanPasses(4f));
        Assert.Equal(new[] { 3, 2 }, PixelArtScale.PlanPasses(5f));   // 6 是最小的 ≥ 5
        Assert.Equal(new[] { 2, 2, 2 }, PixelArtScale.PlanPasses(7f)); // 8 而不是 9
        Assert.Empty(PixelArtScale.PlanPasses(1f));
        Assert.Empty(PixelArtScale.PlanPasses(0.5f));
    }

    [Fact]
    public void 調整影像大小走像素圖模式_整數倍不混色()
    {
        using var doc = ImageCodec.CreateBlankDocument(8, 8, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        for (var y = 0; y < 8; y++)
            layer.Surface.Fill(new SKRectI(0, y, y + 1, y + 1), SKColors.Black); // 左下三角
        using var session = new EditorSession(doc);

        ImageCommands.ResizeImage(session, 24, 24, resample: ResampleMode.PixelArt);
        Assert.Equal(24, doc.Width);

        using var result = ImageCommands.ReadRegion(layer.Surface, new SKRectI(0, 0, 24, 24));
        var (black, white, other) = Count(result);
        Assert.Equal(0, other); // 疊出來剛好 3 倍：直接搬，不能再被雙三次糊一次
        Assert.True(black > 0 && white > 0);
        using var src = Triangle(8);
        Assert.True(DiffFromNearest(src, result, 3) >= 8, "放大後樓梯沒被削：ResizeImage 沒走 PixelArtScale");
    }
}
