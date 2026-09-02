using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

public class TextLayerTests
{
    private static ToolPointerEvent At(float x, float y) => new(new SKPoint(x, y), 1f);

    [Fact]
    public void TextTool_CreatesOwnLayer_AndCommitNamesIt()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 100, SKColors.White);
        var background = (RasterLayer)doc.ActiveLayer!;
        using var session = new EditorSession(doc);
        session.ActiveTool = session.Text;

        session.Text.OnPointerDown(At(20, 20), session);
        session.Text.OnPointerUp(At(20, 20), session);

        Assert.Equal(2, doc.Root.Children.Count);
        var textLayer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        Assert.NotSame(background, textLayer);
        Assert.True(textLayer.IsTextLayer);
        Assert.Empty(background.Elements);
        Assert.False(session.History.CanUndo); // 還沒落地

        var element = (TextElement)textLayer.Elements[0];
        VectorCommands.CommitNewTextLayer(doc, session.History, textLayer, element with { Text = "Hello\nWorld" }, "新增文字");
        Assert.Equal("Hello", textLayer.Name);
        Assert.True(session.History.CanUndo);

        Assert.True(session.Undo());
        Assert.Single(doc.Root.Children); // 整個文字圖層一起消失
        Assert.Same(background, doc.ActiveLayer);
        Assert.True(session.Redo());
        Assert.Equal(2, doc.Root.Children.Count);
    }

    [Fact]
    public void DiscardNewTextLayer_RemovesEmptyLayer()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 100, SKColors.White);
        var background = doc.ActiveLayer!;
        var layer = VectorCommands.CreateTextLayerSilently(doc);
        Assert.Equal(2, doc.Root.Children.Count);
        VectorCommands.DiscardNewTextLayer(doc, layer);
        Assert.Single(doc.Root.Children);
        Assert.Same(background, doc.ActiveLayer);
    }

    [Fact]
    public void Brush_RefusesTextLayer()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 100, SKColors.White);
        using var session = new EditorSession(doc);
        var layer = VectorCommands.CreateTextLayerSilently(doc);
        lock (doc.SyncRoot) layer.AddElement(new TextElement { Text = "x", Position = new SKPoint(10, 10) });
        string? notice = null;
        session.Notified += m => notice = m;

        session.Brush.OnPointerDown(At(50, 50), session);
        session.Brush.OnPointerUp(At(60, 50), session);
        Assert.Equal(0, layer.Surface.TileCount);
        Assert.Contains("文字圖層", notice);
    }

    [Fact]
    public void EffectStack_IncludesTextElements()
    {
        using var doc = ImageCodec.CreateBlankDocument(120, 60, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.AddElement(new TextElement { Text = "II", FontSize = 40, Position = new SKPoint(20, 5), Color = SKColors.Black });
            layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 6, Color = SKColors.Red })]);
        }
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        Assert.True(layer.FxCache.Rendered);

        // 快取裡有紅色外框像素（文字本身是黑的）
        var foundRed = false;
        foreach (var (idx, tile) in layer.FxCache.Surface.Tiles)
        {
            unsafe
            {
                var p = (uint*)tile.Pixels;
                for (var i = 0; i < Tile.Size * Tile.Size && !foundRed; i++)
                    if (A(p[i]) > 200 && R(p[i]) > 200 && G(p[i]) < 60) foundRed = true;
            }
            _ = idx;
        }
        Assert.True(foundRed);

        // 合成結果也是走快取（文字不會再疊一次）
        using var image = Compositing.Compositor.RenderComposite(doc);
        using var bmp = SKBitmap.FromImage(image);
        var anyRed = false;
        for (var y = 0; y < bmp.Height && !anyRed; y++)
        for (var x = 0; x < bmp.Width && !anyRed; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 200 && c.Red > 200 && c.Green < 60) anyRed = true;
        }
        Assert.True(anyRed);
    }

    [Fact]
    public void Mpp_MigratesLegacyTextEffects_AndSplitsMixedLayer()
    {
        using var doc = ImageCodec.CreateBlankDocument(160, 80, SKColors.Gray);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.AddElement(new TextElement
            {
                Text = "A", FontSize = 30, Position = new SKPoint(10, 10),
                Stroke = new TextStroke { Width = 3, Color = SKColors.Blue, Outer = new TextStroke { Width = 2, Color = SKColors.White } },
                Shadow = new TextShadow { Distance = 5, Angle = 0, Blur = 4, Color = new SKColor(0, 0, 0, 128) },
                Glow = new TextGlow { Size = 8, Color = SKColors.Yellow },
                Gradient = new TextGradient { Start = SKColors.Red, End = SKColors.Green, Angle = 45 },
            });
            layer.AddElement(new TextElement { Text = "B", FontSize = 30, Position = new SKPoint(60, 10) });
        }
        var file = Path.Combine(Path.GetTempPath(), $"txt-{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(doc, file);
            using var loaded = MppFormat.Load(file);
            Assert.Equal(3, loaded.Root.Children.Count); // 像素層 + 兩個文字層
            var pixels = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
            Assert.Empty(pixels.Elements);
            Assert.True(pixels.Surface.TileCount > 0);

            var a = Assert.IsType<RasterLayer>(loaded.Root.Children[1]);
            Assert.Equal("A", a.Name);
            var text = Assert.IsType<TextElement>(a.Elements[0]);
            Assert.Null(text.Stroke);
            Assert.Null(text.Shadow);
            Assert.Null(text.Glow);
            Assert.Null(text.Gradient);
            var types = a.Effects.Select(e => e.Effect.GetType()).ToList();
            Assert.Equal([typeof(ObjectGradientEffect), typeof(ObjectOutlineEffect), typeof(ObjectOutlineEffect), typeof(ObjectShadowEffect), typeof(ObjectGlowEffect)], types);
            Assert.Equal(SKColors.Blue, ((ObjectOutlineEffect)a.Effects[1].Effect).Color);
            Assert.Equal(5, ((ObjectShadowEffect)a.Effects[3].Effect).OffsetX);
            Assert.Equal(50, ((ObjectShadowEffect)a.Effects[3].Effect).Opacity);

            var b = Assert.IsType<RasterLayer>(loaded.Root.Children[2]);
            Assert.Equal("B", b.Name);
            Assert.Empty(b.Effects);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ObjectGlowAndGradient_Render()
    {
        var src = new uint[40 * 40];
        for (var y = 15; y < 25; y++)
        for (var x = 10; x < 30; x++)
            src[y * 40 + x] = Pack(0, 0, 0, 255);

        var glowCtx = EffectContext.FromPixels(src, 40, 40, 20);
        new ObjectGlowEffect { Size = 6, Spread = 2, Color = SKColors.Yellow, Opacity = 100 }.Render(glowCtx);
        Assert.True(A(glowCtx.Dst[12 * 40 + 20]) > 0); // 形狀外有光暈
        Assert.Equal(255, A(glowCtx.Dst[20 * 40 + 20]));

        var gradCtx = EffectContext.FromPixels(src, 40, 40);
        new ObjectGradientEffect { Start = SKColors.Red, End = SKColors.Blue, Angle = 0 }.Render(gradCtx);
        Assert.Equal(0u, gradCtx.Dst[2 * 40 + 2]);
        Assert.True(R(gradCtx.Dst[20 * 40 + 11]) > B(gradCtx.Dst[20 * 40 + 11]));
        Assert.True(B(gradCtx.Dst[20 * 40 + 28]) > R(gradCtx.Dst[20 * 40 + 28]));
    }
}
