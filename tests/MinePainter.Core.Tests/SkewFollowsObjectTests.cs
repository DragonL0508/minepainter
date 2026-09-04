using SkiaSharp;
using MinePainter.Core.Effects;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 傾斜要跟著物件的角度走（與漸層同一個概念）：文字轉了 45°，倒的方向也該轉 45°，
/// 不然轉一下物件，調好的傾斜就變成另一個方向。
/// </summary>
public class SkewFollowsObjectTests
{
    private const int Size = 64;

    /// <summary>中央一塊不透明方塊（傾斜的基準線是從有顏色的像素找的）。</summary>
    private static uint[] Square()
    {
        var pixels = new uint[Size * Size];
        for (var y = 16; y < 48; y++)
        for (var x = 16; x < 48; x++)
        {
            pixels[y * Size + x] = 0xFFFFFFFF;
        }
        return pixels;
    }

    private static uint[] Run(SkewEffect effect, float contentRotation)
    {
        var ctx = new EffectContext(
            new SKRectI(0, 0, Size, Size), new SKRectI(0, 0, Size, Size), Square(),
            new SKSizeI(Size, Size))
        {
            ContentRotation = contentRotation,
        };
        effect.Render(ctx);
        return ctx.Dst;
    }

    [Fact]
    public void 物件沒轉時與舊行為一模一樣()
    {
        var relative = Run(new SkewEffect { Horizontal = 30f, RelativeToObject = true }, 0f);
        var canvas = Run(new SkewEffect { Horizontal = 30f, RelativeToObject = false }, 0f);
        Assert.Equal(canvas, relative);
    }

    [Fact]
    public void 物件轉了九十度_水平傾斜就變成畫布上的垂直傾斜()
    {
        // 物件轉 90° 之後，它自己的「水平」就是畫布的垂直方向
        var relative = Run(new SkewEffect { Horizontal = 45f, RelativeToObject = true }, 90f);
        var equivalent = Run(new SkewEffect { Horizontal = 0f, Vertical = 45f, RelativeToObject = false }, 0f);

        var diff = 0;
        for (var i = 0; i < relative.Length; i++)
        {
            if (relative[i] != equivalent[i]) diff++;
        }
        Assert.True(diff * 100 < relative.Length, $"與等效的垂直傾斜差了 {diff} 個像素（{relative.Length} 中）");
    }

    [Fact]
    public void 關掉之後就不跟著物件轉()
    {
        var following = Run(new SkewEffect { Horizontal = 45f, RelativeToObject = true }, 90f);
        var fixedToCanvas = Run(new SkewEffect { Horizontal = 45f, RelativeToObject = false }, 90f);
        Assert.NotEqual(fixedToCanvas, following);
    }
}
