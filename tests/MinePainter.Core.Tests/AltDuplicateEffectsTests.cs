using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>Alt 拖曳複製物件到新圖層：圖層效果（外框、陰影…）與不透明度、混合模式都要一起帶走。</summary>
public class AltDuplicateEffectsTests
{
    [Fact]
    public void DuplicateElementToNewLayer_CopiesEffectsOpacityAndBlend()
    {
        var doc = ImageCodec.CreateBlankDocument(200, 200, SKColors.Transparent);
        using var session = new EditorSession(doc);
        var source = (RasterLayer)doc.ActiveLayer!;
        var text = new TextElement { Text = "Hi", Position = new SKPoint(20, 20) };
        lock (doc.SyncRoot)
        {
            source.AddElement(text);
            source.Opacity = 0.5f;
            source.BlendMode = BlendMode.Multiply;
            source.SetEffects(
            [
                LayerEffect.Create(new ObjectOutlineEffect { Width = 4, Color = SKColors.Red }),
                LayerEffect.Create(new ObjectShadowEffect { Blur = 7 }),
            ]);
        }

        var result = LayerCommands.DuplicateElementToNewLayer(doc, session.History, source, text);
        Assert.NotNull(result);
        var copy = result.Value.Layer;

        Assert.Equal(2, copy.Effects.Count);
        Assert.Equal(source.Effects[0].Effect, copy.Effects[0].Effect);
        Assert.Equal(source.Effects[1].Effect, copy.Effects[1].Effect);
        Assert.NotEqual(source.Effects[0].Id, copy.Effects[0].Id);   // 效果 Id 要是新的，兩層各自可調
        Assert.Equal(0.5f, copy.Opacity);
        Assert.Equal(BlendMode.Multiply, copy.BlendMode);
        Assert.Single(copy.Elements);
        Assert.Same(copy, doc.ActiveLayer);
    }
}
