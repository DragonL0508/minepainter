using MinePainter.Core.Adjustments;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class LayerThumbnailTests
{
    private const int BoxW = 48;
    private const int BoxH = 38;

    private static SKBitmap RenderThumb(Document doc, LayerNode node, int w = BoxW, int h = BoxH)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        LayerThumbnailRenderer.Draw(canvas, doc, node, w, h);
        canvas.Flush();
        return bitmap;
    }

    private static RasterLayer SolidLayer(string name, SKRectI rect, SKColor color)
    {
        var layer = new RasterLayer { Name = name };
        layer.Surface.Fill(rect, color);
        return layer;
    }

    [Fact]
    public void FitRect_KeepsAspectRatioAndCenters()
    {
        // 寬扁文件放進 48×38 的框：寬撐滿、上下留白且對稱
        using var doc = ImageCodec.CreateBlankDocument(400, 100, SKColors.White);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);

        Assert.Equal(BoxW, rect.Width, 3);
        Assert.Equal(BoxW / 4f, rect.Height, 3); // 400:100 → 48:12
        Assert.Equal(rect.Top, BoxH - rect.Bottom, 3);
        Assert.Equal(0, rect.Left, 3);
    }

    [Fact]
    public void FitRect_TallDocumentLeavesSideMargins()
    {
        using var doc = ImageCodec.CreateBlankDocument(100, 400, SKColors.White);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);

        Assert.Equal(BoxH, rect.Height, 3);
        Assert.Equal(BoxH / 4f, rect.Width, 3);
        Assert.Equal(rect.Left, BoxW - rect.Right, 3);
    }

    [Fact]
    public void Draw_ShowsLayerPixelsAtCorrectPosition()
    {
        // 左半紅、右半留空的圖層：縮圖左半應為紅、右半應看到棋盤格（非紅）
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var layer = SolidLayer("紅", new SKRectI(0, 0, 128, 256), SKColors.Red);
        doc.Root.Add(layer);

        using var bmp = RenderThumb(doc, layer);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);

        var left = bmp.GetPixel((int)(rect.Left + rect.Width * 0.25f), BoxH / 2);
        var right = bmp.GetPixel((int)(rect.Left + rect.Width * 0.75f), BoxH / 2);

        Assert.True(left.Red > 200 && left.Green < 60, $"左半應為紅，實得 {left}");
        Assert.True(right.Red > 150 && right.Green > 150 && right.Blue > 150, $"右半應為棋盤格，實得 {right}");
    }

    [Fact]
    public void Draw_IgnoresLayerOpacityAndVisibility()
    {
        // 縮圖表示「這層上有什麼」——半透明或隱藏的圖層照樣畫成不透明
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var layer = SolidLayer("紅", new SKRectI(0, 0, 256, 256), SKColors.Red);
        layer.Opacity = 0.1f;
        layer.IsVisible = false;
        doc.Root.Add(layer);

        using var bmp = RenderThumb(doc, layer);
        var c = bmp.GetPixel(BoxW / 2, BoxH / 2);

        Assert.True(c.Red > 200 && c.Green < 60, $"應為實紅（不套 opacity/visibility），實得 {c}");
    }

    [Fact]
    public void Draw_GroupCompositesChildren()
    {
        // 群組縮圖 = 子節點疊合：上層藍蓋住下層紅
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var group = new GroupLayer { Name = "群組" };
        doc.Root.Add(group);
        group.Add(SolidLayer("紅", new SKRectI(0, 0, 256, 256), SKColors.Red));
        group.Add(SolidLayer("藍", new SKRectI(0, 0, 256, 256), SKColors.Blue));

        using var bmp = RenderThumb(doc, group);
        var c = bmp.GetPixel(BoxW / 2, BoxH / 2);

        Assert.True(c.Blue > 200 && c.Red < 60, $"應為藍（上層蓋住），實得 {c}");
    }

    [Fact]
    public void Draw_GroupSkipsHiddenChildren()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var group = new GroupLayer { Name = "群組" };
        doc.Root.Add(group);
        group.Add(SolidLayer("紅", new SKRectI(0, 0, 256, 256), SKColors.Red));
        var blue = SolidLayer("藍", new SKRectI(0, 0, 256, 256), SKColors.Blue);
        blue.IsVisible = false;
        group.Add(blue);

        using var bmp = RenderThumb(doc, group);
        var c = bmp.GetPixel(BoxW / 2, BoxH / 2);

        Assert.True(c.Red > 200 && c.Blue < 60, $"隱藏的子層不該入鏡，實得 {c}");
    }

    [Fact]
    public void Draw_RespectsLayerOffset()
    {
        // 內容只在左上 64px，整層往右下平移半張圖 → 縮圖中心該有顏色、左上角該是空的
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var layer = SolidLayer("紅", new SKRectI(0, 0, 64, 64), SKColors.Red);
        layer.Offset = new SKPointI(96, 96);
        doc.Root.Add(layer);

        using var bmp = RenderThumb(doc, layer);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);

        var center = bmp.GetPixel(BoxW / 2, BoxH / 2);
        var topLeft = bmp.GetPixel((int)(rect.Left + 2), (int)(rect.Top + 2));

        Assert.True(center.Red > 200 && center.Green < 60, $"位移後內容應在中央，實得 {center}");
        Assert.True(topLeft.Red > 150 && topLeft.Green > 150, $"左上角應為棋盤格，實得 {topLeft}");
    }

    [Fact]
    public void Draw_AdjustmentLayerRendersPlaceholder()
    {
        // 調整圖層沒有像素，但縮圖不該是空白棋盤格
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var adj = new AdjustmentLayer(new BrightnessContrastAdjustment());
        doc.Root.Add(adj);

        using var bmp = RenderThumb(doc, adj);

        // 示意圖是「左半實心的圓」；正中央落在弦線上，取左半內部取樣
        var filled = bmp.GetPixel(BoxW / 2 - 5, BoxH / 2);
        Assert.True(filled.Red < 0x80 && filled.Green < 0x80 && filled.Blue < 0x80,
            $"應畫出深色示意圖，實得 {filled}");
    }

    [Fact]
    public void Draw_EmptyLayerIsAllChecker()
    {
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var layer = new RasterLayer { Name = "空" };
        doc.Root.Add(layer);

        using var bmp = RenderThumb(doc, layer);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);

        // 棋盤格只有白與淺灰
        for (var y = (int)rect.Top + 2; y < rect.Bottom - 2; y++)
        {
            for (var x = (int)rect.Left + 2; x < rect.Right - 2; x++)
            {
                var c = bmp.GetPixel(x, y);
                Assert.True(c.Red > 0xC0 && c.Green > 0xC0 && c.Blue > 0xC0,
                    $"空圖層應只有棋盤格，({x},{y}) 實得 {c}");
            }
        }
    }

    [Fact]
    public void Draw_ClipsContentOutsideDocument()
    {
        // 圖層可以持有畫布外的像素（配合平移），縮圖不該畫到文件框外
        using var doc = ImageCodec.CreateBlankDocument(256, 256, SKColors.Transparent);
        var layer = SolidLayer("紅", new SKRectI(0, 0, 256, 256), SKColors.Red);
        layer.Offset = new SKPointI(-256, 0); // 整層移到畫布左外側
        doc.Root.Add(layer);

        using var bmp = RenderThumb(doc, layer);
        var rect = LayerThumbnailRenderer.FitRect(doc, BoxW, BoxH);
        var c = bmp.GetPixel((int)(rect.Left + rect.Width / 2), BoxH / 2);

        Assert.True(c.Red > 150 && c.Green > 150, $"畫布外的內容不該出現，實得 {c}");
    }

    [Fact]
    public void Draw_IsFastEnoughForPerRefreshUse()
    {
        // 每次 history 變更都會重畫所有列的縮圖 —— 單張必須遠低於一幀
        using var doc = ImageCodec.CreateBlankDocument(1600, 1200, SKColors.White);
        var layer = SolidLayer("大", new SKRectI(0, 0, 1600, 1200), SKColors.Red);
        doc.Root.Add(layer);

        RenderThumb(doc, layer).Dispose(); // 暖機

        // 取中位數而不是平均：這台機器同時在跑別的測試／建置時，偶爾一次被排程搶走
        // 就會把平均拉高，害這條穩定的效能護欄變成隨機失敗。
        const int iterations = 51;
        var samples = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RenderThumb(doc, layer).Dispose();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        var perRender = samples[iterations / 2];
        Assert.True(perRender < 5, $"單張縮圖中位數 {perRender:0.##} ms，太慢（1600×1200 全滿圖層）");
    }
}
