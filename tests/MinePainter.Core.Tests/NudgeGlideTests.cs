using MinePainter.Core.Tools;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>方向鍵微調的節奏：按一下走一格、按住等速滑行（由慢漸快），不跟 OS 按鍵重複走。</summary>
public class NudgeGlideTests
{
    private const double Frame = 1 / 60.0;

    /// <summary>推進 seconds 秒（每幀 1/60），回傳這段時間走的總距離。</summary>
    private static (int X, int Y) Run(NudgeGlide glide, double seconds)
    {
        int x = 0, y = 0;
        for (var t = 0.0; t < seconds; t += Frame)
        {
            var (dx, dy) = glide.Step(Frame);
            x += dx;
            y += dy;
        }
        return (x, y);
    }

    [Fact]
    public void SingleTap_MovesExactlyOnePixel()
    {
        var glide = new NudgeGlide();
        Assert.True(glide.Press(1, 0, 1));
        var (x, y) = Run(glide, 0.15); // 還沒到滑行門檻
        glide.Release(1, 0);

        Assert.Equal(1, x);
        Assert.Equal(0, y);
        Assert.True(glide.IsIdle);
    }

    [Fact]
    public void ShiftTap_MovesTenPixels_SpreadOverSeveralFrames()
    {
        var glide = new NudgeGlide { Shift = true };
        glide.Press(0, 1, 10);

        var frames = 0;
        var total = 0;
        while (total < 10 && frames < 60)
        {
            total += glide.Step(Frame).Dy;
            frames++;
        }

        Assert.Equal(10, total);
        Assert.True(frames > 1, "10px 應該是滑過去的，不是一幀跳完");
    }

    [Fact]
    public void KeyRepeat_IsIgnored_GlideDrivesTheMotion()
    {
        var glide = new NudgeGlide();
        Assert.True(glide.Press(1, 0, 1));
        Assert.False(glide.Press(1, 0, 1)); // OS 的按鍵重複：不再加一格
        Assert.False(glide.Press(1, 0, 1));
    }

    [Fact]
    public void Holding_GlidesContinuously_AndAccelerates()
    {
        var glide = new NudgeGlide();
        glide.Press(1, 0, 1);

        var first = Run(glide, 0.5).X;   // 含 0.16s 門檻與起步
        var second = Run(glide, 0.5).X;  // 已經在加速

        Assert.True(first > 10, $"按住半秒應該滑出明顯距離，實得 {first}px");
        Assert.True(second > first * 1.5, $"應該愈滑愈快：前半秒 {first}px、後半秒 {second}px");
        Assert.True(second < 400, "全速也不該一秒飛過整張圖");
    }

    [Fact]
    public void Release_StopsImmediately()
    {
        var glide = new NudgeGlide();
        glide.Press(1, 0, 1);
        Run(glide, 0.6);

        glide.Release(1, 0);
        var after = Run(glide, 0.5).X;

        Assert.Equal(0, after);
        Assert.True(glide.IsIdle);
    }

    [Fact]
    public void ShiftPressedMidGlide_SpeedsUp()
    {
        var slow = new NudgeGlide();
        slow.Press(1, 0, 1);
        Run(slow, 0.5);
        var slowHalf = Run(slow, 0.3).X;

        var fast = new NudgeGlide();
        fast.Press(1, 0, 1);
        Run(fast, 0.5);
        fast.Shift = true; // 方向鍵按住之後才按 Shift
        var fastHalf = Run(fast, 0.3).X;

        Assert.True(fastHalf > slowHalf * 2, $"Shift 應該明顯加速：{slowHalf}px → {fastHalf}px");
    }

    [Fact]
    public void DiagonalHold_GlidesBothAxes()
    {
        var glide = new NudgeGlide();
        glide.Press(1, 0, 1);
        glide.Press(0, -1, 1);

        var (x, y) = Run(glide, 0.6);

        Assert.True(x > 10);
        Assert.True(y < -10);
    }
}
