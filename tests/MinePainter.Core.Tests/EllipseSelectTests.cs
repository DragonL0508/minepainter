using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>橢圓（圓形）選取工具：拖出外接矩形，選到的是橢圓不是矩形。</summary>
public class EllipseSelectTests
{
    private static EditorSession NewSession() =>
        new(ImageCodec.CreateBlankDocument(256, 256, SKColors.White));

    private static void Drag(EditorSession session, SKPoint from, SKPoint to,
        ToolModifiers modifiers = ToolModifiers.None)
    {
        var tool = session.EllipseSelect;
        tool.OnPointerDown(new ToolPointerEvent(from, 1f, modifiers), session);
        tool.OnPointerMove(new ToolPointerEvent(to, 1f, modifiers), session);
        tool.OnPointerUp(new ToolPointerEvent(to, 1f, modifiers), session);
    }

    [Fact]
    public void Drag_SelectsEllipseInsideTheDraggedBox()
    {
        using var session = NewSession();
        Drag(session, new SKPoint(50, 50), new SKPoint(150, 150));

        var selection = session.Selection;
        Assert.NotNull(selection);
        Assert.Equal(new SKRectI(50, 50, 150, 150), selection!.Bounds); // 外接框＝拖出來的矩形
        Assert.Equal(255, selection.CoverageAt(100, 100));              // 中心在裡面
        Assert.Equal(0, selection.CoverageAt(52, 52));                  // 角落在橢圓外（矩形選取會選到）
        Assert.Equal(255, selection.CoverageAt(100, 52));               // 上緣中點在裡面
    }

    [Fact]
    public void Click_WithoutDrag_Deselects()
    {
        using var session = NewSession();
        Drag(session, new SKPoint(50, 50), new SKPoint(150, 150));
        Assert.NotNull(session.Selection);

        Drag(session, new SKPoint(20, 20), new SKPoint(20, 20)); // 點一下沒拖
        Assert.Null(session.Selection);
    }

    [Fact]
    public void Shift_AddsToExistingSelection()
    {
        using var session = NewSession();
        Drag(session, new SKPoint(20, 20), new SKPoint(80, 80));
        Drag(session, new SKPoint(150, 150), new SKPoint(210, 210), ToolModifiers.Shift);

        var selection = session.Selection;
        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(50, 50));   // 第一個橢圓
        Assert.Equal(255, selection.CoverageAt(180, 180));  // 第二個橢圓
        Assert.Equal(0, selection.CoverageAt(120, 120));    // 兩者之間沒被選到
    }

    [Fact]
    public void DragPreview_IsAnEllipseOutline()
    {
        using var session = NewSession();
        var tool = session.EllipseSelect;
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(0, 0), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(200, 100), 1f), session);

        var preview = session.Preview;
        Assert.NotNull(preview);
        Assert.True(preview!.Closed);
        Assert.True(preview.Points.Count > 8); // 折線取樣的橢圓，不是四個角
        foreach (var p in preview.Points)
        {
            // 每個點都落在橢圓上：((x-cx)/rx)^2 + ((y-cy)/ry)^2 = 1
            var nx = (p.X - 100f) / 100f;
            var ny = (p.Y - 50f) / 50f;
            Assert.Equal(1f, nx * nx + ny * ny, 3);
        }

        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(200, 100), 1f), session);
        Assert.Null(session.Preview);
    }

    [Fact]
    public void RefusesOnTextLayer()
    {
        using var session = NewSession();
        var doc = session.Document;
        var text = VectorCommands.CreateTextLayerSilently(doc);
        var element = new TextElement { Text = "Hi", Position = new SKPoint(40, 40), FontSize = 40 };
        lock (doc.SyncRoot) text.AddElement(element);
        VectorCommands.CommitNewTextLayer(doc, session.History, text, element, "新增文字");

        Drag(session, new SKPoint(50, 50), new SKPoint(150, 150));
        Assert.Null(session.Selection); // 文字圖層沒有可選的像素（與其他選取工具一致）
    }
}
