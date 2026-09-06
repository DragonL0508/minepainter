using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 文字物件的邊把手 + Shift：只看被拖的那一軸等比縮放。之前一律取兩軸較大者，上下邊拖時「寬度」根本沒動，
/// 縮到目前比例就再也縮不下去（使用者 2026-09-07 回報「按住 Shift 往上推，縮到一定程度就不能再縮小」）。
/// </summary>
public class ElementShiftResizeTests
{
    private static (EditorSession Session, RasterLayer Layer, TextElement Text) NewTextSession()
    {
        var doc = ImageCodec.CreateBlankDocument(600, 400, SKColors.White);
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "Hello", FontSize = 60, Position = new SKPoint(100, 100) };
        layer.AddElement(text);
        ElementDragHelper.SetSelected(session, layer, text);
        return (session, layer, text);
    }

    private static SKPoint[] Handles(RasterLayer layer, TextElement text) =>
        MoveTool.HandlePoints(HandleDragController.ElementFrame(layer, text));

    [Fact]
    public void 上邊把手加Shift_往下推_字級一路縮小()
    {
        var (session, layer, text) = NewTextSession();
        using (session)
        {
            var frame = HandleDragController.ElementFrame(layer, text);
            var top = Handles(layer, text)[4];
            var helper = new ElementDragHelper();
            Assert.True(helper.TryBegin(session, top, 6f, allowInsideMove: false));
            // 往下推到剩四分之一高
            helper.Continue(session, new SKPoint(top.X, top.Y + frame.Height * 0.75f), ToolModifiers.Shift);
            helper.End(session);

            var after = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.True(after.FontSize < 60 * 0.4f, $"字級只縮到 {after.FontSize}，被卡住了");
            Assert.Equal(1f, after.ScaleX, 2);   // Shift = 回到原始比例
        }
    }

    [Fact]
    public void 上邊把手不加Shift_往下推_一樣能壓扁()
    {
        var (session, layer, text) = NewTextSession();
        using (session)
        {
            var frame = HandleDragController.ElementFrame(layer, text);
            var top = Handles(layer, text)[4];
            var helper = new ElementDragHelper();
            Assert.True(helper.TryBegin(session, top, 6f, allowInsideMove: false));
            helper.Continue(session, new SKPoint(top.X, top.Y + frame.Height * 0.75f), ToolModifiers.None);
            helper.End(session);

            var after = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.True(after.FontSize < 60 * 0.4f, $"字級只縮到 {after.FontSize}");
        }
    }

    [Fact]
    public void 右邊把手加Shift_往左推_以寬度為準等比縮小()
    {
        var (session, layer, text) = NewTextSession();
        using (session)
        {
            var frame = HandleDragController.ElementFrame(layer, text);
            var right = Handles(layer, text)[5];
            var helper = new ElementDragHelper();
            Assert.True(helper.TryBegin(session, right, 6f, allowInsideMove: false));
            helper.Continue(session, new SKPoint(right.X - frame.Width * 0.5f, right.Y), ToolModifiers.Shift);
            helper.End(session);

            var after = Assert.IsType<TextElement>(layer.Elements[0]);
            Assert.InRange(after.FontSize, 60 * 0.35f, 60 * 0.65f);
            Assert.Equal(1f, after.ScaleX, 2);
        }
    }
}
