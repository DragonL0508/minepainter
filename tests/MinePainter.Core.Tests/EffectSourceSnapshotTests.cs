using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class EffectSourceSnapshotTests
{
    [Fact]
    public async Task EffectSource_DoesNotPublishResultInvalidatedWhileRendering()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var element = new ProbeElement(canvas =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            canvas.Clear(SKColors.Red);
        });
        layer.AddElement(element);
        layer.SetEffects([LayerEffect.Create(new GaussianBlurEffect { Radius = 0 })]);
        var stalePublished = false;
        void Published(LayerNode node)
        {
            if (!ReferenceEquals(node, layer)) return;
            lock (doc.SyncRoot)
                stalePublished |= LayerEffectRenderer.ReadPixels(layer.FxCache.Surface, doc.Bounds).Any(p => p != 0);
        }
        LayerEffectRenderer.LayerRendered += Published;
        var work = Task.Run(() => LayerEffectRenderer.RenderLayerNow(doc, layer));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            lock (doc.SyncRoot) layer.HiddenElementId = element.Id;
        }
        finally
        {
            release.Set();
            try { await work; }
            finally { LayerEffectRenderer.LayerRendered -= Published; }
        }
        Assert.False(stalePublished, "已藏起文字的過期工作被發布，會在拖曳中重新閃現");
        Assert.True(layer.FxCache.UpToDate);
    }

    private sealed record ProbeElement(Action<SKCanvas> Draw) : VectorElement
    {
        public override SKRectI Bounds => new(0, 0, 64, 64);
        public override void Render(SKCanvas canvas) => Draw(canvas);
        public override VectorElement Translated(float dx, float dy) => this;
        public override VectorElement TransformedBy(SKMatrix matrix, float sx, float sy, float rotationDeg) => this;
    }

    [Fact]
    public async Task EffectSource_RasterizesOutsideDocumentLock()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        layer.AddElement(new ProbeElement(canvas =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, 64, 64), paint);
        }));
        layer.SetEffects([LayerEffect.Create(new GaussianBlurEffect { Radius = 0 })]);
        var work = Task.Run(() => LayerEffectRenderer.RenderLayerNow(doc, layer));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "來源點陣化未開始");
            var acquired = Monitor.TryEnter(doc.SyncRoot, TimeSpan.FromMilliseconds(500));
            if (acquired) Monitor.Exit(doc.SyncRoot);
            Assert.True(acquired, "來源點陣化持有文件鎖，編輯與畫布仍被阻塞");
        }
        finally
        {
            release.Set();
            await work;
        }
        Assert.True(layer.FxCache.UpToDate);
    }

    [Fact]
    public void EffectSource_SurvivesPixelWritesAndElementRemoval()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Red);
        var layer = (RasterLayer)doc.ActiveLayer!;
        var element = new ProbeElement(canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Green };
            canvas.DrawRect(new SKRect(8, 8, 16, 16), paint);
        });
        layer.AddElement(element);
        using var source = RasterEffectSource.Capture(layer, doc.Bounds);
        layer.Surface.Fill(doc.Bounds, SKColors.Blue);
        layer.RemoveElement(element.Id);
        layer.Offset = new SKPointI(20, 20);
        var pixels = source.Read(CancellationToken.None);
        Assert.Equal((uint)SKColors.Red, pixels[0]);
        Assert.Equal((uint)SKColors.Green, pixels[10 * 64 + 10]);
        Assert.Throws<OperationCanceledException>(() => source.Read(new CancellationToken(true)));
    }

    [Fact]
    public void Preview_ChangingRadiusReusesSameClippedSource()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.Red);
        using var session = new EditorSession(doc);
        using var preview = new EffectSession(session, (RasterLayer)doc.ActiveLayer!);
        var initial = preview.CreateContext(new GaussianBlurEffect { Radius = 1 });
        for (var radius = 2; radius <= 100; radius++)
        {
            var next = preview.CreateContext(new GaussianBlurEffect { Radius = radius });
            Assert.Same(initial.Src, next.Src);
            Assert.NotSame(initial.Dst, next.Dst);
        }
        ((RasterLayer)doc.ActiveLayer!).Surface.Fill(doc.Bounds, SKColors.Blue);
        var after = preview.CreateContext(new GaussianBlurEffect { Radius = 5 });
        Assert.Equal((uint)SKColors.Red, after.Src[0]);
    }
}
