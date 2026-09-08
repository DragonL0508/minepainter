using System.Diagnostics;
using MinePainter.App.Rendering;
using MinePainter.Core.IO;
using MinePainter.Core.Tools;
using MinePainter.Core.Layers;
using MinePainter.Core.Effects;
using MinePainter.Core.Vectors;
using SkiaSharp;

internal static class FileDragBench
{
    public static void Run(string path)
    {
        using var gpu = new OffscreenGpu();
        Console.WriteLine($"Renderer: {gpu.Renderer}");
        RunCase(path, gpu, false);
        RunCase(path, gpu, true);
    }

    private static void RunCase(string path, OffscreenGpu gpu, bool workers)
    {
        using var session = new EditorSession(MppFormat.Load(path));
        if (!workers) session.Compositor.StopRendering();
        var doc = session.Document;
        session.LiveElementRendering = true;
        EditorSession.RenderEffectsWhileDragging = true;
        Console.WriteLine($"Document {doc.Width}x{doc.Height}, workers={workers}");
        LayerEffectRenderer.RenderAllNow(doc);
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(gpu.Context, true,
            new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul))!;
        void Draw()
        {
            target.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot)
            {
                if (!renderer.TryDraw(target.Canvas, session, doc.Bounds, 1, gpu.Context))
                    throw new InvalidOperationException("Unexpected CPU fallback");
                if (session.Ghost is { } ghost) target.Canvas.DrawImage(ghost.Image, ghost.Rect);
            }
            gpu.Finish();
        }
        foreach (var layer in doc.Descendants().OfType<RasterLayer>().Where(l => l.HasElements))
        foreach (var original in layer.Elements.OfType<TextElement>().ToArray())
        {
            Console.WriteLine($"Text bounds={original.Bounds}, effects={layer.Effects.Count}, offset={layer.Offset}, elements={layer.Elements.Count}");
            foreach (var effect in layer.Effects) Console.WriteLine($"  {effect.Effect.GetType().Name} enabled={effect.Enabled}");
            foreach (var name in new[] { "LastRegion", "LastClipped", "DirtyAll", "Dirty" })
                Console.WriteLine($"  {name}={typeof(LayerEffectCache).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(layer.FxCache)}");
            var element = (VectorElement)original;
            Draw(); Draw();
            using var beforeImage = target.Snapshot();
            using var before = SKBitmap.FromImage(beforeImage);
            for (var i = 0; i < 20; i++)
            {
                var drag = new ElementDragHelper();
                var start = Stopwatch.GetTimestamp();
                lock (doc.SyncRoot) drag.BeginMoveLocked(session, layer, element, SKPoint.Empty);
                var overlay = session.ElementOverlay;
                Console.WriteLine($"  down {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms image={overlay?.Image?.Width}x{overlay?.Image?.Height}, bounds={overlay?.Bounds}");
                start = Stopwatch.GetTimestamp();
                Draw();
                Console.WriteLine($"  GPU frame + finish {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms");
                if (i == 0)
                {
                    using var afterImage = target.Snapshot();
                    using var after = SKBitmap.FromImage(afterImage);
                    var a = before.Bytes;
                    var b = after.Bytes;
                    var max = 0;
                    for (var p = 0; p < a.Length; p++) max = Math.Max(max, Math.Abs(a[p] - b[p]));
                    Console.WriteLine($"  pointer-down pixel max difference={max}/255");
                    if (max > 1) throw new InvalidOperationException("Pointer down changed displayed pixels");
                }
                drag.Continue(session, new SKPoint(0.4f, 0.4f));
                drag.End(session);
                element = layer.FindElement(original.Id)!;
            }
            LayerEffectRenderer.RenderAllNow(doc);
            session.CollectOverlayGhost();
        }
    }
}
