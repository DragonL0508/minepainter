using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class EffectDragSnapshotTests
{
    private static EditorSession Create(out RasterLayer layer, out TextElement text)
    {
        var session = new EditorSession(ImageCodec.CreateBlankDocument(512, 384, SKColors.Transparent));
        session.Compositor.StopRendering();
        layer = (RasterLayer)session.Document.ActiveLayer!;
        text = new TextElement { Text = "TEST", FontSize = 90, Position = new SKPoint(60, 180) };
        layer.AddElement(text);
        layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 5 }),
            LayerEffect.Create(new SkewEffect { Horizontal = 45 })]);
        LayerEffectRenderer.RenderAllNow(session.Document);
        return session;
    }

    [Fact]
    public void 傾斜文字按下的快照與完整效果輸出逐像素相同()
    {
        using var session = Create(out var layer, out var text);
        var expected = LayerEffectRenderer.ReadPixels(layer.FxCache.Surface, session.Document.Bounds);
        lock (session.Document.SyncRoot) session.BeginElementOverlayLocked(layer, text);
        Assert.True(session.OverlayReusedCache);
        var overlay = Assert.IsType<EditorSession.ElementDragOverlay>(session.ElementOverlay);
        using var surface = SKSurface.Create(new SKImageInfo(512, 384, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(overlay.Image!, overlay.CurrentRect);
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        Assert.Equal(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), bitmap.Bytes);
        Assert.Empty(layer.FxCache.Surface.Tiles);
        var published = false;
        void OnPublished(LayerNode node) { if (ReferenceEquals(node, layer)) published = true; }
        LayerEffectRenderer.LayerRendered += OnPublished;
        try { LayerEffectRenderer.RenderAllNow(session.Document); }
        finally { LayerEffectRenderer.LayerRendered -= OnPublished; }
        Assert.True(layer.FxCache.UpToDate);
        Assert.False(published, "隱藏的純文字仍觸發整張画布的效果工作");
    }

    [Fact]
    public void 快速小數位移重用同一快照且不逐次縮放或偏移()
    {
        using var session = Create(out var layer, out var text);
        SKImage? image = null;
        SKRect initial = default;
        for (var i = 0; i < 20; i++)
        {
            var current = layer.FindElement(text.Id)!;
            var drag = new ElementDragHelper();
            lock (session.Document.SyncRoot) drag.BeginMoveLocked(session, layer, current, SKPoint.Empty);
            var overlay = session.ElementOverlay!;
            if (image == null) { image = overlay.Image; initial = overlay.InitialRect; }
            Assert.Same(image, overlay.Image);
            drag.Continue(session, new SKPoint(.4f, .4f));
            var rect = overlay.CurrentRect;
            Assert.InRange(rect.Left, initial.Left + .4f * (i + 1) - .001f, initial.Left + .4f * (i + 1) + .001f);
            Assert.InRange(rect.Width, initial.Width - .001f, initial.Width + .001f);
            Assert.InRange(rect.Height, initial.Height - .001f, initial.Height + .001f);
            drag.End(session);
        }
    }

    [Fact]
    public void 效果參數變更後不能重用舊殘影或過期效果快取()
    {
        using var session = Create(out var layer, out var text);
        var drag = new ElementDragHelper();
        lock (session.Document.SyncRoot) drag.BeginMoveLocked(session, layer, text, SKPoint.Empty);
        var image = session.ElementOverlay!.Image;
        drag.End(session);
        layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 20 })]);
        lock (session.Document.SyncRoot) session.BeginElementOverlayLocked(layer, text);
        Assert.False(session.OverlayReusedCache);
        Assert.NotSame(image, session.ElementOverlay!.Image);
        Assert.Null(session.Ghost);
    }
}
