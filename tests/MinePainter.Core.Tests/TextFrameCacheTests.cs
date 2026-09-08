using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class TextFrameCacheTests
{
    [Fact]
    public void 拖曳反覆讀框不再配置排版物件()
    {
        var text = new TextElement { Text = "4K 效能測試\nPerformance", FontSize = 180 };
        var expected = text.FrameBounds;
        for (var i = 0; i < 100; i++) _ = text.FrameBounds;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var last = SKRect.Empty;
        for (var i = 0; i < 1000; i++) last = text.FrameBounds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, last);
        Assert.True(allocated < 1024, $"重複量測配置了 {allocated} bytes");
    }

    [Fact]
    public void 複製後修改位置字級內容不能沿用原框()
    {
        var text = new TextElement { Text = "Hello", FontSize = 60 };
        var original = text.FrameBounds;
        var moved = text with { Position = new SKPoint(120, 80) };
        var expected = original;
        expected.Offset(120, 80);
        Assert.Equal(expected, moved.FrameBounds);
        Assert.True((text with { FontSize = 120 }).FrameBounds.Width > original.Width * 1.8f);
        Assert.True((text with { Text = "Hello Hello" }).FrameBounds.Width > original.Width);
        Assert.NotEqual(original, (text with { Rotation = 45 }).FrameBounds);
        Assert.Equal(original, text.FrameBounds);
    }

    [Fact]
    public void 多執行緒讀框得到一致結果()
    {
        var text = new TextElement { Text = "並行量測", FontSize = 96, Underline = true };
        var frames = new SKRect[32];
        Parallel.For(0, frames.Length, i => frames[i] = text.FrameBounds);
        Assert.All(frames, frame => Assert.Equal(frames[0], frame));
    }
}
