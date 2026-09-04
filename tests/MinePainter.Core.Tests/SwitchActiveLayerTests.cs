using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 換作用中圖層＝上一層還浮著的東西先落地。變形框綁在原本那層，留著不落地的話把手框會一直
/// 優先顯示那個舊的變形框，新圖層的內容框就長不出來（使用者回報「縮放過之後再點到另一個
/// 圖層，不會自動框住那層的東西」）。
/// </summary>
public class SwitchActiveLayerTests
{
    [Fact]
    public void 縮放過再換圖層_框要跟著換到新圖層()
    {
        var doc = ImageCodec.CreateBlankDocument(600, 400, SKColors.Transparent);
        var pixels = (RasterLayer)doc.ActiveLayer!;
        pixels.Surface.Fill(new SKRectI(40, 40, 200, 160), SKColors.Red);

        var textLayer = new RasterLayer { Name = "文字" };
        textLayer.AddElement(new TextElement
        {
            Text = "文字", FontSize = 48f, Color = SKColors.White, Position = new SKPoint(320, 240),
        });
        doc.Root.Add(textLayer);

        using (doc)
        using (var session = new EditorSession(doc))
        {
            session.ActiveTool = session.Move;

            // 在像素圖層上縮放一下（變形 session 就留著了）
            var transform = session.BeginTransform()!;
            transform.TargetRect = SKRect.Create(
                transform.TargetRect.Left, transform.TargetRect.Top,
                transform.TargetRect.Width * 1.5f, transform.TargetRect.Height * 1.5f);
            transform.Apply(preview: true);
            Assert.NotNull(session.Transform);

            // 換到文字圖層
            session.SetActiveLayer(textLayer);

            Assert.Same(textLayer, doc.ActiveLayer);
            Assert.Null(session.Transform); // 舊的變形已落地，不會再霸著把手框
            session.RefreshSelectionHandles();

            // 框要落在文字圖層的內容上，不是還停在像素圖層那邊
            Assert.NotNull(session.SelectionHandles);
            var frame = session.SelectionHandles!.Value;
            var text = textLayer.Elements[0].FrameBounds;
            Assert.True(frame.IntersectsWith(text), $"框 {frame} 沒框到文字 {text}");
            Assert.False(frame.IntersectsWith(new SKRect(40, 40, 200, 160)),
                "框還停在上一層的內容上");
        }
    }
}
