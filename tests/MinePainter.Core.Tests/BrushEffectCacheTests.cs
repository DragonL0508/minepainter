using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 筆刷拖曳中不能把效果快取標髒：筆劃還在 StrokeBuffer、圖層像素沒動，效果堆疊沒理由重算。
/// 以前每動一下滑鼠就重算一次、合成器又要等它算完，效果多的專案畫起來一頓一頓（2026-09-07 回報）。
/// </summary>
public class BrushEffectCacheTests
{
    private static (EditorSession Session, RasterLayer Layer) NewSession()
    {
        var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
            layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 3 })]);
        LayerEffectRenderer.RenderLayerNow(doc, layer);
        lock (doc.SyncRoot) Assert.True(layer.FxCache.UpToDate, "前置：效果快取要先算好");
        return (session, layer);
    }

    [Fact]
    public void 拖曳中效果快取保持最新_放開才標髒()
    {
        var (session, layer) = NewSession();
        using var _ = session;
        var doc = session.Document;
        var tool = session.Brush;
        tool.Settings.Radius = 10;

        var changed = 0;
        doc.Changed += _ => changed++;

        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(40, 40), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(80, 40), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(120, 40), 1f), session);

        lock (doc.SyncRoot)
        {
            Assert.True(layer.FxCache.UpToDate, "拖曳中效果快取被標髒了：效果堆疊會每動一下滑鼠就重算一次");
            Assert.True(session.StrokeBuffer.ShouldOverlay(layer), "筆劃要靠合成器疊在舊快取上預覽");
        }
        Assert.True(changed >= 1, "合成器仍要收到通知，不然畫面上看不到正在畫的筆劃");

        tool.OnPointerUp(new ToolPointerEvent(new SKPoint(120, 40), 1f), session);
        lock (doc.SyncRoot)
            Assert.False(layer.FxCache.UpToDate, "烙進圖層後像素真的變了，這時才該重算效果");
    }

    [Fact]
    public void 拖曳中祖先群組快取仍會標髒()
    {
        var (session, layer) = NewSession();
        using var _ = session;
        var doc = session.Document;
        GroupLayer group;
        lock (doc.SyncRoot)
        {
            group = new GroupLayer { Name = "群組" };
            doc.Root.Remove(layer);
            group.Add(layer);
            doc.Root.Add(group);
            doc.ActiveLayer = layer;
            group.Cache.MarkClean(Tiles.TileIndex.FromPixel(40, 40)); // 假裝合成器已把這格算好
        }

        var tool = session.Brush;
        tool.Settings.Radius = 10;
        tool.OnPointerDown(new ToolPointerEvent(new SKPoint(40, 40), 1f), session);
        tool.OnPointerMove(new ToolPointerEvent(new SKPoint(80, 40), 1f), session);

        lock (doc.SyncRoot)
            Assert.False(group.Cache.IsClean(Tiles.TileIndex.FromPixel(40, 40)),
                "群組快取沒被標髒：群組裡的圖層畫筆劃時畫面不會更新");
    }
}
