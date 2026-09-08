using System.Diagnostics;
using MinePainter.App.Rendering;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

internal static class DragBench
{
    public static void Run()
    {
        using var gpu = new OffscreenGpu();
        Console.WriteLine($"Renderer: {gpu.Renderer}");
        foreach (var workers in new[] { false, true })
        foreach (var transform in new[] { false, true })
        {
            using var session = new EditorSession(ImageCodec.CreateBlankDocument(3840, 2160, SKColors.White));
            var doc = session.Document;
            if (!workers) session.Compositor.StopRendering();
            session.LiveElementRendering = true;
            EditorSession.RenderEffectsWhileDragging = true;
            var layer = new RasterLayer();
            var text = new TextElement { Text = "效能測試 Performance\n帶效果文字拖曳", FontSize = 180, Position = new SKPoint(200, 300) };
            lock (doc.SyncRoot)
            {
                doc.Root.Add(layer);
                doc.ActiveLayer = layer;
                layer.AddElement(text);
                layer.SetEffects([LayerEffect.Create(new ObjectOutlineEffect { Width = 12 }),
                    LayerEffect.Create(new ObjectShadowEffect { OffsetX = 16, OffsetY = 16, Blur = 24 })]);
            }
            LayerEffectRenderer.RenderAllNow(doc);
            session.ActiveTool = session.Move;
            if (transform) session.BeginTransform();
            using var renderer = new GpuLayerRenderer();
            using var target = SKSurface.Create(gpu.Context, true,
                new SKImageInfo(3840, 2160, SKColorType.Bgra8888, SKAlphaType.Premul))!;
            void Draw()
            {
                target.Canvas.Clear(SKColors.Transparent);
                lock (doc.SyncRoot)
                    if (!renderer.TryDraw(target.Canvas, session, doc.Bounds, 1, gpu.Context))
                        throw new InvalidOperationException("Unexpected fallback");
                gpu.Finish();
            }
            Draw(); Draw();
            var frame = text.FrameBounds;
            var origin = new SKPoint(frame.MidX, frame.MidY);
            var start = Stopwatch.GetTimestamp();
            session.Move.OnPointerDown(new ToolPointerEvent(origin, 1), session);
            Console.WriteLine($"4K workers={workers}, transform={transform}: pointer down {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} ms");
            var moves = new double[180];
            var frames = new double[moves.Length];
            for (var i = 0; i < moves.Length; i++)
            {
                start = Stopwatch.GetTimestamp();
                session.Move.OnPointerMove(new ToolPointerEvent(new SKPoint(origin.X + i, origin.Y + i / 2f), 1), session);
                moves[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                Draw();
                frames[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Report("pointer move", moves);
            Report("GPU frame + finish", frames);
        }
    }

    private static void Report(string label, double[] samples)
    {
        Array.Sort(samples);
        Console.WriteLine($"{label}: median {samples[samples.Length / 2]:F3}; p95 {samples[(int)(samples.Length * .95)]:F3}; max {samples[^1]:F3} ms");
    }
}
