using System.Diagnostics;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MinePainter.Core.Tests;

public class FloodFillPerfTests(ITestOutputHelper output)
{
    [Fact]
    public void FloodFill_WholeCanvas_CompletesQuickly()
    {
        // 使用者實測案例：整片 1600×1200 白色畫布點魔術棒曾把 UI 凍住數十秒
        using var doc = ImageCodec.CreateBlankDocument(1600, 1200, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;

        var sw = Stopwatch.StartNew();
        var mask = FloodFiller.Fill(layer, new SKPointI(800, 600), 32, doc.Bounds);
        sw.Stop();

        output.WriteLine($"1600×1200 全選 flood fill: {sw.ElapsedMilliseconds}ms");
        Assert.Equal(255, mask.CoverageAt(0, 0));
        Assert.Equal(255, mask.CoverageAt(1599, 1199));
        // Debug build 門檻放寬到 2 秒（Release 應 <100ms）；凍結等級的迴歸會直接爆掉
        Assert.True(sw.ElapsedMilliseconds < 2000, $"flood fill 太慢：{sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void FloodFill_8K_CompletesInTime()
    {
        using var doc = ImageCodec.CreateBlankDocument(8192, 4096, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;

        var sw = Stopwatch.StartNew();
        var mask = FloodFiller.Fill(layer, new SKPointI(4096, 2048), 0, doc.Bounds);
        sw.Stop();

        output.WriteLine($"8192×4096 全選 flood fill: {sw.ElapsedMilliseconds}ms");
        Assert.Equal(255, mask.CoverageAt(100, 100));
        Assert.True(sw.ElapsedMilliseconds < 8000, $"flood fill 太慢：{sw.ElapsedMilliseconds}ms");
    }
}
