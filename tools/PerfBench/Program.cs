using System.Diagnostics;
using System.Runtime.InteropServices;
using MinePainter.App.Rendering;
using MinePainter.Core.AI;
using MinePainter.Core.Adjustments;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;

// 可重跑的 CPU／raster 基準，不啟動視窗、不發送輸入、不把提交時間冒充 GPU 執行時間。
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; CPUs: {Environment.ProcessorCount}");
if (args.Contains("--drag"))
{
    DragBench.Run();
    return;
}
if (args.Contains("--gpu"))
{
    GpuBench.Run();
    return;
}
Console.WriteLine("Median of 5 samples after 2 warmups. Rendering backend: CPU raster (not GPU timing).");
const int size = 1024;
var mask = new byte[size * size];
new Random(718).NextBytes(mask);
foreach (var radius in new[] { 1, 16, 64 })
{
    foreach (var direction in new[] { -1, 1 })
    {
        var shift = radius * direction;
        var expected = LegacyShift(mask, size, size, shift);
        var actual = BackgroundRemover.Shift(mask, size, size, shift);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Morphology output differs");
        var baseline = Median(() => GC.KeepAlive(LegacyShift(mask, size, size, shift)));
        var optimized = Median(() => GC.KeepAlive(BackgroundRemover.Shift(mask, size, size, shift)));
        Console.WriteLine($"Mask {size}x{size}, shift {shift}: legacy {baseline:F2} ms; optimized {optimized:F2} ms; ratio {baseline / optimized:F2}x; pixels equal");
    }
}

foreach (var adjustments in new[] { 1, 5, 10 })
{
    using var doc = ImageCodec.CreateBlankDocument(512, 512, SKColors.Red);
    for (var i = 0; i < adjustments; i++)
        doc.Root.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(0.02f, -0.03f)) { Opacity = 0.5f });
    using var session = new EditorSession(doc);
    session.Compositor.StopRendering();
    using var renderer = new GpuLayerRenderer();
    using var target = SKSurface.Create(new SKImageInfo(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul));
    var ms = Median(() =>
    {
        target.Canvas.Clear(SKColors.Transparent);
        lock (doc.SyncRoot)
            if (!renderer.TryDraw(target.Canvas, session, doc.Bounds)) throw new InvalidOperationException("Unexpected fallback");
        target.Canvas.Flush();
    });
    Console.WriteLine($"Adjustment stack {adjustments}: {ms:F2} ms; source tile draws {renderer.LastTiles}; theoretical legacy draws {4 * (1 << adjustments)}");
}

using (var doc = ImageCodec.CreateBlankDocument(2048, 2048, SKColors.Red))
using (var session = new EditorSession(doc))
using (var renderer = new GpuLayerRenderer())
using (var target = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul)))
{
    session.Compositor.StopRendering();
    target.Canvas.Scale(0.125f);
    void Draw()
    {
        target.Canvas.Clear(SKColors.Transparent);
        lock (doc.SyncRoot) renderer.TryDraw(target.Canvas, session, doc.Bounds, 0.125);
        target.Canvas.Flush();
    }
    Draw();
    Console.WriteLine($"LOD cold: source copies {renderer.LastSourceCopies}; builds {renderer.LastLodBuilds}");
    var warm = Median(Draw);
    Console.WriteLine($"LOD warm: {warm:F3} ms; source copies {renderer.LastSourceCopies}; builds {renderer.LastLodBuilds}");
    ((RasterLayer)doc.ActiveLayer!).Surface.Fill(new SKRectI(0, 0, 256, 256), SKColors.Blue);
    Draw();
    Console.WriteLine($"LOD one tile edit: source copies {renderer.LastSourceCopies}; builds {renderer.LastLodBuilds}; retained pixels {renderer.CachedImageBytes / 1048576.0:F2} MiB");
}

static double Median(Action action)
{
    action();
    action();
    var times = new double[5];
    for (var i = 0; i < times.Length; i++)
    {
        var start = Stopwatch.GetTimestamp();
        action();
        times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    Array.Sort(times);
    return times[times.Length / 2];
}

// 保留重構前的兩趟 O(Nr) 算法作比較，與正式實作使用相同 Parallel.For 與邊界規則。
static byte[] LegacyShift(byte[] source, int width, int height, int shift)
{
    var radius = Math.Abs(shift);
    var maximum = shift > 0;
    var temp = new byte[source.Length];
    var result = new byte[source.Length];
    Parallel.For(0, height, y =>
    {
        for (var x = 0; x < width; x++)
        {
            int value = maximum ? 0 : 255;
            for (var k = -radius; k <= radius; k++)
            {
                var pixel = source[y * width + Math.Clamp(x + k, 0, width - 1)];
                value = maximum ? Math.Max(value, pixel) : Math.Min(value, pixel);
            }
            temp[y * width + x] = (byte)value;
        }
    });
    Parallel.For(0, height, y =>
    {
        for (var x = 0; x < width; x++)
        {
            int value = maximum ? 0 : 255;
            for (var k = -radius; k <= radius; k++)
            {
                var pixel = temp[Math.Clamp(y + k, 0, height - 1) * width + x];
                value = maximum ? Math.Max(value, pixel) : Math.Min(value, pixel);
            }
            result[y * width + x] = (byte)value;
        }
    });
    return result;
}
