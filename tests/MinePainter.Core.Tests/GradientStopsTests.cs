using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class GradientStopsTests
{
    [Fact]
    public void ColorAt_InterpolatesBetweenNeighbours_AndClampsEnds()
    {
        var g = new GradientStops([
            new GradientStop(0.2f, SKColors.Red),
            new GradientStop(0.5f, SKColors.Lime),
            new GradientStop(0.8f, SKColors.Blue),
        ]);
        Assert.Equal(SKColors.Red, g.ColorAt(0f));       // 首節點之前 = 首色
        Assert.Equal(SKColors.Blue, g.ColorAt(1f));      // 末節點之後 = 末色
        var mid = g.ColorAt(0.35f);                      // 紅→綠一半
        Assert.InRange(mid.Red, 120, 136);
        Assert.InRange(mid.Green, 120, 136);
        Assert.Equal(0, mid.Blue);
        Assert.Equal(SKColors.Lime, g.ColorAt(0.5f));
    }

    [Fact]
    public void Serialize_RoundTrips_WithAlpha()
    {
        var g = new GradientStops([
            new GradientStop(0f, new SKColor(1, 2, 3, 128)),
            new GradientStop(0.333f, SKColors.White),
            new GradientStop(1f, SKColors.Black),
        ]);
        Assert.True(GradientStops.TryParse(g.Serialize(), out var back));
        Assert.Equal(g, back);
    }

    [Fact]
    public void Insert_KeepsVisualUnchanged_AndSortsByPosition()
    {
        var g = GradientStops.Two(SKColors.Black, SKColors.White).Insert(0.25f);
        Assert.Equal(3, g.Count);
        Assert.Equal(0.25f, g[1].Position);
        Assert.Equal(g.ColorAt(0.25f), GradientStops.Two(SKColors.Black, SKColors.White).ColorAt(0.25f));
        Assert.Equal(2, g.RemoveAt(1).Count);
        Assert.Equal(2, GradientStops.Two(SKColors.Black, SKColors.White).RemoveAt(0).Count); // 少於兩個不刪
    }

    [Fact]
    public void ObjectGradient_LoadsLegacyTwoColorKeys()
    {
        var dict = new Dictionary<string, string>
        {
            ["start"] = "FFFF0000", ["end"] = "FF0000FF", ["angle"] = "45", ["radial"] = "0",
        };
        var fx = Assert.IsType<ObjectGradientEffect>(EffectSerializer.Load("objectGradient", dict));
        Assert.Equal(SKColors.Red, fx.Start);
        Assert.Equal(SKColors.Blue, fx.End);
        Assert.Equal(45f, fx.Angle);
    }

    [Fact]
    public void ObjectGradient_MultiStop_RoundTripsThroughSerializer()
    {
        var fx = new ObjectGradientEffect
        {
            Stops = new GradientStops([
                new GradientStop(0f, SKColors.Red),
                new GradientStop(0.5f, SKColors.Yellow),
                new GradientStop(1f, SKColors.Blue),
            ]),
        };
        var back = Assert.IsType<ObjectGradientEffect>(EffectSerializer.Load(EffectSerializer.TypeIdOf(fx), EffectSerializer.Save(fx)));
        Assert.Equal(fx.Stops, back.Stops);
        Assert.Equal(SKColors.Yellow, back.Stops.ColorAt(0.5f));
    }
}
