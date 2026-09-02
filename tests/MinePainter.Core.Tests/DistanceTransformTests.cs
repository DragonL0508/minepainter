using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class DistanceTransformTests
{
    [Fact]
    public void FromAlpha_MatchesBruteForceEuclidean()
    {
        const int w = 37, h = 29;
        var rnd = new Random(7);
        var src = new uint[w * h];
        var opaque = new List<(int X, int Y)>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (rnd.NextDouble() < 0.06)
            {
                src[y * w + x] = FromColor(SKColors.Black, 255);
                opaque.Add((x, y));
            }
        }
        var ctx = EffectContext.FromPixels(src, w, h, 0);
        const int pad = 3;
        var dist = DistanceTransform.FromAlpha(ctx, pad);
        var dw = w + pad * 2;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var best = double.MaxValue;
            foreach (var (ox, oy) in opaque)
                best = Math.Min(best, Math.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy)));
            Assert.Equal(best, dist[(y + pad) * dw + (x + pad)], 3);
        }
    }

    [Fact]
    public void Outline_CornerIsRound_NotOctagonal()
    {
        // 單一像素外框寬 10：距離 10 的圓上（45° 方向）要有覆蓋，chamfer 近似會在這裡缺角
        const int w = 40, h = 40;
        var src = new uint[w * h];
        src[20 * w + 20] = FromColor(SKColors.Black, 255);
        var fx = new ObjectOutlineEffect { Width = 10, Color = SKColors.Red };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);
        // (7,7) 距離 = 7√2 ≈ 9.9 → 在外框內；(20,30) 距離 10 → 邊緣仍有覆蓋
        Assert.True(A(ctx.Dst[13 * w + 13]) > 128);
        Assert.True(A(ctx.Dst[30 * w + 20]) > 0);
        // (12,12) 距離 8√2 ≈ 11.3 → 在外框外
        Assert.Equal(0, A(ctx.Dst[12 * w + 12]));
    }
}
