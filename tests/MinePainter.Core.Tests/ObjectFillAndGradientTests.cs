using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>物件塗色，以及「漸層角度跟著物件轉」。</summary>
public class ObjectFillAndGradientTests
{
    /// <summary>中間一塊不透明、四周透明的來源。</summary>
    private static uint[] Blob(int w, int h)
    {
        var pixels = new uint[w * h];
        for (var y = h / 4; y < h * 3 / 4; y++)
        for (var x = w / 4; x < w * 3 / 4; x++)
            pixels[y * w + x] = 0xFF204080; // premul BGRA：不透明
        return pixels;
    }

    private static SKColor At(uint[] px, int w, int x, int y)
    {
        var p = px[y * w + x];
        return new SKColor((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF), (byte)(p >> 24));
    }

    [Fact]
    public void Fill_ReplacesColour_KeepsShape()
    {
        const int w = 32, h = 32;
        var ctx = EffectContext.FromPixels(Blob(w, h), w, h);
        new ObjectFillEffect { Color = new SKColor(0, 200, 0), Opacity = 100 }.Render(ctx);

        Assert.Equal(new SKColor(0, 200, 0), At(ctx.Dst, w, 16, 16)); // 物件內＝新顏色
        Assert.Equal(0, At(ctx.Dst, w, 1, 1).Alpha);                  // 外面仍然透明（形狀不變）
    }

    [Fact]
    public void Fill_OpacityBlendsTowardsTheColour()
    {
        const int w = 16, h = 16;
        var src = Blob(w, h);
        var original = At(src, w, 8, 8);
        var ctx = EffectContext.FromPixels(src, w, h);
        new ObjectFillEffect { Color = SKColors.Red, Opacity = 50 }.Render(ctx);

        var mixed = At(ctx.Dst, w, 8, 8);
        Assert.True(mixed.Red > original.Red && mixed.Red < 255, $"應該是混色，拿到 {mixed}");
        Assert.Equal(255, mixed.Alpha);
    }

    [Fact]
    public void Fill_ZeroOpacity_IsNoOp()
    {
        const int w = 16, h = 16;
        var src = Blob(w, h);
        var ctx = EffectContext.FromPixels(src, w, h);
        new ObjectFillEffect { Color = SKColors.Red, Opacity = 0 }.Render(ctx);
        Assert.Equal(At(src, w, 8, 8), At(ctx.Dst, w, 8, 8));
    }

    /// <summary>漸層在某個角度下的「左右哪邊比較亮」，用來比對方向。</summary>
    private static (SKColor Left, SKColor Right) GradientEnds(float angle, float contentRotation, bool relative)
    {
        const int w = 40, h = 40;
        var ctx = new EffectContext(new SKRectI(0, 0, w, h), new SKRectI(0, 0, w, h), Blob(w, h), new SKSizeI(w, h))
        {
            ContentRotation = contentRotation,
        };
        new ObjectGradientEffect
        {
            Stops = GradientStops.Two(SKColors.Black, SKColors.White),
            Angle = angle,
            RelativeToObject = relative,
        }.Render(ctx);
        return (At(ctx.Dst, w, 11, 20), At(ctx.Dst, w, 28, 20));
    }

    [Fact]
    public void Gradient_FollowsObjectRotation()
    {
        // 0°：水平漸層，右邊比較白
        var flat = GradientEnds(0f, 0f, relative: true);
        Assert.True(flat.Right.Red > flat.Left.Red);

        // 物件轉 180°：同一個角度參數，漸層也跟著轉半圈 → 換成左邊比較白
        var turned = GradientEnds(0f, 180f, relative: true);
        Assert.True(turned.Left.Red > turned.Right.Red,
            $"漸層沒跟著物件轉：左 {turned.Left.Red} 右 {turned.Right.Red}");
    }

    [Fact]
    public void Gradient_CanIgnoreObjectRotation()
    {
        var relative = GradientEnds(0f, 180f, relative: true);
        var absolute = GradientEnds(0f, 180f, relative: false);
        // 關掉「跟著物件轉」＝以畫布為準，與物件沒轉時同一個方向
        var flat = GradientEnds(0f, 0f, relative: true);
        Assert.True(absolute.Right.Red > absolute.Left.Red);
        Assert.Equal(flat.Right.Red, absolute.Right.Red);
        Assert.NotEqual(relative.Right.Red, absolute.Right.Red);
    }
}
