using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class ObjectShadowThicknessTests
{
    private static uint[] Canvas(int w, int h, Func<int, int, uint> f)
    {
        var a = new uint[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            a[y * w + x] = f(x, y);
        return a;
    }

    private static uint Premul(byte r, byte g, byte b, byte a) =>
        FromColor(new SKColor(r, g, b), a);

    [Fact]
    public void Thickness_ExtrudesShadowAlongOffsetDirection()
    {
        // 20x20 方塊在 (10..30)，位移 (2,0)、厚度 10、不模糊：
        // 陰影應從 x=12 一路連到 x=42（方塊右緣 30 + 2 + 10）
        const int w = 64, h = 64;
        var src = Canvas(w, h, (x, y) => x is >= 10 and < 30 && y is >= 10 and < 30 ? Premul(255, 0, 0, 255) : 0);
        var fx = new ObjectShadowEffect { OffsetX = 2, OffsetY = 0, Thickness = 10, Blur = 0, Opacity = 100, Color = SKColors.Black };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        Assert.Equal(255, A(ctx.Dst[20 * w + 35]));   // 擠出區：本來沒陰影
        Assert.Equal(255, A(ctx.Dst[20 * w + 41]));   // 擠出末端
        Assert.Equal(0, A(ctx.Dst[20 * w + 43]));     // 超過厚度
        Assert.Equal(0, A(ctx.Dst[20 * w + 9]));      // 左側不長
        Assert.Equal(0, A(ctx.Dst[41 * w + 20]));     // 垂直方向不長
    }

    [Fact]
    public void Thickness_Zero_MatchesPlainShadow()
    {
        const int w = 48, h = 48;
        var src = Canvas(w, h, (x, y) => x is >= 10 and < 30 && y is >= 10 and < 30 ? Premul(255, 0, 0, 255) : 0);
        var fx = new ObjectShadowEffect { OffsetX = 3, OffsetY = 0, Thickness = 0, Blur = 0, Opacity = 100 };
        var ctx = EffectContext.FromPixels(src, w, h, fx.SourceMargin);
        fx.Render(ctx);

        Assert.Equal(255, A(ctx.Dst[20 * w + 31]));
        Assert.Equal(0, A(ctx.Dst[20 * w + 33]));
    }

    [Fact]
    public void ExtrusionOffsets_DiagonalHasNoGaps()
    {
        var offsets = ObjectShadowEffect.ExtrusionOffsets(3, 4, 20);
        Assert.Contains((3, 4), offsets);
        // 每一步與前一步最多差 1px（不會跳格出現縫隙）
        for (var i = 1; i < offsets.Count; i++)
        {
            Assert.InRange(Math.Abs(offsets[i].X - offsets[i - 1].X), 0, 1);
            Assert.InRange(Math.Abs(offsets[i].Y - offsets[i - 1].Y), 0, 1);
        }
        // 末端離起點約 20px
        var last = offsets[^1];
        var dist = Math.Sqrt(Math.Pow(last.X - 3, 2) + Math.Pow(last.Y - 4, 2));
        Assert.InRange(dist, 19, 21);
    }

    [Fact]
    public void Thickness_RoundTripsThroughSerializer()
    {
        var fx = new ObjectShadowEffect { Thickness = 17 };
        var saved = EffectSerializer.Save(fx);
        var loaded = Assert.IsType<ObjectShadowEffect>(EffectSerializer.Load(EffectSerializer.TypeIdOf(fx), saved));
        Assert.Equal(17, loaded.Thickness);
    }
}
