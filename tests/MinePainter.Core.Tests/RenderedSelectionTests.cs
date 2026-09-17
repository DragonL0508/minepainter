using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 2026-09-17 使用者回報：用了變形效果後，魔術棒／物件選取選到的是還沒變形過的區域；
/// 應該選畫面上目前渲染的樣子。
/// </summary>
public class RenderedSelectionTests
{
    [Fact]
    public void 魔術棒_選到的是變形後畫面上的形狀()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(500, 400, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(new SKRectI(100, 100, 200, 300), SKColors.Red);
        layer.InvalidateAll();
        // 以下緣為基準往右倒 45°：最上面那一列往右推約 200px
        LayerEffectCommands.Add(doc, session.History, layer,
            LayerEffect.Create(new SkewEffect { Horizontal = 45f, Pivot = 2 }));

        LayerEffectRenderer.RenderLayerNow(doc, layer);
        byte baseAlpha, shownAlpha;
        lock (doc.SyncRoot)
        {
            using var b = ImageCommands.ReadRegion(layer.Surface, new SKRectI(340, 110, 341, 111));
            using var s = ImageCommands.ReadRegion(layer.DisplaySurface, new SKRectI(340, 110, 341, 111));
            baseAlpha = b.GetPixel(0, 0).Alpha;
            shownAlpha = s.GetPixel(0, 0).Alpha;
        }
        Assert.Equal(0, baseAlpha);     // 基底像素這裡是空的
        Assert.Equal(255, shownAlpha);  // 畫面上這裡是紅的（這個測試的前提）

        session.Tolerance = 10;
        session.Wand.OnPointerDown(new ToolPointerEvent(new SKPoint(340, 110), 1f), session);

        var selection = session.Selection;
        Assert.NotNull(selection);
        Assert.Equal(255, selection!.CoverageAt(340, 110));
        Assert.Equal(0, selection.CoverageAt(5, 5));     // 讀基底的話會選到整片透明背景
        Assert.Equal(0, selection.CoverageAt(110, 110)); // 變形前的位置現在是空的，不該在選取裡
    }
}
