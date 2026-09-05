using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 分離文字：選一段拆成獨立圖層（前／中／後）收進群組，像素位置一模一樣（使用者 2026-09-06 明示）。
/// 這是我們對「一段文字多種樣式」的作法。
/// </summary>
public class SplitTextTests
{
    private static (EditorSession Session, RasterLayer Layer, TextElement Text) NewTextDoc(TextElement text)
    {
        var doc = ImageCodec.CreateBlankDocument(400, 200, SKColors.Transparent);
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.AddElement(text);
        return (session, layer, text);
    }

    /// <summary>兩張圖不一樣的像素比例（alpha 或色差 > 8 才算）。</summary>
    private static double DiffRatio(SKImage a, SKImage b)
    {
        using var ba = SKBitmap.FromImage(a);
        using var bb = SKBitmap.FromImage(b);
        var pa = ba.GetPixelSpan();
        var pb = bb.GetPixelSpan();
        long diff = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            var d = Math.Max(Math.Max(Math.Abs(pa[i] - pb[i]), Math.Abs(pa[i + 1] - pb[i + 1])),
                Math.Max(Math.Abs(pa[i + 2] - pb[i + 2]), Math.Abs(pa[i + 3] - pb[i + 3])));
            if (d > 8) diff++;
        }
        return diff / (double)(pa.Length / 4);
    }

    private static long InkPixels(SKImage image)
    {
        using var bmp = SKBitmap.FromImage(image);
        var span = bmp.GetPixelSpan();
        long n = 0;
        for (var i = 3; i < span.Length; i += 4) if (span[i] > 0) n++;
        return n;
    }

    [Theory]
    [InlineData(TextAlign.Left, 0f, 1f)]
    [InlineData(TextAlign.Center, 0f, 1f)]
    [InlineData(TextAlign.Right, 17f, 1f)]
    [InlineData(TextAlign.Left, -30f, 1.4f)]
    public void SplitText_KeepsEveryGlyphWhereItWas(TextAlign align, float rotation, float scaleX)
    {
        var text = new TextElement
        {
            Text = "這是一把最強的劍\nAB",
            FontFamily = "Microsoft JhengHei",
            FontSize = 32,
            Position = new SKPoint(60, 40),
            Alignment = align,
            Rotation = rotation,
            ScaleX = scaleX,
            LetterSpacing = 3,
            Color = SKColors.Black,
        };
        var (session, layer, element) = NewTextDoc(text);
        using var _ = session;
        var doc = session.Document;

        using var before = OutputRender.Render(doc);
        Assert.True(InkPixels(before) > 500, "測試文字得真的畫出東西");

        // 選「最強」
        var result = VectorCommands.SplitText(doc, session.History, layer, element, 4, 2);
        Assert.NotNull(result);
        var group = result.Value.Group;
        Assert.Same(group, doc.Root.Children[0]);
        Assert.Equal("背景", group.Name);
        // 這是一把 / 最強 / 的劍 / AB（第二行是獨立一段）
        Assert.Equal(["這是一把", "最強", "的劍", "AB"],
            group.Children.Cast<RasterLayer>().Select(l => ((TextElement)l.Elements[0]).Text).ToArray());
        Assert.Equal("最強", ((TextElement)result.Value.Selected.Elements[0]).Text);
        Assert.Same(result.Value.Selected, doc.ActiveLayer);

        using var after = OutputRender.Render(doc);
        var diff = DiffRatio(before, after);
        Assert.True(diff < 0.0005, $"分離前後畫面應一模一樣，實際有 {diff:P3} 的像素不同");

        // undo 回到原本那一層、redo 再拆開
        session.History.Undo();
        Assert.Same(layer, doc.Root.Children[0]);
        Assert.Equal("這是一把最強的劍\nAB", ((TextElement)layer.Elements[0]).Text);
        using var undone = OutputRender.Render(doc);
        Assert.True(DiffRatio(before, undone) < 0.0005);
        session.History.Redo();
        Assert.Same(group, doc.Root.Children[0]);
    }

    [Fact]
    public void SplitText_CopiesLayerEffectsAndProperties_ToEveryPiece()
    {
        var text = new TextElement { Text = "Hello World", FontFamily = "Arial", FontSize = 40, Position = new SKPoint(20, 50) };
        var (session, layer, element) = NewTextDoc(text);
        using var _ = session;
        lock (session.Document.SyncRoot)
        {
            layer.Opacity = 0.6f;
            layer.BlendMode = BlendMode.Multiply;
            layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 3 })]);
        }

        var result = VectorCommands.SplitText(session.Document, session.History, layer, element, 6, 5);
        Assert.NotNull(result);
        Assert.All(result.Value.Group.Children, node =>
        {
            var piece = Assert.IsType<RasterLayer>(node);
            Assert.Equal(0.6f, piece.Opacity);
            Assert.Equal(BlendMode.Multiply, piece.BlendMode);
            Assert.Single(piece.Effects);
            Assert.True(piece.Surface.ExactContentBounds().IsEmpty, "文字圖層不帶像素");
        });
        Assert.Equal(2, result.Value.Group.Children.Count);   // "Hello " 與 "World"
    }

    [Fact]
    public void SplitText_RefusesEmptyRange_WholeText_AndWarpedText()
    {
        var text = new TextElement { Text = "abc", FontFamily = "Arial", FontSize = 20, Position = new SKPoint(10, 10) };
        var (session, layer, element) = NewTextDoc(text);
        using var _ = session;
        Assert.Null(VectorCommands.SplitText(session.Document, session.History, layer, element, 1, 0));
        Assert.Null(VectorCommands.SplitText(session.Document, session.History, layer, element, 0, 3));

        var warped = element with { Deform = new TextDeform(SKMatrix.CreateSkew(0.3f, 0), null) };
        Assert.Empty(warped.SplitPieces(1, 1, out var _unused));
        Assert.Empty(session.History.UndoStack);
    }

    [Fact]
    public void SplitText_KeepsOriginalLayerWhenItHasOtherElements()
    {
        var text = new TextElement { Text = "one two", FontFamily = "Arial", FontSize = 20, Position = new SKPoint(10, 10) };
        var (session, layer, element) = NewTextDoc(text);
        using var _ = session;
        var other = new TextElement { Text = "other", FontFamily = "Arial", FontSize = 20, Position = new SKPoint(10, 100) };
        lock (session.Document.SyncRoot) layer.AddElement(other);

        var result = VectorCommands.SplitText(session.Document, session.History, layer, element, 4, 3);
        Assert.NotNull(result);
        Assert.Same(layer, result.Value.Group.Children[0]);          // 原圖層留在群組底下
        Assert.Single(layer.Elements);
        Assert.Equal("other", ((TextElement)layer.Elements[0]).Text);
        Assert.Equal(3, result.Value.Group.Children.Count);

        session.History.Undo();
        Assert.Equal(2, layer.Elements.Count);
        Assert.Same(layer, session.Document.Root.Children[0]);
    }
}
