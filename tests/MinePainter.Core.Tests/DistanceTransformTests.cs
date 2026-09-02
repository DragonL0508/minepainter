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

    [Fact]
    public void Outline_Smooth_BridgesSmallGap_WithoutTouchingFarPixels()
    {
        // 兩塊 10×10 方形中間留 3px 縫：平滑 0 時外框寬 1 不會碰到縫中央；平滑 2 會把縫補平、外框跨過去
        const int w = 40, h = 30;
        var src = new uint[w * h];
        for (var y = 10; y < 20; y++)
        for (var x = 10; x < 30; x++)
            if (x < 20 || x >= 23) src[y * w + x] = FromColor(SKColors.Black, 255);

        var plain = new ObjectOutlineEffect { Width = 1, Color = SKColors.Red };
        var ctxPlain = EffectContext.FromPixels(src, w, h, plain.SourceMargin);
        plain.Render(ctxPlain);
        Assert.Equal(0, A(ctxPlain.Dst[15 * w + 21]));

        var smooth = plain with { Smooth = 2 };
        var ctxSmooth = EffectContext.FromPixels(src, w, h, smooth.SourceMargin);
        smooth.Render(ctxSmooth);
        Assert.Equal(255, A(ctxSmooth.Dst[15 * w + 21]));
        Assert.Equal(SKColors.Red.Red, R(ctxSmooth.Dst[15 * w + 21]));

        // 離內容遠的像素不受影響；一般邊緣的外框覆蓋也不變
        Assert.Equal(0, A(ctxSmooth.Dst[5 * w + 5]));
        Assert.Equal(A(ctxPlain.Dst[15 * w + 9]), A(ctxSmooth.Dst[15 * w + 9]));
        // 原本的不透明像素保持原色（閉運算只會補、不會削）
        Assert.Equal(src[10 * w + 10], ctxSmooth.Dst[10 * w + 10]);
    }
}
