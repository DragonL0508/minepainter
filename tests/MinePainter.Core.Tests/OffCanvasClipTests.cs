using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 效果只算「看得到的那塊」：畫布外的部分算了也永遠不會被合成到（合成器只走畫布內的 tile），
/// 但成本照付 —— 一個大部分在畫布外的大物件，每次都在算不存在的畫面。
/// 往外仍留 margin：畫布外的內容，它的外框／陰影還是可能伸進畫布裡。
/// </summary>
public class OffCanvasClipTests
{
    private static (EditorSession Session, RasterLayer Layer) NewDoc(SKRectI content)
    {
        var doc = new Document(200, 200);
        var session = new EditorSession(doc);
        var layer = new RasterLayer { Name = "內容" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(layer);
            layer.Surface.Fill(content, SKColors.Red);
            doc.ActiveLayer = layer;
        }
        layer.InvalidateAll();
        return (session, layer);
    }

    private static void AddOutline(EditorSession session, RasterLayer layer, int width) =>
        LayerEffectCommands.Add(session.Document, session.History, layer,
            LayerEffect.Create(new ObjectOutlineEffect { Width = width }, color: SKColors.Black));

    [Fact]
    public void ContentFarOutsideTheCanvas_IsNotComputedAtAll()
    {
        // 內容整塊在畫布右邊很遠處，連外框都碰不到畫布
        var (session, layer) = NewDoc(new SKRectI(2000, 2000, 2200, 2200));
        AddOutline(session, layer, 4);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        Assert.True(layer.FxCache.LastClipped);
        Assert.Equal(0, layer.FxCache.LastRegion.Width);   // 完全不算
        Assert.Equal(0, layer.FxCache.Surface.TileCount);  // 也不佔記憶體
        session.Dispose();
    }

    [Fact]
    public void OutlineOfOffCanvasContent_StillReachesIntoTheCanvas()
    {
        // 內容剛好貼在畫布左外側；外框要能伸進畫布裡（裁切保留了 margin）
        var (session, layer) = NewDoc(new SKRectI(-60, 50, -4, 150));
        AddOutline(session, layer, 10);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        using var image = Compositor.RenderComposite(session.Document);
        using var bmp = SKBitmap.FromImage(image);
        var edge = bmp.GetPixel(2, 100);
        Assert.True(edge.Alpha > 0, "畫布外內容的外框應該還是要畫進畫布裡");
        Assert.True(edge.Red < 100 && edge.Green < 100, $"應該是黑色外框，拿到 {edge}");
        session.Dispose();
    }

    [Fact]
    public void ContentInsideTheCanvas_IsNotClipped_SoMovingTheLayerDoesNotRecompute()
    {
        var (session, layer) = NewDoc(new SKRectI(60, 60, 140, 140));
        AddOutline(session, layer, 4);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        Assert.False(layer.FxCache.LastClipped); // 整塊都在畫布內：沒裁到
        Assert.False(layer.FxCache.HasPending);

        // 快取與畫布無關 → 平移圖層不必整份重算（這是效果快取用圖層座標的原因）
        lock (session.Document.SyncRoot) layer.Offset = new SKPointI(5, 5);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.False(layer.FxCache.DirtyAll);
        session.Dispose();
    }

    [Fact]
    public void MovingAClippedLayer_RecomputesTheNewlyVisiblePart()
    {
        // 內容比畫布寬很多：一定會被裁，這時快取就與「畫布落在圖層的哪裡」有關了
        var (session, layer) = NewDoc(new SKRectI(0, 50, 1200, 150));
        AddOutline(session, layer, 4);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);
        Assert.True(layer.FxCache.LastClipped);
        Assert.False(layer.FxCache.HasPending);

        lock (session.Document.SyncRoot) layer.Offset = new SKPointI(-500, 0);
        LayerEffectRenderer.RenderLayerNow(session.Document, layer);

        // 平移後看得到的是另一段內容，重算過才對
        using var image = Compositor.RenderComposite(session.Document);
        using var bmp = SKBitmap.FromImage(image);
        Assert.Equal(SKColors.Red, bmp.GetPixel(100, 100));
        session.Dispose();
    }
}
