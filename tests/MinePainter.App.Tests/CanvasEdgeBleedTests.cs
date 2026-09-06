using MinePainter.App.Rendering;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 使用者 2026-09-06 回報：「畫布縮小時，底邊、右邊會出現一個非常細微的破圖（白線）」。
///
/// 成因：文件尺寸不是 256 的倍數時，最右／最下那格貼圖裡「畫布外」的區域是透明的；
/// 縮小檢視走雙線性取樣，畫布最後一排像素會混到旁邊的透明，底下的白色棋盤格就透出來。
/// 左上邊剛好是貼圖的邊緣，Skia 對邊緣是夾住（clamp）取樣，所以沒事 —— 這正是只有右下有線的原因。
/// 修法：貼圖時把來源範圍限制在畫布內（strict src rect），邊緣像素被夾住、不再混到透明。
/// </summary>
public class CanvasEdgeBleedTests
{
    private const int Size = 300; // 不是 256 的倍數：右／下邊落在貼圖中間

    [Theory]
    [InlineData(0.37, 3.7f)]   // LOD 第 1 階
    [InlineData(0.21, 3.7f)]   // LOD 第 2 階
    [InlineData(0.73, 3.7f)]   // 逐格
    [InlineData(0.37, 3.3f)]
    [InlineData(0.37, 3.9f)]
    [InlineData(0.5, 3.6f)]
    public void 縮小檢視_畫布右下邊不透出底色(double scale, float offset)
    {
        var doc = ImageCodec.CreateBlankDocument(Size, Size, SKColors.White);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, Size, Size), new SKColor(0x20, 0x20, 0x20));
        using var session = new EditorSession(doc);

        var viewW = (int)Math.Ceiling(Size * scale) + 8;
        var info = new SKImageInfo(viewW, viewW, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White); // 模擬底下的棋盤格（白）
        // 畫布邊落在非整數像素上：與 Fit 置中後的常態一樣
        canvas.Translate(offset, offset);
        canvas.Scale((float)scale);
        canvas.ClipRect(SKRect.Create(0, 0, Size, Size));
        using (var renderer = new GpuLayerRenderer())
        {
            lock (doc.SyncRoot)
                Assert.True(renderer.TryDraw(canvas, session, new SKRectI(0, 0, Size, Size), scale));
        }
        canvas.Flush();

        using var image = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(image);
        // 畫布在螢幕上的最後一欄／一列：裁切（非反鋸齒）以像素中心判定，中心在畫布邊界內的都算
        var edge = offset + Size * scale;
        var lastX = (int)Math.Ceiling(edge - 0.5) - 1;
        var lastY = lastX;
        var firstX = (int)Math.Ceiling(offset - 0.5);
        var worst = 0;
        var dump = string.Join(" ", Enumerable.Range(lastX - 3, 6).Select(x => bmp.GetPixel(x, lastY - 10).Red.ToString("X2")));
        for (var y = firstX; y <= lastY; y++) worst = Math.Max(worst, bmp.GetPixel(lastX, y).Red);
        for (var x = firstX; x <= lastX; x++) worst = Math.Max(worst, bmp.GetPixel(x, lastY).Red);
        Assert.True(worst <= 0x28, $"畫布右／下邊最亮到 {worst:X2}（內容是 20；邊界 {edge:F2}，最後一欄附近 {dump}）：邊緣混到透明、底色透出來了");
    }
}
