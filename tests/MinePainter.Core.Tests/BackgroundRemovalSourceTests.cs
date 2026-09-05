using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 快速模式（或變形後）的圖層帶著「原始高清來源」：AI 去背要把遮罩一起套到原圖上，
/// 輸出大圖時才是從去背後的原圖重畫，而不是拿代理解析度的結果放大。
/// </summary>
public class BackgroundRemovalSourceTests
{
    private static SKBitmap Disc(int side, SKColor fill, SKColor background)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawCircle(side / 2f, side / 2f, side * 0.3f, paint);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>代理畫布 proxySide、原圖 originalSide 的圖層（像素縮小蓋上、來源指向原圖）。</summary>
    private static (EditorSession Session, RasterLayer Layer, SKBitmap Original) ProxyLayer(
        int originalSide, int proxySide, SKColor fill, SKColor background)
    {
        var original = Disc(originalSide, fill, background);
        var session = new EditorSession(ImageCodec.CreateBlankDocument(proxySide, proxySide, SKColors.Transparent));
        var doc = session.Document;
        var layer = Assert.IsType<RasterLayer>(doc.ActiveLayer);
        lock (doc.SyncRoot)
        {
            using var small = original.Resize(new SKImageInfo(proxySide, proxySide,
                SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
            using var pixmap = small.PeekPixels();
            layer.Surface.CopyFrom(pixmap, SKPointI.Empty);

            var scale = proxySide / (float)originalSide;
            layer.SetPixelSource(new LayerPixelSource(
                SKImage.FromBitmap(original),
                new SKRectI(0, 0, originalSide, originalSide),
                SKMatrix.CreateScale(scale, scale),
                SKPointI.Empty,
                SKRect.Create(0, 0, proxySide, proxySide),
                0f,
                new SKSize(originalSide, originalSide),
                layer.Surface.Revision));
        }
        doc.SetOutputSize(originalSide, originalSide);
        return (session, layer, original);
    }

    private static SKColor SourcePixel(LayerPixelSource source, int x, int y)
    {
        using var bitmap = SKBitmap.FromImage(source.Pixels);
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public void 遮罩依來源矩陣套到原圖上()
    {
        const int original = 512, proxy = 128;
        var (session, layer, bitmap) = ProxyLayer(original, proxy, SKColors.Red, SKColors.White);
        using (session)
        using (bitmap)
        {
            // 圖層座標的遮罩：左半邊留、右半邊去
            var mask = new byte[proxy * proxy];
            for (var y = 0; y < proxy; y++)
            for (var x = 0; x < proxy / 2; x++)
                mask[y * proxy + x] = 255;

            using var masked = layer.ValidPixelSource!.Masked(
                new SKRectI(0, 0, proxy, proxy), mask);

            Assert.Equal(new SKRectI(0, 0, original, original), masked.Bounds);
            Assert.Equal(255, SourcePixel(masked, 100, 256).Alpha); // 左半邊：原圖不動
            Assert.Equal(0, SourcePixel(masked, 400, 256).Alpha);   // 右半邊：去掉
            // 邊界（x = 256）附近一格內過渡，再過去就是全透明
            Assert.Equal(0, SourcePixel(masked, 262, 256).Alpha);
            Assert.Equal(255, SourcePixel(masked, 250, 256).Alpha);
        }
    }

    [Fact]
    public void 去背之後_原始高清來源也去了背_輸出大圖從它重畫()
    {
        const int original = 512, proxy = 128;
        var (session, layer, bitmap) = ProxyLayer(original, proxy, SKColors.Red, SKColors.White);
        using (session)
        using (bitmap)
        {
            var doc = session.Document;
            var before = layer.ValidPixelSource;
            Assert.NotNull(before);

            // 本機演算：白底紅圓，圓留、底去
            Assert.True(BackgroundRemovalCommand.Run(session, layer, new BackgroundRemovalOptions()));

            var after = layer.ValidPixelSource;
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.Equal(255, SourcePixel(after, original / 2, original / 2).Alpha); // 圓心留著
            Assert.Equal(0, SourcePixel(after, 8, 8).Alpha);                          // 角落的白底去掉

            // 輸出 4 倍：圓的邊緣要銳利（從原圖重畫），不是 128 放大來的軟邊
            using var output = OutputRender.Render(doc);
            using var pixels = SKBitmap.FromImage(output);
            Assert.Equal(original, output.Width);
            var edge = 0;
            var r = original * 0.3f;
            for (var x = 0; x < original; x++)
            {
                var a = pixels.GetPixel(x, original / 2).Alpha;
                if (a is > 20 and < 235) edge++;
            }
            Assert.True(edge <= 8, $"圓的水平切線上有 {edge} 個半透明像素 —— 邊緣是放大來的（半徑 {r}）");

            // undo：舊來源接回去、還有效
            session.History.Undo();
            Assert.Same(before, layer.ValidPixelSource);
            Assert.Equal(255, SourcePixel(before, 8, 8).Alpha);

            session.History.Redo();
            Assert.Same(after, layer.ValidPixelSource);
        }
    }
}
