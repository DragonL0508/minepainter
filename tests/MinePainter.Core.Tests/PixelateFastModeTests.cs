using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 2026-09-17 使用者回報：「馬賽克在快速模式看到的，跟實際輸出 4K 的圖片有落差」。
/// 格子大小是像素長度，輸出放大時要跟著放大，格線也要落在畫布的同一個相對位置。
/// </summary>
public class PixelateFastModeTests
{
    private static SKBitmap Noise(int side)
    {
        var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        var rng = new Random(7);
        // 16px 的色塊雜訊：每個馬賽克格子的平均色都不一樣，格線錯位或格子大小不對就比得出來
        for (var y = 0; y < side; y += 16)
        for (var x = 0; x < side; x += 16)
        {
            var c = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            for (var yy = y; yy < Math.Min(side, y + 16); yy++)
            for (var xx = x; xx < Math.Min(side, x + 16); xx++)
                bitmap.SetPixel(xx, yy, c);
        }
        return bitmap;
    }

    [Theory]
    [InlineData(128, 512, 8)]  // 4 倍
    [InlineData(128, 192, 10)] // 1.5 倍：格子變 15px
    public void 輸出的馬賽克_格子跟著放大_看起來跟代理畫布一樣(int proxy, int output, int cell)
    {
        using var session = new EditorSession(ImageCodec.CreateBlankDocument(proxy, proxy, SKColors.White));
        var doc = session.Document;
        doc.SetOutputSize(output, output);
        using var original = Noise(output);
        var layer = ImageCommands.ImportImageLayer(session, original, "圖");
        LayerEffectCommands.Add(doc, session.History, layer, LayerEffect.Create(new PixelateEffect { CellSize = cell }));

        using var proxyImage = Compositor.RenderComposite(doc);
        using var proxyBmp = SKBitmap.FromImage(proxyImage);
        using var outImage = OutputRender.Render(doc);
        using var outBmp = SKBitmap.FromImage(outImage);
        Assert.Equal(output, outBmp.Width);

        var scale = output / (float)proxy;
        var outCell = cell * scale;

        // 1. 輸出裡一格馬賽克是 cell×scale 那麼大：格子內部是同一個顏色
        Assert.Equal(outBmp.GetPixel(1, 1), outBmp.GetPixel((int)outCell - 2, (int)outCell - 2));

        // 2. 每一格的顏色與代理畫布上同一格差不多（取各格中心比）
        double error = 0;
        var n = 0;
        for (var cy = 0; (cy + 1) * cell <= proxy; cy++)
        for (var cx = 0; (cx + 1) * cell <= proxy; cx++)
        {
            var p = proxyBmp.GetPixel(cx * cell + cell / 2, cy * cell + cell / 2);
            var o = outBmp.GetPixel((int)((cx + 0.5f) * outCell), (int)((cy + 0.5f) * outCell));
            error += Math.Abs(p.Red - o.Red) + Math.Abs(p.Green - o.Green) + Math.Abs(p.Blue - o.Blue);
            n += 3;
        }
        Assert.True(error / n < 12, $"輸出與代理畫布的馬賽克對不上（每通道平均差 {error / n:F1}）");
    }
}
