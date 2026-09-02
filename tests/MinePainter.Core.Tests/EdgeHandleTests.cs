using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class EdgeHandleTests
{
    private static readonly SKRect Start = new(10, 20, 110, 70); // 100x50

    [Fact]
    public void HitCorner_FindsEdgeMidpoints()
    {
        Assert.Equal(4, MoveTool.HitCorner(Start, new SKPoint(60, 20), 3));   // 上中
        Assert.Equal(5, MoveTool.HitCorner(Start, new SKPoint(110, 45), 3));  // 右中
        Assert.Equal(6, MoveTool.HitCorner(Start, new SKPoint(60, 70), 3));   // 下中
        Assert.Equal(7, MoveTool.HitCorner(Start, new SKPoint(10, 45), 3));   // 左中
        Assert.Equal(0, MoveTool.HitCorner(Start, new SKPoint(10, 20), 3));   // 角仍優先
        Assert.Equal(-1, MoveTool.HitCorner(Start, new SKPoint(60, 45), 3));  // 中央不是把手
    }

    [Fact]
    public void ResizeRect_EdgeHandle_MovesOnlyThatSide()
    {
        var r = MoveTool.ResizeRect(Start, 5, new SKPoint(150, 999), keepAspect: false);
        Assert.Equal(new SKRect(10, 20, 150, 70), r);

        r = MoveTool.ResizeRect(Start, 4, new SKPoint(999, 0), keepAspect: false);
        Assert.Equal(new SKRect(10, 0, 110, 70), r);

        r = MoveTool.ResizeRect(Start, 7, new SKPoint(30, 999), keepAspect: false);
        Assert.Equal(new SKRect(30, 20, 110, 70), r);

        r = MoveTool.ResizeRect(Start, 6, new SKPoint(999, 120), keepAspect: false);
        Assert.Equal(new SKRect(10, 20, 110, 120), r);
    }

    [Fact]
    public void ResizeRect_EdgeHandle_KeepAspect_ScalesOtherAxisAroundCenter()
    {
        // 右邊拉到 210：寬 200，比例 2:1 → 高 100，垂直中心 45 不變
        var r = MoveTool.ResizeRect(Start, 5, new SKPoint(210, 0), keepAspect: true);
        Assert.Equal(200, r.Width, 3);
        Assert.Equal(100, r.Height, 3);
        Assert.Equal(45, r.MidY, 3);
        Assert.Equal(10, r.Left, 3);
    }
}
