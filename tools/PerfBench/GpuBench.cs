using System.Diagnostics;
using System.Reflection;
using MinePainter.App.Rendering;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;

/// <summary>實際 GPU 的像素與同步完成時間；獨立工具可在沒有 GPU 的 CI 上不執行。</summary>
internal static class GpuBench
{
    public static void Run()
    {
        using var gpu = new OffscreenGpu();
        Console.WriteLine($"Renderer: {gpu.Renderer}");
        Console.WriteLine("GPU timings include CPU submission and glFinish; median of 5 warmed frames, not GPU timestamps.");
        foreach (var count in new[] { 1, 5, 10 })
        foreach (var nested in new[] { false, true })
        {
            using var doc = ImageCodec.CreateBlankDocument(513, 385, SKColors.Transparent);
            var layer = (RasterLayer)doc.ActiveLayer!;
            var group = doc.Root;
            if (nested)
            {
                layer.Surface.Fill(doc.Bounds, SKColors.DarkBlue);
                group = new GroupLayer { Opacity = 0.65f };
                doc.Root.Add(group);
                layer = new RasterLayer();
                group.Add(layer);
            }
            layer.Surface.Fill(new SKRectI(7, 9, 490, 370), new SKColor(173, 51, 87, 137));
            for (var i = 0; i < count; i++)
                group.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(0.02f, -0.03f)) { Opacity = 0.5f });
            using var session = new EditorSession(doc);
            session.Compositor.StopRendering();
            using var renderer = new GpuLayerRenderer();
            var info = new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var target = SKSurface.Create(gpu.Context, true, info) ?? throw new InvalidOperationException("GPU surface unavailable");
            void Draw()
            {
                target.Canvas.Clear(SKColors.Transparent);
                lock (doc.SyncRoot)
                    if (!renderer.TryDraw(target.Canvas, session, doc.Bounds, 1, gpu.Context))
                        throw new InvalidOperationException("Unexpected CPU fallback");
                gpu.Finish();
            }
            Draw();
            Draw();
            var samples = new double[5];
            for (var i = 0; i < samples.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                Draw();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(samples);
            using var rendered = target.Snapshot();
            using var reference = Compositor.RenderComposite(doc);
            using var actual = SKBitmap.FromImage(rendered);
            using var expected = SKBitmap.FromImage(reference);
            var a = actual.Bytes;
            var b = expected.Bytes;
            int max = 0, overTolerance = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var delta = Math.Abs(a[i] - b[i]);
                max = Math.Max(max, delta);
                if (delta > 2) overTolerance++;
            }
            Console.WriteLine($"Adjustments {count}, nested {nested}: {samples[2]:F3} ms; tile draws {renderer.LastTiles}; max channel error {max}; bytes over 2/255: {overTolerance}");
            if (!nested)
            {
                // 直接對照仍保留的配置失敗退路，避免把原有 GPU 捨入誤差歸給新管線。
                // 只在量測工具反射此私有入口，正式 App 不增加效能開關。
                var legacy = typeof(GpuLayerRenderer).GetMethod("DrawRange", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void DrawLegacy()
                {
                    target.Canvas.Clear(SKColors.Transparent);
                    lock (doc.SyncRoot) legacy.Invoke(renderer, [target.Canvas, session, doc.Root.Children, doc.Root.Children.Count, doc.Bounds]);
                    gpu.Finish();
                }
                DrawLegacy();
                DrawLegacy();
                var legacySamples = new double[5];
                for (var i = 0; i < legacySamples.Length; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    DrawLegacy();
                    legacySamples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                Array.Sort(legacySamples);
                using var legacyImage = target.Snapshot();
                using var legacyBitmap = SKBitmap.FromImage(legacyImage);
                var old = legacyBitmap.Bytes;
                int legacyError = 0, changed = 0;
                for (var i = 0; i < old.Length; i++)
                {
                    legacyError = Math.Max(legacyError, Math.Abs(old[i] - b[i]));
                    changed = Math.Max(changed, Math.Abs(old[i] - a[i]));
                }
                Console.WriteLine($"Legacy warmed median: {legacySamples[2]:F3} ms; ratio {legacySamples[2] / samples[2]:F2}x; max CPU error {legacyError}; max optimized/legacy difference {changed}");
                if (changed != 0) throw new InvalidOperationException("New GPU pipeline differs from legacy pixels");
            }
            // 十層的既有 GPU 路徑也累积到 3/255；另以上面的新舊逐位元組相等把關。
            if (max > 3) throw new InvalidOperationException("GPU pixels exceed the reference scene's rounding budget");
        }
        ValidateLod(gpu);
        MeasureLargeViewport(gpu);
    }

    private static void ValidateLod(OffscreenGpu gpu)
    {
        using var doc = ImageCodec.CreateBlankDocument(1024, 1024, SKColors.Red);
        using var session = new EditorSession(doc);
        session.Compositor.StopRendering();
        using var renderer = new GpuLayerRenderer();
        var info = new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var target = SKSurface.Create(gpu.Context, true, info) ?? throw new InvalidOperationException();
        target.Canvas.Scale(0.25f);
        void Draw()
        {
            target.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot) renderer.TryDraw(target.Canvas, session, doc.Bounds, 0.25, gpu.Context);
            gpu.Finish();
        }
        Draw();
        Draw();
        if (renderer.LastLodBuilds != 0) throw new InvalidOperationException("GPU LOD cache missed");
        ((RasterLayer)doc.ActiveLayer!).Surface.Fill(new SKRectI(0, 0, 256, 256), SKColors.Blue);
        Draw();
        if (renderer.LastSourceCopies != 1) throw new InvalidOperationException("GPU LOD recopied unchanged sources");
        using (var image = target.Snapshot())
        using (var bitmap = SKBitmap.FromImage(image))
        {
            if (bitmap.GetPixel(10, 10) != SKColors.Blue || bitmap.GetPixel(100, 100) != SKColors.Red)
                throw new InvalidOperationException("GPU LOD pixels differ");
        }
        using (var raster = SKSurface.Create(info))
        {
            raster.Canvas.Scale(0.25f);
            lock (doc.SyncRoot) renderer.TryDraw(raster.Canvas, session, doc.Bounds, 0.25);
        }
        Draw();
        if (renderer.LastSourceCopies != 16 || renderer.LastLodBuilds != 1)
            throw new InvalidOperationException("Context switch reused old GPU resources");
        Console.WriteLine("GPU LOD: warm reuse, one-tile update, pixels and GPU/raster/context switch passed");
    }

    private static void MeasureLargeViewport(OffscreenGpu gpu)
    {
        using var doc = ImageCodec.CreateBlankDocument(3840, 2160, SKColors.DarkRed);
        for (var i = 0; i < 10; i++)
            doc.Root.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(0.02f, -0.03f)) { Opacity = 0.5f });
        using var session = new EditorSession(doc);
        session.Compositor.StopRendering();
        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(gpu.Context, true,
            new SKImageInfo(3840, 2160, SKColorType.Bgra8888, SKAlphaType.Premul)) ?? throw new InvalidOperationException();
        void Draw()
        {
            target.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot) renderer.TryDraw(target.Canvas, session, doc.Bounds, 1, gpu.Context);
            gpu.Finish();
        }
        Draw();
        Draw();
        var samples = new double[5];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Draw();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"4K viewport, 10 adjustments: {samples[2]:F3} ms median CPU+GPU completion; source tile draws {renderer.LastTiles}");
    }
}
