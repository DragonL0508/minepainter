using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>圖層文字平面化：文字烙成像素、物件移除、單一步 undo 兩者一起還原。</summary>
public class FlattenTextTests
{
    private static int DarkPixels(RasterLayer layer, SKRectI docRect)
    {
        var count = 0;
        for (var y = docRect.Top; y < docRect.Bottom; y++)
        for (var x = docRect.Left; x < docRect.Right; x++)
        {
            var lx = x - layer.Offset.X;
            var ly = y - layer.Offset.Y;
            var idx = TileIndex.FromPixel(lx, ly);
            var tile = layer.Surface.GetTileForRead(idx);
            if (tile == null) continue;
            var r = idx.ToPixelRect();
            using var pm = tile.AsPixmap();
            var c = pm.GetPixelColor(lx - r.Left, ly - r.Top);
            if (c.Alpha > 200 && c.Red < 80) count++;
        }
        return count;
    }

    [Fact]
    public void FlattenText_BakesPixels_RemovesElements_SingleUndo()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(400, 300, SKColors.White));
        var doc = session.Document;
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            layer.Offset = new SKPointI(7, -3); // 有位移的圖層也要對得準
        }
        var text = new TextElement
        {
            Text = "平面化",
            FontFamily = "Microsoft JhengHei",
            FontSize = 64,
            Color = SKColors.Black,
            Position = new SKPoint(60, 80),
        };
        lock (doc.SyncRoot) layer.AddElement(text);
        var bounds = text.Bounds;
        Assert.Equal(0, DarkPixels(layer, bounds));

        Assert.True(LayerCommands.FlattenText(doc, session.History, layer));

        Assert.False(layer.HasElements);
        var baked = DarkPixels(layer, bounds);
        Assert.True(baked > 300, $"文字應烙進像素（深色像素 {baked}）");
        Assert.Equal("平面化文字", session.History.UndoLabel);

        session.Undo();
        Assert.Single(layer.Elements);
        Assert.Equal(text, layer.Elements[0]);
        Assert.Equal(0, DarkPixels(layer, bounds));

        session.Redo();
        Assert.False(layer.HasElements);
        Assert.Equal(baked, DarkPixels(layer, bounds));
    }

    [Fact]
    public void FlattenText_NoElements_ReturnsFalse_NoHistory()
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(100, 100, SKColors.White));
        var layer = (RasterLayer)session.Document.ActiveLayer!;
        Assert.False(LayerCommands.FlattenText(session.Document, session.History, layer));
        Assert.False(session.History.CanUndo);
    }
}
