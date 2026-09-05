using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 「Alt＝反向」的共同約定（使用者 2026-09-05 明示，對標 Photoshop）：
/// 選取工具 Alt＝減選、筆刷 Alt＝擦除、橡皮擦 Alt＝把擦掉的還原回來。
/// 另外：文字圖層不給用鋼筆（路徑的三個出口在文字圖層都會被擋）。
/// </summary>
public class AltModifierTests
{
    [Theory]
    [InlineData(ToolModifiers.None, SelectionCombineMode.Replace)]
    [InlineData(ToolModifiers.Shift, SelectionCombineMode.Add)]
    [InlineData(ToolModifiers.Alt, SelectionCombineMode.Subtract)]
    [InlineData(ToolModifiers.Ctrl, SelectionCombineMode.Subtract)]          // 原本的減選鍵留著
    [InlineData(ToolModifiers.Shift | ToolModifiers.Alt, SelectionCombineMode.Intersect)]
    [InlineData(ToolModifiers.Shift | ToolModifiers.Ctrl, SelectionCombineMode.Intersect)]
    public void 選取工具的修飾鍵(ToolModifiers mods, SelectionCombineMode expected)
        => Assert.Equal(expected, SelectionCommands.ModeFrom(mods));

    [Fact]
    public void Alt_拖矩形選取是減選()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(100, 100, SKColors.White));
        var tool = new RectangleSelectTool();

        Drag(tool, session, new SKPoint(10, 10), new SKPoint(90, 90), ToolModifiers.None);
        var before = session.Selection;
        Assert.NotNull(before);

        // 從中間挖掉一塊
        Drag(tool, session, new SKPoint(40, 40), new SKPoint(60, 60), ToolModifiers.Alt);

        Assert.NotNull(session.Selection);
        Assert.True(session.Selection!.CoverageAt(20, 20) > 0, "外圈應該還在選取範圍內");
        Assert.Equal(0, session.Selection.CoverageAt(50, 50));
    }

    private static void Drag(ITool tool, EditorSession session, SKPoint from, SKPoint to, ToolModifiers mods)
    {
        tool.OnPointerDown(new ToolPointerEvent(from, 1f, mods), session);
        tool.OnPointerMove(new ToolPointerEvent(to, 1f, mods), session);
        tool.OnPointerUp(new ToolPointerEvent(to, 1f, mods), session);
    }

    [Fact]
    public void 文字圖層不給用鋼筆()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.AddElement(new TextElement
            {
                Text = "字",
                FontSize = 48,
                Color = SKColors.Black,
                Position = new SKPoint(20, 60),
            });
        }
        Assert.True(layer.IsTextLayer);

        string? message = null;
        session.Notified += m => message = m;

        new PenTool().OnPointerDown(new ToolPointerEvent(new SKPoint(50, 50), 1f), session);

        Assert.Null(session.PenPath);
        Assert.Contains("鋼筆", message ?? "");
    }

    [Fact]
    public void 一般圖層照樣可以用鋼筆()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.White));
        new PenTool().OnPointerDown(new ToolPointerEvent(new SKPoint(50, 50), 1f), session);
        Assert.NotNull(session.PenPath);
    }
}
