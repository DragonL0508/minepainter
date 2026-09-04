using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 按住 Shift 拖角＝維持原始比例。要維持比例的是「內容框」，但把手畫在「含效果外擴的顯示框」上，
/// 而外擴是固定寬度、不跟著縮 —— 兩個框的長寬比不一樣。在顯示框上套內容的比例、算完再扣掉外擴，
/// 出來的就不是原始比例了（文字加了外光暈之後 Shift 縮放會歪掉）。
/// </summary>
public class ShiftAspectScaleTests
{
    private static (Documents.Document Doc, RasterLayer Layer) NewTextDoc(bool withGlow)
    {
        var doc = ImageCodec.CreateBlankDocument(1200, 800, SKColors.Transparent);
        var layer = new RasterLayer { Name = "文字" };
        layer.AddElement(new TextElement
        {
            Text = "特效文字", FontSize = 64f, Color = SKColors.White, Position = new SKPoint(200, 300),
        });
        doc.Root.Add(layer);
        doc.ActiveLayer = layer;
        if (withGlow)
        {
            layer.SetEffects([LayerEffect.Create(new ObjectGlowEffect
            {
                Size = 40, Spread = 10, Color = SKColors.Yellow, Opacity = 80,
            })]);
            LayerEffectRenderer.RenderLayerNow(doc, layer);
        }
        return (doc, layer);
    }

    /// <summary>Shift 拖右下角一段「比例不對」的位移，回傳內容框與它原本的長寬比。</summary>
    private static (SKRect Rect, float Aspect) ShiftDrag(bool withGlow)
    {
        var (doc, _) = NewTextDoc(withGlow);
        using (doc)
        using (var session = new EditorSession(doc))
        {
            session.ActiveTool = session.Move;
            var transform = session.BeginTransform()!;
            var aspect = transform.ResetSize.Width / transform.ResetSize.Height;

            // 把手畫在「含效果外擴」的顯示框上（GetFrame 給的就是這個框）
            var shown = HandleDragController.GetFrame(session)!.Value;
            var bottomRight = MoveTool.HandlePoints(shown)[2];

            var handle = new HandleDragController();
            Assert.True(handle.TryBegin(session, bottomRight, tolerance: 8f));
            handle.Continue(session, new SKPoint(bottomRight.X + 160, bottomRight.Y + 20),
                ToolModifiers.Shift);
            var rect = transform.TargetRect;
            handle.End(session);
            return (rect, aspect);
        }
    }

    [Fact]
    public void Shift拖角維持原始比例_外光暈不該影響結果()
    {
        var plain = ShiftDrag(withGlow: false);
        var glow = ShiftDrag(withGlow: true);

        Assert.True(plain.Rect.Height > 0 && glow.Rect.Height > 0);
        // 比例要回到原始值（容許 SnapToPixels 的一格誤差）
        Assert.InRange(plain.Rect.Width / plain.Rect.Height, plain.Aspect * 0.98f, plain.Aspect * 1.02f);
        Assert.InRange(glow.Rect.Width / glow.Rect.Height, glow.Aspect * 0.98f, glow.Aspect * 1.02f);
        // 而且加不加外光暈，縮出來的內容框要一樣（外擴不該參與比例計算）
        Assert.Equal(plain.Rect.Width, glow.Rect.Width, 1);
        Assert.Equal(plain.Rect.Height, glow.Rect.Height, 1);
    }
}
