using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;
using static MinePainter.Core.Effects.EffectMath;

namespace MinePainter.Core.Tests;

/// <summary>去背後的硬邊切出：沒有半透明毛邊、碎片與小洞清掉、邊緣的背景色汙染換成內部顏色（使用者 2026-09-06）。</summary>
public class HardEdgeCutTests
{
    /// <summary>64×64：紅方塊（16..48）疊在綠底上，遮罩是 6 格寬的線性軟邊；邊緣兩格的顏色混了綠；角落有一塊 3×3 的碎片。</summary>
    private static (byte[] Mask, uint[] Pixels, int W, int H) Scene()
    {
        const int w = 64, h = 64;
        var mask = new byte[w * h];
        var pixels = new uint[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            // 到方塊邊界的有號距離（內正外負，切比雪夫）
            var d = Math.Min(Math.Min(x - 16, 47 - x), Math.Min(y - 16, 47 - y));
            var soft = Math.Clamp((d + 3) / 6f, 0f, 1f);   // 6 格寬的過渡
            mask[i] = (byte)Math.Round(soft * 255);
            // 顏色：內部純紅；靠邊兩格混綠（模擬背景汙染）；外面純綠
            var mix = d >= 2 ? 0f : d >= 0 ? 0.5f : 1f;
            var r = (int)(255 * (1 - mix));
            var g = (int)(255 * mix);
            pixels[i] = Premul(0, g, r, 255);
        }
        // 碎片
        for (var y = 4; y < 7; y++)
        for (var x = 4; x < 7; x++)
            mask[y * w + x] = 255;
        return (mask, pixels, w, h);
    }

    [Fact]
    public void Apply_LeavesOnlyAOnePixelAntialiasRing_AndDropsIslands()
    {
        var (mask, pixels, w, h) = Scene();
        var (hard, output) = HardEdgeCut.Apply(mask, pixels, w, h);

        var partial = 0;
        for (var i = 0; i < hard.Length; i++)
            if (hard[i] is > 0 and < 255) partial++;
        // 32×32 方塊的周長 128 格，抗鋸齒過渡最多一格寬（角落多一點）
        Assert.True(partial <= 128 * 1.5, $"半透明像素應只剩邊緣一圈，實際 {partial} 個");

        Assert.Equal(0, hard[5 * w + 5]);          // 碎片丟掉
        Assert.Equal(255, hard[32 * w + 32]);      // 中心實心
        Assert.Equal(0, hard[10 * w + 32]);        // 外面透明
        Assert.Equal(0u, output[10 * w + 32]);
    }

    [Fact]
    public void Apply_ReplacesContaminatedEdgeColorsWithInteriorColor()
    {
        var (mask, pixels, w, h) = Scene();
        var (hard, output) = HardEdgeCut.Apply(mask, pixels, w, h);

        // 原本邊緣兩格是紅綠各半；切完之後只要還留著的像素，顏色都該接近內部的紅
        for (var x = 16; x < 48; x++)
        {
            var i = 17 * w + x;   // 邊緣往內第二格
            if (hard[i] == 0) continue;
            Unpremul(output[i], out _, out var g, out var r, out _);
            Assert.True(r > 200 && g < 40, $"({x},17) 應為紅色，實際 R={r} G={g}");
        }
        Assert.Equal(Premul(0, 0, 255, 255), output[32 * w + 32]);   // 內部不動
    }

    [Fact]
    public void Apply_WithoutAntialias_IsPurelyBinary()
    {
        var (mask, pixels, w, h) = Scene();
        var (hard, _) = HardEdgeCut.Apply(mask, pixels, w, h, antialias: false);
        Assert.All(hard, v => Assert.True(v is 0 or 255));
    }

    [Fact]
    public void BackgroundRemoval_WithHardEdge_HasNoSoftFringe()
    {
        // 白底紅圓，本機演算去背；硬邊模式下輸出除了一圈抗鋸齒外沒有半透明像素
        const int side = 256;
        var doc = ImageCodec.CreateBlankDocument(side, side, SKColors.White);
        using var session = new EditorSession(doc);
        var layer = (RasterLayer)doc.ActiveLayer!;
        lock (doc.SyncRoot)
        {
            using var bmp = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };
            canvas.DrawCircle(side / 2f, side / 2f, side * 0.3f, paint);
            canvas.Flush();
            using var pixmap = bmp.PeekPixels();
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
        }

        Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions { HardEdge = true }));

        var pixels = BackgroundRemovalCommand.ReadRegion(layer.Surface, new SKRectI(0, 0, side, side));
        var partial = 0;
        var opaque = 0;
        foreach (var p in pixels)
        {
            var a = A(p);
            if (a is > 0 and < 255) partial++;
            if (a == 255) opaque++;
        }
        var circumference = 2 * Math.PI * side * 0.3;
        Assert.True(partial <= circumference * 2, $"半透明像素 {partial} 個，超過圓周 {circumference:0} 的兩倍");
        Assert.True(opaque > Math.PI * side * 0.3 * side * 0.3 * 0.8, "圓的內部應該幾乎全是實心");
    }
}
