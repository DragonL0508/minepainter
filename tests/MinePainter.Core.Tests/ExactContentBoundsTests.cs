using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// TileSurface.ExactContentBounds：逐像素的精確邊界。
/// （ContentBounds 是 tile 對齊的保守值，顯示給使用者會出現比畫布還大的數字。）
/// </summary>
public class ExactContentBoundsTests
{
    [Fact]
    public void EmptySurface_IsEmpty()
    {
        using var surface = new TileSurface();
        Assert.True(surface.ExactContentBounds().IsEmpty);
    }

    [Fact]
    public void SingleRect_MatchesExactly()
    {
        using var surface = new TileSurface();
        var rect = new SKRectI(40, 40, 360, 300);
        surface.Fill(rect, SKColors.Red);

        Assert.Equal(rect, surface.ExactContentBounds());
    }

    [Fact]
    public void IsTighterThanTileAlignedBounds()
    {
        using var surface = new TileSurface();
        surface.Fill(new SKRectI(100, 380, 340, 560), SKColors.Green);

        var tileAligned = surface.ContentBounds;
        var exact = surface.ExactContentBounds();

        Assert.Equal(new SKRectI(100, 380, 340, 560), exact);
        // tile 對齊會外擴到 256 的倍數
        Assert.True(tileAligned.Width > exact.Width && tileAligned.Height > exact.Height,
            $"tile 對齊 {tileAligned} 應大於精確 {exact}");
        Assert.Equal(0, tileAligned.Left % Tile.Size);
    }

    [Fact]
    public void TwoDisjointRects_UnionOfBoth()
    {
        using var surface = new TileSurface();
        surface.Fill(new SKRectI(10, 20, 30, 40), SKColors.Red);
        surface.Fill(new SKRectI(600, 700, 640, 720), SKColors.Blue);

        Assert.Equal(new SKRectI(10, 20, 640, 720), surface.ExactContentBounds());
    }

    [Fact]
    public void FullyTransparentWrites_AreNotContent()
    {
        using var surface = new TileSurface();
        surface.Fill(new SKRectI(0, 0, 100, 100), SKColors.Transparent);

        Assert.True(surface.ExactContentBounds().IsEmpty);
    }

    [Fact]
    public void SinglePixel_IsOnePixelWide()
    {
        using var surface = new TileSurface();
        surface.Fill(new SKRectI(500, 300, 501, 301), SKColors.White);

        Assert.Equal(new SKRectI(500, 300, 501, 301), surface.ExactContentBounds());
    }
}
