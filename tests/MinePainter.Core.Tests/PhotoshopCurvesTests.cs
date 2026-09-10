using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class PhotoshopCurvesTests
{
    private static readonly IReadOnlyList<(float X, float Y)> Arch = [(0, 0), (0.5f, 1), (1, 0)];

    [Fact]
    public void NaturalSpline_MatchesAnalyticNaturalBoundarySolution()
    {
        // For these three knots the natural solution is 3x - 4x³ on [0,.5],
        // reflected about .5 on the other half (endpoint second derivatives zero).
        var table = CurvesAdjustment.BuildTable(Arch, true);
        for (var i = 0; i < 256; i++)
        {
            var x = Math.Min(i, 255 - i) / 255.0;
            Assert.Equal((byte)Math.Round((3 * x - 4 * x * x * x) * 255), table[i]);
        }
        Assert.NotEqual(CurvesAdjustment.BuildTable(Arch)[64], table[64]);
    }

    [Fact]
    public void NaturalSpline_ClampsOutsideKnotsAndOvershoot()
    {
        var domain = CurvesAdjustment.BuildTable([(0.25f, 0.2f), (0.75f, 0.8f)], true);
        Assert.Equal((byte)51, domain[0]);
        Assert.Equal((byte)51, domain[63]);
        Assert.Equal((byte)204, domain[192]);
        Assert.Equal((byte)204, domain[255]);
        var high = CurvesAdjustment.BuildTable([(0, 0), (0.25f, 1), (0.75f, 1), (1, 0)], true);
        var low = CurvesAdjustment.BuildTable([(0, 1), (0.25f, 0), (0.75f, 0), (1, 1)], true);
        Assert.Equal((byte)255, high[128]);
        Assert.Equal((byte)0, low[128]);
    }

    [Fact]
    public void NaturalSpline_SortsAndUsesLastDuplicateInput()
    {
        var messy = CurvesAdjustment.BuildTable([(1, 0), (0.5f, 0.1f), (0, 0), (0.5f, 1)], true);
        Assert.Equal(CurvesAdjustment.BuildTable(Arch, true), messy);
        Assert.All(CurvesAdjustment.BuildTable([(1, 0.5f), (1, 0.5f)], true), b => Assert.Equal((byte)128, b));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), CurvesAdjustment.BuildTable([], true));
    }

    [Fact]
    public void NaturalSpline_ModePersistsAndIsEditableWhileOldFilesStayMonotone()
    {
        var original = new CurvesAdjustment { MasterCurve = Arch, Curves = [Arch] };
        Assert.False(original.UseNaturalSpline);
        var toggle = Assert.Single(original.Parameters.OfType<BoolParam>(), p => p.Key == "naturalSpline");
        var edited = Assert.IsType<CurvesAdjustment>(toggle.With(original, true));
        Assert.True(toggle.Get(edited));
        var saved = edited.SaveParams();
        var restored = CurvesAdjustment.Load(saved);
        Assert.True(restored.UseNaturalSpline);
        Assert.Equal(Arch, restored.MasterCurve);
        Assert.Equal(Arch, restored.Curves[0]);
        saved.Remove("naturalSpline");
        Assert.False(CurvesAdjustment.Load(saved).UseNaturalSpline);
        Assert.Equal(CurvesAdjustment.BuildTable(Arch), CurvesAdjustment.BuildTable(Arch, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturalSpline_FilterUsesModeForMasterAndChannels(bool rgb)
    {
        var adjustment = new CurvesAdjustment
        {
            UseNaturalSpline = true,
            Mode = rgb ? CurvesAdjustment.ModeRgb : CurvesAdjustment.ModeLuminosity,
            MasterCurve = Arch,
            Curves = rgb ? [Arch, CurvesAdjustment.Identity, Arch] : [Arch],
        };
        using var bitmap = new SKBitmap(1, 1);
        using var canvas = new SKCanvas(bitmap);
        using var filter = adjustment.CreateColorFilter();
        using var paint = new SKPaint { Color = new SKColor(64, 64, 64), ColorFilter = filter };
        canvas.DrawRect(0, 0, 1, 1, paint);
        var natural = CurvesAdjustment.BuildTable(Arch, true);
        var pixel = bitmap.GetPixel(0, 0);
        Assert.Equal(natural[natural[64]], pixel.Red);
        Assert.Equal(rgb ? natural[64] : natural[natural[64]], pixel.Green);
        Assert.Equal(natural[natural[64]], pixel.Blue);
    }
}
