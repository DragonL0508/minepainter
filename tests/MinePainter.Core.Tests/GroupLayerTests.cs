using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class GroupLayerTests
{
    private static SKColor ReadPixel(Compositor compositor, int x, int y, int timeoutMs = 3000)
    {
        var idx = TileIndex.FromPixel(x, y);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (compositor.TryGetTile(idx, out _))
            {
                // 等 dirty 清空後再採樣（避免拿到舊圖）
                Thread.Sleep(30);
                var c = compositor.SamplePixel(x, y);
                return c;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException();
    }

    private static RasterLayer SolidLayer(string name, SKRectI rect, SKColor color)
    {
        var layer = new RasterLayer { Name = name };
        layer.Surface.Fill(rect, color);
        return layer;
    }

    [Fact]
    public void GroupIsolation_BlendChildOnlyAffectsSiblings()
    {
        // 底層：白。群組內：灰底 + Multiply 紅。
        // isolated 語意：紅只與群組內灰相乘，再以 Normal 疊到白上。
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var group = new GroupLayer { Name = "g" };
        var grey = SolidLayer("grey", new SKRectI(0, 0, 256, 256), new SKColor(200, 200, 200));
        var red = SolidLayer("red", new SKRectI(0, 0, 128, 256), new SKColor(255, 0, 0));
        red.BlendMode = BlendMode.Multiply;

        lock (doc.SyncRoot)
        {
            doc.Root.Add(group);
            group.Add(grey);
            group.Add(red);
        }

        using var compositor = new Compositor(doc);
        // 左半：200 × 紅(255,0,0) multiply → (200, 0, 0)
        var left = ReadPixel(compositor, 64, 128);
        Assert.Equal(new SKColor(200, 0, 0), left);
        // 右半：只有灰
        var right = ReadPixel(compositor, 200, 128);
        Assert.Equal(new SKColor(200, 200, 200), right);
    }

    [Fact]
    public void GroupOpacity_AppliesToWholeGroup()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var group = new GroupLayer { Name = "g", Opacity = 0.5f };
        group.Add(SolidLayer("black", new SKRectI(0, 0, 256, 256), SKColors.Black));
        lock (doc.SyncRoot) doc.Root.Add(group);

        using var compositor = new Compositor(doc);
        var px = ReadPixel(compositor, 128, 128);
        Assert.InRange(px.Red, 126, 130); // 白 + 50% 黑 = 中灰
    }

    [Fact]
    public void EditInsideNestedGroup_InvalidatesThroughCaches()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var outer = new GroupLayer { Name = "outer" };
        var inner = new GroupLayer { Name = "inner" };
        var layer = SolidLayer("content", new SKRectI(0, 0, 256, 256), new SKColor(0, 0, 200));

        lock (doc.SyncRoot)
        {
            doc.Root.Add(outer);
            outer.Add(inner);
            inner.Add(layer);
        }

        using var compositor = new Compositor(doc);
        Assert.Equal(new SKColor(0, 0, 200), ReadPixel(compositor, 128, 128));

        // 改巢狀圖層內容 → 兩層群組快取都該失效並重合成
        lock (doc.SyncRoot)
        {
            layer.Surface.Fill(new SKRectI(0, 0, 256, 256), new SKColor(0, 200, 0));
        }
        layer.Invalidate(new SKRectI(0, 0, 256, 256));

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (compositor.SamplePixel(128, 128) == new SKColor(0, 200, 0)) return;
            Thread.Sleep(20);
        }
        Assert.Fail("巢狀群組編輯後未重新合成");
    }

    [Fact]
    public void InsertRemove_Undoable()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        using var history = new HistoryManager(doc);
        var layer = SolidLayer("added", new SKRectI(0, 0, 256, 256), SKColors.Black);

        LayerCommands.InsertLayer(doc, history, doc.Root, doc.Root.Children.Count, layer);
        Assert.Equal(2, doc.Root.Children.Count);

        history.Undo();
        Assert.Single(doc.Root.Children);
        Assert.Null(layer.Document);

        history.Redo();
        Assert.Equal(2, doc.Root.Children.Count);
        Assert.Same(doc, layer.Document);
    }

    [Fact]
    public void WrapInGroup_Undoable()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        using var history = new HistoryManager(doc);
        var layer = (RasterLayer)doc.Root.Children[0];

        var group = LayerCommands.WrapInGroup(doc, history, layer);
        Assert.Same(group, doc.Root.Children[0]);
        Assert.Same(layer, group.Children[0]);

        history.Undo();
        Assert.Same(layer, doc.Root.Children[0]);
        Assert.Null(group.Document);

        history.Redo();
        Assert.Same(group, doc.Root.Children[0]);
        Assert.Same(layer, group.Children[0]);
    }

    [Fact]
    public void MoveNode_ReordersSiblings_Undoable()
    {
        using var doc = new Document(256, 256);
        using var history = new HistoryManager(doc);
        var a = new RasterLayer { Name = "a" };
        var b = new RasterLayer { Name = "b" };
        lock (doc.SyncRoot)
        {
            doc.Root.Add(a);
            doc.Root.Add(b);
        }

        LayerCommands.MoveNode(doc, history, a, doc.Root, 1); // a 移到頂
        Assert.Equal(new[] { "b", "a" }, doc.Root.Children.Select(c => c.Name));

        history.Undo();
        Assert.Equal(new[] { "a", "b" }, doc.Root.Children.Select(c => c.Name));
    }

    [Fact]
    public void HiddenLayer_NotComposited()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.White);
        var top = SolidLayer("top", new SKRectI(0, 0, 256, 256), SKColors.Black);
        top.IsVisible = false;
        lock (doc.SyncRoot) doc.Root.Add(top);

        using var compositor = new Compositor(doc);
        Assert.Equal(SKColors.White, ReadPixel(compositor, 128, 128));
    }
}
