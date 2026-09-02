using MinePainter.Core.Tools;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 畫筆型工具的游標是照 <see cref="IBrushCursorTool.CursorRadius"/> 畫出來的圈，
/// 所以那個值必須就是實際下筆的半徑——對不上的話圈會騙人，比沒有圈更糟。
/// </summary>
public class BrushCursorTests
{
    [Fact]
    public void BrushEraserAndBackgroundEraserAllDrawABrushCursor()
    {
        Assert.IsAssignableFrom<IBrushCursorTool>(new BrushTool());
        Assert.IsAssignableFrom<IBrushCursorTool>(new EraserTool());
        Assert.IsAssignableFrom<IBrushCursorTool>(new BackgroundEraserTool());
    }

    [Fact]
    public void ToolsThatPaintNothingUnderThePointerDoNot()
    {
        Assert.IsNotAssignableFrom<IBrushCursorTool>(new MoveTool());
        Assert.IsNotAssignableFrom<IBrushCursorTool>(new FillTool());
        Assert.IsNotAssignableFrom<IBrushCursorTool>(new EyedropperTool());
    }

    [Fact]
    public void TheCursorRadiusFollowsTheBrushSize()
    {
        var brush = new BrushTool();
        brush.Settings.Radius = 37f;
        Assert.Equal(37f, ((IBrushCursorTool)brush).CursorRadius);

        var eraser = new EraserTool();
        eraser.Settings.Radius = 12.5f;
        Assert.Equal(12.5f, ((IBrushCursorTool)eraser).CursorRadius);

        var bg = new BackgroundEraserTool();
        bg.Settings.Radius = 64f;
        Assert.Equal(64f, ((IBrushCursorTool)bg).CursorRadius);
    }
}
