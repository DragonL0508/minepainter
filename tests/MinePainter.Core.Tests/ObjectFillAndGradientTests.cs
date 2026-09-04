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

    /// <summary>一根轉了 <paramref name="degrees"/> 度的長條（premul BGRA），當作「旋轉過的物件」。</summary>
    private static uint[] RotatedBar(int size, float degrees)
    {
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var half = size * 0.35f;
        var mid = size / 2f;
        canvas.Save();
        canvas.RotateDegrees(degrees, mid, mid);
        canvas.DrawRect(SKRect.Create(mid - half, mid - 6, half * 2, 12), new SKPaint { Color = SKColors.Blue });
        canvas.Restore();
        canvas.Flush();

        using var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), size * 4, 0, 0));
        var pixels = new uint[size * size];
        var bytes = bitmap.Bytes;
        Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);
        return pixels;
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(45f, 0f)]
    [InlineData(30f, 0f)]
    [InlineData(-60f, 0f)]
    // 漸層橫跨物件的「厚度」（使用者的用法：角度 ~90°，跨過整行字的高度）。
    // 細長的物件一轉，外接框大得多，這個方向被壓縮得最嚴重 —— 漸層幾乎整個消失。
    [InlineData(0f, 90f)]
    [InlineData(45f, 90f)]
    [InlineData(-30f, 90f)]
    public void 漸層跟著物件轉時要用滿整條漸層(float rotation, float angle)
    {
        // 「角度跟著物件轉」＝漸層沿著物件自己的方向鋪，所以不管物件轉幾度，
        // 物件身上都該看得到**整條**漸層（從頭到尾）。
        //
        // 用外接框推算頭尾的話（|dx|·寬 + |dy|·高），物件一轉，外接框就大一圈，
        // 漸層被拉到那個大範圍上，物件身上只剩中間一小段 ——
        // 使用者看到的就是「漸層不見了、只剩一個顏色」。
        const int size = 128;
        var src = RotatedBar(size, rotation);
        var ctx = new EffectContext(new SKRectI(0, 0, size, size), new SKRectI(0, 0, size, size), src,
            new SKSizeI(size, size))
        {
            ContentRotation = rotation,
        };
        new ObjectGradientEffect
        {
            Stops = GradientStops.Two(SKColors.Black, SKColors.White),
            Angle = angle,
            RelativeToObject = true,
        }.Render(ctx);

        int darkest = 255, brightest = 0;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var c = At(ctx.Dst, size, x, y);
            if (c.Alpha < 250) continue; // 只看物件實心的部分（邊緣有抗鋸齒）
            if (c.Red < darkest) darkest = c.Red;
            if (c.Red > brightest) brightest = c.Red;
        }
        var used = brightest - darkest;
        Assert.True(used > 245, $"漸層只用到 {used}/255（物件轉 {rotation}°、漸層角度 {angle}°）—— 沒有鋪滿物件");
    }
}
