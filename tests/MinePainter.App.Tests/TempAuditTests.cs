using MinePainter.App.Rendering;
using MinePainter.Core.Compositing;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MinePainter.App.Tests;

public class TempAuditTests(ITestOutputHelper output)
{
    private const string Path = @"C:\Users\chung\Downloads\孩子回家.mpp";
    private static string Out(string n) => System.IO.Path.Combine(
        @"C:\Users\chung\AppData\Local\Temp\claude\C--Users-chung-OneDrive----minepainter\c7a26f27-2fa9-47a5-a253-60047147ed1c\scratchpad", n);

    /// <summary>每一種「拿掉一個效果」的組合都跟 CPU 合成器對拍。</summary>
    [Fact]
    public void 各種效果組合都要與合成器一致()
    {
        var layerNames = new List<string>();
        {
            var probe = MppFormat.Load(Path);
            layerNames.AddRange(probe.Descendants().OfType<RasterLayer>()
                .Where(l => l.HasEffects).Select(l => l.Name));
        }
        output.WriteLine($"有效果的圖層：{string.Join("／", layerNames)}");

        foreach (var name in layerNames)
        {
            var count = CountEffects(name);
            for (var drop = -1; drop < count; drop++)
            {
                var (diff, worst) = Compare(name, drop);
                output.WriteLine($"{name}｜{(drop < 0 ? "原樣" : $"移除第 {drop + 1} 個效果")}：" +
                                 $"不同像素 {diff:P4}・最大差 {worst}");
                Assert.True(diff < 0.0001, $"{name} drop={drop} 有 {diff:P4} 的像素與合成器不同");
            }
        }
    }

    private static int CountEffects(string layerName)
    {
        var doc = MppFormat.Load(Path);
        return doc.Descendants().OfType<RasterLayer>().First(l => l.Name == layerName).Effects.Count;
    }

    private (double Diff, int Worst) Compare(string layerName, int drop)
    {
        var doc = MppFormat.Load(Path);
        var session = new EditorSession(doc);
        var layer = doc.Descendants().OfType<RasterLayer>().First(l => l.Name == layerName);
        if (drop >= 0)
        {
            lock (doc.SyncRoot)
            {
                layer.SetEffects(layer.Effects.Where((_, i) => i != drop).ToList());
            }
        }
        LayerEffectRenderer.RenderAllNow(doc);

        var info = new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var gpu = SKSurface.Create(info);
        using (var r = new GpuLayerRenderer())
        {
            gpu.Canvas.Clear(SKColors.Transparent);
            lock (doc.SyncRoot) Assert.True(r.TryDraw(gpu.Canvas, session, new SKRectI(0, 0, doc.Width, doc.Height)));
        }
        gpu.Canvas.Flush();
        using var gpuImage = gpu.Snapshot();
        using var cpuImage = Compositor.RenderComposite(doc);
        return DiffOf(gpuImage, cpuImage, info);
    }

    private static (double, int) DiffOf(SKImage a, SKImage b, SKImageInfo info)
    {
        using var ba = new SKBitmap(info);
        using var bb = new SKBitmap(info);
        Assert.True(a.ReadPixels(info, ba.GetPixels(), info.RowBytes, 0, 0));
        Assert.True(b.ReadPixels(info, bb.GetPixels(), info.RowBytes, 0, 0));
        var pa = ba.Bytes;
        var pb = bb.Bytes;
        long bad = 0;
        var worst = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            var d = 0;
            for (var c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(pa[i + c] - pb[i + c]));
            if (d > worst) worst = d;
            if (d > 8) bad++;
        }
        return ((double)bad / (pa.Length / 4), worst);
    }
}
