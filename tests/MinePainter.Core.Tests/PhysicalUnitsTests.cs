using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>新增影像的實體單位／解析度（使用者 2026-09-06 要求「用 dpi、cm 這類專業參數開新專案」）。</summary>
public class PhysicalUnitsTests
{
    [Theory]
    [InlineData(21.0, LengthUnit.Centimeter, 300, 2480)]     // A4 寬 @300 dpi
    [InlineData(297, LengthUnit.Millimeter, 300, 3508)]     // A4 高 @300 dpi
    [InlineData(8.5, LengthUnit.Inch, 300, 2550)]           // Letter 寬
    [InlineData(1920, LengthUnit.Pixel, 96, 1920)]
    public void ToPixels_MatchesPrintConventions(double value, LengthUnit unit, double dpi, int expected)
    {
        Assert.Equal(expected, PhysicalUnits.ToPixels(value, unit, dpi));
    }

    [Fact]
    public void FromPixels_RoundTripsThroughToPixels()
    {
        foreach (var unit in new[] { LengthUnit.Centimeter, LengthUnit.Millimeter, LengthUnit.Inch })
        {
            var physical = PhysicalUnits.FromPixels(1234, unit, 150);
            Assert.Equal(1234, PhysicalUnits.ToPixels(physical, unit, 150));
        }
        Assert.Equal(300, PhysicalUnits.ToDpi(PhysicalUnits.FromDpi(300, ResolutionUnit.PixelsPerCentimeter), ResolutionUnit.PixelsPerCentimeter), 6);
    }

    [Fact]
    public void Presets_PrintSizesUse300DpiAndScreenSizesUse96()
    {
        var a4 = Assert.Single(PhysicalUnits.Presets, p => p.Label.StartsWith("A4", StringComparison.Ordinal));
        Assert.Equal((2480, 3508, 300f), (a4.Width, a4.Height, a4.Dpi));
        var fullHd = Assert.Single(PhysicalUnits.Presets, p => p.Label.Contains("Full HD", StringComparison.Ordinal));
        Assert.Equal((1920, 1080, PhysicalUnits.ScreenDpi), (fullHd.Width, fullHd.Height, fullHd.Dpi));
        Assert.All(PhysicalUnits.Presets, p => Assert.True(p.Width > 0 && p.Height > 0 && p.Dpi > 0));
    }

    [Fact]
    public void Document_DpiDefaultsTo96_RejectsGarbage_AndRoundTripsThroughMpp()
    {
        using var doc = ImageCodec.CreateBlankDocument(8, 8, SKColors.White, dpi: 300);
        Assert.Equal(300f, doc.Dpi);
        doc.Dpi = float.NaN;
        Assert.Equal(PhysicalUnits.ScreenDpi, doc.Dpi);
        doc.Dpi = 72;

        var path = Path.Combine(Path.GetTempPath(), $"dpi_{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, path);
            using var loaded = MppFormat.Load(path);
            Assert.Equal(72f, loaded.Dpi);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        using var plain = ImageCodec.CreateBlankDocument(4, 4, SKColors.White);
        Assert.Equal(PhysicalUnits.ScreenDpi, plain.Dpi);
    }
}
