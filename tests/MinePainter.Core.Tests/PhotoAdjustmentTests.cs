using MinePainter.Core.Adjustments;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>色溫／色調與曝光度調整（使用者 2026-09-06 要求補的）。</summary>
public class PhotoAdjustmentTests
{
    private static SKColor Apply(IAdjustment adjustment, SKColor input)
    {
        using var filter = adjustment.CreateColorFilter();
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint { Color = input, ColorFilter = filter, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(0, 0, 1, 1, paint);
        return bmp.GetPixel(0, 0);
    }

    [Fact]
    public void Temperature_WarmsAndCools_TintShiftsGreenMagenta()
    {
        var gray = new SKColor(128, 128, 128);

        var warm = Apply(new TemperatureTintAdjustment(Temperature: 1f), gray);
        Assert.True(warm.Red > 128 && warm.Blue < 128, $"色溫往暖應紅增藍減，實際 {warm}");

        var cool = Apply(new TemperatureTintAdjustment(Temperature: -1f), gray);
        Assert.True(cool.Red < 128 && cool.Blue > 128, $"色溫往冷應藍增紅減，實際 {cool}");

        var magenta = Apply(new TemperatureTintAdjustment(Tint: 1f), gray);
        Assert.True(magenta.Green < 128 && magenta.Red > 128, $"色調往洋紅應綠減，實際 {magenta}");

        Assert.Equal(gray, Apply(new TemperatureTintAdjustment(), gray));
        Assert.Equal(SKColors.Black, Apply(new TemperatureTintAdjustment(Temperature: 1f), SKColors.Black)); // 增益不染黑
    }

    [Fact]
    public void Exposure_OneEvDoublesMidtones_OffsetLiftsBlack_GammaBendsMid()
    {
        var mid = new SKColor(64, 64, 64);
        var plusOne = Apply(new ExposureAdjustment(Exposure: 1f), mid);
        Assert.InRange(plusOne.Red, 126, 130);

        var lifted = Apply(new ExposureAdjustment(Offset: 0.2f), SKColors.Black);
        Assert.InRange(lifted.Red, 49, 53);

        var gamma = Apply(new ExposureAdjustment(Gamma: 2f), new SKColor(64, 64, 64));
        Assert.True(gamma.Red > 64, "gamma > 1 應把中間調拉亮");

        Assert.Equal(mid, Apply(new ExposureAdjustment(), mid));
    }

    [Fact]
    public void Registry_RoundTripsBothAdjustments()
    {
        var exposure = new ExposureAdjustment(1.5f, -0.1f, 0.8f);
        var loaded = (ExposureAdjustment)AdjustmentRegistry.Load("exposure", exposure.SaveParams());
        Assert.Equal(exposure.SaveParams(), loaded.SaveParams());   // record 的 Parameters 清單每個實例各一份，比參數就好

        var wb = new TemperatureTintAdjustment(0.3f, -0.6f);
        var loadedWb = (TemperatureTintAdjustment)AdjustmentRegistry.Load("temperatureTint", wb.SaveParams());
        Assert.Equal(wb.SaveParams(), loadedWb.SaveParams());

        Assert.All(new[] { "exposure", "temperatureTint" }, id => Assert.NotNull(AdjustmentRegistry.Find(id)));
    }
}
