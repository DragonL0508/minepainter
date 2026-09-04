using MinePainter.App.Rendering;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 縮小檢視的 LOD 金字塔（見 <see cref="GpuLayerRenderer.LodLevelFor"/>）。
///
/// 這裡守兩件事：**選階不能選錯**（選太粗畫面就糊了、選太細等於沒省到），
/// 以及**來源一改貼圖就要重建** —— 貼圖快取的老毛病一律是「內容變了、版本沒變」，
/// 畫面因此停在上一份（逐格那條路踩過，見 Tile.Version 的註解）。
/// </summary>
public class GpuLodTests
{
    [Theory]
    [InlineData(4.0, 0)]
    [InlineData(1.0, 0)]
    [InlineData(0.51, 0)]
    [InlineData(0.5, 1)]     // 50%：一張貼圖 2×2 格
    [InlineData(0.3, 1)]
    [InlineData(0.25, 2)]    // 25%：4×4 格
    [InlineData(0.2, 2)]
    [InlineData(0.125, 3)]   // 12.5%：8×8 格
    [InlineData(0.02, 3)]    // 再縮下去也不會超過最高階
    [InlineData(0.0, 0)]     // 算不出縮放比就照舊逐格畫
    [InlineData(double.NaN, 0)]
    public void 依縮放比選階(double scale, int expected)
        => Assert.Equal(expected, GpuLayerRenderer.LodLevelFor(scale));

    [Fact]
    public void 來源改了版本就要跟著變()
    {
        using var surface = new TileSurface();
        surface.Fill(new SKRectI(0, 0, 512, 512), SKColors.Red); // 第 1 階的區塊(0,0)＝2×2 格

        var before = GpuLayerRenderer.LodVersion(surface, 1, 0, 0);
        Assert.Equal(before, GpuLayerRenderer.LodVersion(surface, 1, 0, 0)); // 沒動就不該重建

        // 區塊內任何一格被改到都要失效（這裡改的是右下那格）
        surface.Fill(new SKRectI(300, 300, 310, 310), SKColors.Blue);
        var after = GpuLayerRenderer.LodVersion(surface, 1, 0, 0);
        Assert.NotEqual(before, after);

        // 區塊外的格子不算數，不然平移時整排貼圖會白重建
        surface.Fill(new SKRectI(600, 600, 610, 610), SKColors.Green);
        Assert.Equal(after, GpuLayerRenderer.LodVersion(surface, 1, 0, 0));

        // 整格被拿掉也是「內容變了」（缺格算 0，不是跳過）
        surface.RemoveTile(new TileIndex(1, 1));
        Assert.NotEqual(after, GpuLayerRenderer.LodVersion(surface, 1, 0, 0));
    }

    [Fact]
    public void 縮小檢視改貼LOD而且內容要對()
    {
        const int size = 1024; // 4×4 格
        var doc = ImageCodec.CreateBlankDocument(size, size, SKColors.Transparent);
        var layer = (RasterLayer)doc.ActiveLayer!;
        layer.Surface.Fill(new SKRectI(0, 0, size, size), SKColors.Red);
        var session = new EditorSession(doc) { LiveElementRendering = true };

        using var renderer = new GpuLayerRenderer();
        using var target = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));

        SKColor Draw(double scale)
        {
            var canvas = target.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Save();
            canvas.Scale((float)scale);
            lock (session.Document.SyncRoot)
            {
                // 沒有 GPU context：離屏 surface 退回 raster，畫面仍舊要正確
                Assert.True(renderer.TryDraw(canvas, session, new SKRectI(0, 0, size, size), scale));
            }
            canvas.Restore();
            canvas.Flush();
            using var image = target.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return bitmap.GetPixel(128, 128);
        }

        // 25%：整份文件正好是第 2 階的一個區塊，一張貼圖抵 16 格
        Assert.Equal(SKColors.Red, Draw(0.25));
        Assert.Equal(1, renderer.LastLodTiles);
        Assert.Equal(0, renderer.LastTiles);

        // 來源改了要跟著變（版本 hash 失效的端到端版本）
        lock (session.Document.SyncRoot) layer.Surface.Fill(new SKRectI(0, 0, size, size), SKColors.Blue);
        layer.Invalidate(new SKRectI(0, 0, size, size));
        Assert.Equal(SKColors.Blue, Draw(0.25));

        // 100%：照舊逐格畫，一格都不走 LOD
        Assert.Equal(SKColors.Blue, Draw(1.0));
        Assert.Equal(0, renderer.LastLodTiles);
        Assert.Equal(16, renderer.LastTiles);
    }
}
