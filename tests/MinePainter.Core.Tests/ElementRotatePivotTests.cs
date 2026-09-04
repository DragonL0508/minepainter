using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 右鍵拖曳旋轉文字時，畫面上轉的是覆疊快照、真正改的是原件 —— 兩者必須繞同一個軸心。
/// 覆疊圖的框是「含效果外擴」的框，它的中心跟「使用者看到的框」（著墨範圍）中心差了
/// 排版框與著墨框的落差；拿覆疊圖的中心當軸，手勢中的字就會繞錯圓心跑，放開又跳回去。
/// </summary>
public class ElementRotatePivotTests
{
    private static (EditorSession Session, RasterLayer Layer, TextElement Text) Setup()
    {
        var doc = ImageCodec.CreateBlankDocument(1200, 600, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement
        {
            Text = "MinePainter 特效文字",
            FontSize = 96f,
            Position = new SKPoint(120, 160),
            Color = SKColors.Black,
            Glow = new TextGlow { Size = 24, Spread = 5 },
        };
        layer.AddElement(text);
        var session = new EditorSession(doc);
        session.SelectedElement = (layer.Id, text.Id);
        return (session, layer, text);
    }

    [Fact]
    public void 覆疊圖要繞著使用者看到的框中心轉()
    {
        var (session, _, text) = Setup();
        using (session)
        {
            var frame = text.FrameBounds;
            var center = new SKPoint(frame.MidX, frame.MidY);

            var helper = new ElementDragHelper();
            Assert.True(helper.TryBeginRotate(session, center));
            helper.ContinueRotate(session, new SKPoint(center.X + 100, center.Y + 100));

            var overlay = session.ElementOverlay;
            Assert.NotNull(overlay);
            Assert.NotEqual(0f, overlay!.Rotation);

            // 軸心＝使用者看到的框中心，而不是覆疊圖（含光暈外擴）的中心
            Assert.Equal(center.X, overlay.Pivot.X, 3);
            Assert.Equal(center.Y, overlay.Pivot.Y, 3);

            var image = overlay.CurrentRect;
            Assert.True(Math.Abs(image.MidY - overlay.Pivot.Y) > 1f,
                "這個樣本的兩個中心本來就該不一樣，否則測不到東西");
        }
    }
}
