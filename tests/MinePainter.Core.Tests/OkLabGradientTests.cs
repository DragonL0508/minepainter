using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 漸層在 OKLab 內插：中段不能像 sRGB 那樣發灰變暗；端點與 alpha 要跟以前一模一樣。
/// </summary>
public class OkLabGradientTests
{
    [Fact]
    public void 轉換來回誤差不超過一階()
    {
        SKColor[] samples = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White, SKColors.Black,
            new SKColor(0x3A, 0x7B, 0xD5), new SKColor(200, 30, 120), new SKColor(17, 17, 17)];
        foreach (var c in samples)
        {
            var back = OkLab.ToColor(OkLab.FromColor(c));
            Assert.True(Math.Abs(back.Red - c.Red) <= 1 && Math.Abs(back.Green - c.Green) <= 1 && Math.Abs(back.Blue - c.Blue) <= 1,
                $"{c} 來回變成 {back}：轉換矩陣抄錯了");
        }
    }

    [Fact]
    public void 藍到黃的中點比sRGB內插亮_不是死灰()
    {
        var mid = OkLab.Lerp(SKColors.Blue, SKColors.Yellow, 0.5f);
        var srgbMid = new SKColor(128, 128, 128);
        Assert.True(OkLab.FromColor(mid).L > OkLab.FromColor(srgbMid).L + 0.05f,
            $"中點 {mid} 沒有比 sRGB 的灰更亮：內插沒走 OKLab");
    }

    [Fact]
    public void 端點精確_alpha線性()
    {
        var a = new SKColor(200, 30, 120, 255);
        var b = new SKColor(10, 240, 60, 51);
        Assert.Equal(a, OkLab.Lerp(a, b, 0f));
        Assert.Equal(b, OkLab.Lerp(a, b, 1f));
        Assert.Equal(153, OkLab.Lerp(a, b, 0.5f).Alpha); // (255+51)/2

        // 一端全透明：色彩取另一端，不會混進透明像素沒意義的 RGB 拖出暗邊
        var faded = OkLab.Lerp(SKColors.Red, SKColors.Transparent, 0.5f);
        Assert.Equal(255, faded.Red);
        Assert.Equal(128, faded.Alpha);
    }

    [Fact]
    public void GradientStops沿用同一套內插()
    {
        var stops = GradientStops.Two(SKColors.Blue, SKColors.Yellow);
        Assert.Equal(OkLab.Lerp(SKColors.Blue, SKColors.Yellow, 0.5f), stops.ColorAt(0.5f));
        Assert.Equal(SKColors.Blue, stops.ColorAt(0f));
        Assert.Equal(SKColors.Yellow, stops.ColorAt(1f));
    }
}
