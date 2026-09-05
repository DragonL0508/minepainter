using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 快速模式的去背在「原始高清來源」的解析度上做：遮罩用原圖那一塊算、直接乘到原圖，再縮回代理畫布。
/// 之前是代理解析度的遮罩放大套到原圖，邊緣一放大就糊（使用者 2026-09-06 回報）。
/// </summary>
public class BackgroundRemovalHiResTests
{
    private static SKBitmap Square(int side, SKColor fill, SKColor background)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paint = new SKPaint { Color = fill, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(side * 0.25f, side * 0.25f, side * 0.5f, side * 0.5f), paint);
        canvas.Flush();
        return bitmap;
    }

    private static (EditorSession Session, RasterLayer Layer, SKBitmap Original) ProxyLayer(int originalSide, int proxySide)
    {
        var original = Square(originalSide, SKColors.Red, SKColors.White);
        var session = new EditorSession(ImageCodec.CreateBlankDocument(proxySide, proxySide, SKColors.Transparent));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot)
        {
            using var small = original.Resize(new SKImageInfo(proxySide, proxySide, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            using var pixmap = small.PeekPixels();
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);
            var scale = proxySide / (float)originalSide;
            layer.SetPixelSource(new LayerPixelSource(SKImage.FromBitmap(original), new SKRectI(0, 0, originalSide, originalSide),
                SKMatrix.CreateScale(scale, scale), SKPointI.Empty, SKRect.Create(0, 0, proxySide, proxySide), 0f,
                new SKSize(originalSide, originalSide), layer.Surface.Revision));
        }
        doc.SetOutputSize(originalSide, originalSide);
        return (session, layer, original);
    }

    [Fact]
    public void SourceRegion_And_MaskResampling_RoundTripThroughTheMatrix()
    {
        var (session, layer, bitmap) = ProxyLayer(400, 100);
        using (session)
        using (bitmap)
        {
            var source = layer.ValidPixelSource!;
            Assert.Equal(4f, source.SourcePixelsPerLayerPixel, 2);
            Assert.Equal(new SKRectI(40, 80, 240, 400), source.SourceRegionFor(new SKRectI(10, 20, 60, 100)));

            // 圖層座標的遮罩（左半 255）→ 來源座標 → 回圖層座標，內容不變
            var region = new SKRectI(0, 0, 400, 400);
            var crop = new SKRectI(0, 0, 100, 100);
            var layerMask = new byte[100 * 100];
            for (var y = 0; y < 100; y++)
                for (var x = 0; x < 50; x++) layerMask[y * 100 + x] = 255;

            var sourceMask = source.ResampleMaskToSource(layerMask, crop, region);
            Assert.Equal(255, sourceMask[200 * 400 + 100]);
            Assert.Equal(0, sourceMask[200 * 400 + 300]);

            var back = source.ResampleMaskToLayer(sourceMask, region, crop);
            Assert.Equal(255, back[50 * 100 + 20]);
            Assert.Equal(0, back[50 * 100 + 80]);
        }
    }

    [Fact]
    public void MaskedInSourceSpace_MultipliesAlphaAndClearsOutsideRegion()
    {
        var (session, layer, bitmap) = ProxyLayer(200, 50);
        using (session)
        using (bitmap)
        {
            var region = new SKRectI(50, 50, 150, 150);
            var mask = new byte[100 * 100];
            Array.Fill(mask, (byte)128);
            using var masked = layer.ValidPixelSource!.MaskedInSourceSpace(region, mask);
            using var pixels = SKBitmap.FromImage(masked.Pixels);
            Assert.Equal(128, pixels.GetPixel(100, 100).Alpha);
            Assert.Equal(0, pixels.GetPixel(10, 10).Alpha);
        }
    }

    [Fact]
    public void FastMode_RemovalComputesMaskAtSourceResolution()
    {
        const int original = 512, proxy = 128;
        var (session, layer, bitmap) = ProxyLayer(original, proxy);
        using (session)
        using (bitmap)
        {
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));

            var after = layer.ValidPixelSource;
            Assert.NotNull(after);
            using var pixels = SKBitmap.FromImage(after.Pixels);
            Assert.Equal(255, pixels.GetPixel(original / 2, original / 2).Alpha);
            Assert.Equal(0, pixels.GetPixel(8, 8).Alpha);

            // 方塊左緣在 x=128：來源解析度算的遮罩在 1～2 個原始像素內完成過渡；
            // 代理解析度放大來的至少要 4 個以上（一個代理像素＝4 個原始像素）
            var transition = 0;
            for (var x = 100; x < 160; x++)
            {
                var a = pixels.GetPixel(x, original / 2).Alpha;
                if (a is > 10 and < 245) transition++;
            }
            Assert.True(transition <= 3, $"左緣有 {transition} 個半透明的原始像素 —— 遮罩不是在來源解析度算的");

            // 代理畫布同一份遮罩縮回來：中心留、角落去
            lock (session.Document.SyncRoot)
            {
                var center = BackgroundRemovalCommand.ReadRegion(layer.Surface, new SKRectI(proxy / 2, proxy / 2, proxy / 2 + 1, proxy / 2 + 1))[0];
                var corner = BackgroundRemovalCommand.ReadRegion(layer.Surface, new SKRectI(2, 2, 3, 3))[0];
                Assert.Equal(255u, center >> 24);
                Assert.Equal(0u, corner >> 24);
            }
        }
    }
}
