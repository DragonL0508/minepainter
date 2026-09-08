using MinePainter.Core.AI;
using Xunit;

namespace MinePainter.Core.Tests;

public class MaskMorphologyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 37)]
    [InlineData(43, 1)]
    [InlineData(47, 31)]
    public void Shift_MatchesClampedSquareForSoftMasks(int width, int height)
    {
        var input = new byte[width * height];
        new Random(471).NextBytes(input);
        var original = (byte[])input.Clone();
        foreach (var radius in new[] { 1, 2, 7, 60 })
        foreach (var direction in new[] { -1, 1 })
        {
            var actual = BackgroundRemover.Shift(input, width, height, radius * direction);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var expected = direction > 0 ? 0 : 255;
                // 重複邊界值不改變 min/max，直接窮舉相交的方框作獨立參考。
                for (var sy = Math.Max(0, y - radius); sy <= Math.Min(height - 1, y + radius); sy++)
                for (var sx = Math.Max(0, x - radius); sx <= Math.Min(width - 1, x + radius); sx++)
                    expected = direction > 0 ? Math.Max(expected, input[sy * width + sx])
                        : Math.Min(expected, input[sy * width + sx]);
                Assert.Equal((byte)expected, actual[y * width + x]);
            }
        }
        Assert.Equal(original, input);
        Assert.Same(input, BackgroundRemover.Shift(input, width, height, 0));
    }
}
