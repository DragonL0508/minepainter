using MinePainter.Core.Effects;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

/// <summary>內陰影、斜角和浮雕、外框位置 —— 為了對上 Photoshop 圖層樣式而加的效果（使用者 2026-09-06）。</summary>
public class PhotoshopStyleEffectTests
{
    /// <summary>w×h 透明底，中間一塊不透明的純色方塊。</summary>
    private static (uint[] Pixels, int W, int H, SKRectI Box) Square(int w = 64, int h = 64, int inset = 16)
    {
        var px = new uint[w * h];
        var box = new SKRectI(inset, inset, w - inset, h - inset);
        for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
                px[y * w + x] = Premul(128, 128, 128, 255);
        return (px, w, h, box);
    }

    private static EffectContext Run(IEffect fx, uint[] px, int w, int h)
    {
        var ctx = EffectContext.FromPixels(px, w, h, fx.SourceMargin < 0 ? 0 : fx.SourceMargin);
        fx.Render(ctx);
        return ctx;
    }

    [Fact]
    public void InnerShadow_DarkensTheEdgeFacingTheLight_AndStaysInside()
    {
        var (px, w, h, box) = Square();
        // 光從左上（120°）來 → 陰影貼在方塊的上緣與左緣內側
        var ctx = Run(new InnerShadowEffect { Angle = 120, Distance = 6, Size = 0, Opacity = 100, Color = SKColors.Black, RelativeToObject = false }, px, w, h);

        var topInside = ctx.Dst[(box.Top + 2) * w + w / 2];
        var center = ctx.Dst[(h / 2) * w + w / 2];
        var bottomInside = ctx.Dst[(box.Bottom - 3) * w + w / 2];
        Assert.True(Intensity(topInside) < Intensity(center) - 60, $"上緣內側應變暗：{Intensity(topInside)} vs 中心 {Intensity(center)}");
        Assert.Equal(Intensity(center), Intensity(bottomInside));   // 背光那一側不變
        Assert.Equal(0, A(ctx.Dst[2 * w + 2]));                       // 物件外面仍是透明
        Assert.Equal(255, A(center));
    }

    [Fact]
    public void Bevel_InnerBevel_HighlightsTowardLight_ShadowsAway()
    {
        var (px, w, h, box) = Square();
        var ctx = Run(new BevelEmbossEffect { Style = 0, Size = 6, Depth = 200, Angle = 90, Altitude = 30, RelativeToObject = false }, px, w, h);

        var top = Intensity(ctx.Dst[(box.Top + 2) * w + w / 2]);       // 朝光（上）的坡面
        var bottom = Intensity(ctx.Dst[(box.Bottom - 3) * w + w / 2]); // 背光的坡面
        var center = Intensity(ctx.Dst[(h / 2) * w + w / 2]);
        Assert.True(top > center + 30, $"朝光的坡面應變亮：{top} vs {center}");
        Assert.True(bottom < center - 30, $"背光的坡面應變暗：{bottom} vs {center}");
        Assert.Equal(0, A(ctx.Dst[2 * w + 2]));   // 內斜角不畫到外面

        // 方向反過來（凹陷）：亮暗互換
        var down = Run(new BevelEmbossEffect { Style = 0, Size = 6, Depth = 200, Angle = 90, Altitude = 30, Up = false, RelativeToObject = false }, px, w, h);
        Assert.True(Intensity(down.Dst[(box.Top + 2) * w + w / 2]) < center - 30);
    }

    [Fact]
    public void Bevel_OuterBevel_PaintsOutsideTheShape()
    {
        var (px, w, h, box) = Square();
        var fx = new BevelEmbossEffect { Style = 1, Size = 6, Depth = 200, Angle = 90, Altitude = 30, RelativeToObject = false };
        Assert.True(fx.OutputMargin >= 6, "外斜角要回報輸出會長到內容外");
        var ctx = Run(fx, px, w, h);
        Assert.True(A(ctx.Dst[(box.Top - 3) * w + w / 2]) > 0, "上緣外側應該有亮部");
        Assert.True(A(ctx.Dst[(box.Bottom + 2) * w + w / 2]) > 0, "下緣外側應該有陰影");
        Assert.Equal(Premul(128, 128, 128, 255), ctx.Dst[(h / 2) * w + w / 2]);   // 內部平台不動
    }

    [Fact]
    public void Outline_Inside_DoesNotGrowTheShape_Center_GrowsHalf()
    {
        var (px, w, h, box) = Square();
        var inside = new ObjectOutlineEffect { Width = 6, Position = 2, Color = SKColors.Red, RelativeToObject = false };
        Assert.Equal(0, inside.OutputMargin);
        var ctx = Run(inside, px, w, h);
        Assert.Equal(0, A(ctx.Dst[(box.Top - 1) * w + w / 2]));                          // 外面沒長
        Assert.Equal(255, R(Unpremultiplied(ctx.Dst[(box.Top + 2) * w + w / 2])));       // 內側 6px 是紅
        Assert.Equal(128, R(Unpremultiplied(ctx.Dst[(h / 2) * w + w / 2])));             // 中心不變

        var center = new ObjectOutlineEffect { Width = 6, Position = 1, Color = SKColors.Red, RelativeToObject = false };
        var ctx2 = Run(center, px, w, h);
        Assert.Equal(255, R(Unpremultiplied(ctx2.Dst[(box.Top - 2) * w + w / 2])));      // 外 3px
        Assert.Equal(255, R(Unpremultiplied(ctx2.Dst[(box.Top + 1) * w + w / 2])));      // 內 3px
        Assert.Equal(0, A(ctx2.Dst[(box.Top - 5) * w + w / 2]));                          // 再外面沒有
    }

    private static uint Unpremultiplied(uint p)
    {
        Unpremul(p, out var b, out var g, out var r, out var a);
        return Pack(b, g, r, a);
    }
}
