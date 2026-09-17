using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 2026-09-17 使用者回報：「顏色透明化之後有些操作會導致部分區域渲染效果消失，其中一個例子是先拖出畫布外再拖回來」。
/// 根因：效果「套用時的選取遮罩」釘在畫布上而不是圖層上，而且全選的遮罩只到畫布邊。
/// </summary>
public class EffectMaskAnchorTests
{
    private static (EditorSession Session, RasterLayer Layer) WhiteLayer(SKRectI content)
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(200, 200, SKColors.Transparent));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot) layer.Surface.Fill(content, SKColors.White);
        layer.InvalidateAll();
        return (session, layer);
    }

    private static SKColor Composite(EditorSession session, int x, int y)
    {
        using var image = Compositor.RenderComposite(session.Document);
        using var bmp = SKBitmap.FromImage(image);
        return bmp.GetPixel(x, y);
    }

    private static SelectionMask LeftHalf(EditorSession session)
    {
        using var path = new SKPath();
        path.AddRect(new SKRect(0, 0, 100, 200));
        return SelectionMask.FromPath(path, session.Document.Bounds);
    }

    [Fact]
    public void 全選後套效果_等於整層_畫布外的內容拖進來也有效果()
    {
        var (session, layer) = WhiteLayer(new SKRectI(0, 0, 500, 200)); // 比畫布寬很多
        EditCommands.SelectAll(session);
        var entry = LayerEffect.CreateFor(layer, session.Document.Bounds,
            new ColorToAlphaEffect { Color = SKColors.White }, session.Selection, SKColors.White);
        Assert.Null(entry.Mask);
        LayerEffectCommands.Add(session.Document, session.History, layer, entry);

        lock (session.Document.SyncRoot) layer.Offset = new SKPointI(-300, 0); // 原本在畫布外的那段拖進來
        layer.InvalidateComposite(session.Document.Bounds);
        Assert.Equal(0, Composite(session, 100, 100).Alpha);
        session.Dispose();
    }

    [Fact]
    public void 局部選取的遮罩_跟著圖層走()
    {
        var (session, layer) = WhiteLayer(new SKRectI(0, 0, 200, 200));
        session.Selection = LeftHalf(session); // 只有左半套效果
        var entry = LayerEffect.CreateFor(layer, session.Document.Bounds,
            new ColorToAlphaEffect { Color = SKColors.White }, session.Selection, SKColors.White);
        Assert.NotNull(entry.Mask);
        LayerEffectCommands.Add(session.Document, session.History, layer, entry);
        Assert.Equal(0, Composite(session, 50, 100).Alpha);
        Assert.Equal(255, Composite(session, 150, 100).Alpha);

        // 圖層右移 60 並強制整層重算（拖出畫布再拖回來時發生的事）：透明的仍是「圖層的左半」
        lock (session.Document.SyncRoot)
        {
            layer.Offset = new SKPointI(60, 0);
            layer.FxCache.MarkAllDirty();
        }
        layer.InvalidateComposite(session.Document.Bounds);
        Assert.Equal(0, Composite(session, 110, 100).Alpha);   // 圖層 x=50：仍在遮罩內
        Assert.Equal(255, Composite(session, 180, 100).Alpha); // 圖層 x=120：遮罩外，白色還在
        session.Dispose();
    }

    [Fact]
    public void 遮罩錨點存進mpp_讀回來位置不變()
    {
        var (session, layer) = WhiteLayer(new SKRectI(0, 0, 200, 200));
        session.Selection = LeftHalf(session);
        LayerEffectCommands.Add(session.Document, session.History, layer, LayerEffect.CreateFor(layer,
            session.Document.Bounds, new ColorToAlphaEffect { Color = SKColors.White }, session.Selection, SKColors.White));
        lock (session.Document.SyncRoot) layer.Offset = new SKPointI(60, 0);

        var file = Path.Combine(Path.GetTempPath(), $"mp_anchor_{Guid.NewGuid():N}.mpp");
        try
        {
            MppFormat.Save(session.Document, file);
            var loaded = MppFormat.Load(file);
            var loadedLayer = loaded.Descendants().OfType<RasterLayer>().Single(l => l.HasEffects);
            Assert.Equal(SKPointI.Empty, loadedLayer.Effects[0].MaskAnchor);
            using var image = Compositor.RenderComposite(loaded);
            using var bmp = SKBitmap.FromImage(image);
            Assert.Equal(0, bmp.GetPixel(110, 100).Alpha);
            Assert.Equal(255, bmp.GetPixel(180, 100).Alpha);
        }
        finally
        {
            File.Delete(file);
            session.Dispose();
        }
    }
}
