using MinePainter.Core.IO;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 「顏色不準」的兩個實際來源：帶 ICC 的影像進來沒轉 sRGB（P3 截圖整張偏淡）、
/// JPEG 匯出用 4:2:0 色度次取樣（紅字邊緣糊成粉紅）。
/// </summary>
public class ColorAccuracyTests
{
    /// <summary>
    /// 用 Skia 自己寫一張帶 Display P3 ICC 的 PNG。像素直接寫原始位元組 ——
    /// Erase 會把（sRGB 的）顏色先轉進 P3 再存，解碼回 sRGB 又轉回來，剛好抵銷、測不到東西。
    /// </summary>
    private static unsafe byte[] P3Png(SKColor color)
    {
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul, p3);
        using var bmp = new SKBitmap(info);
        var raw = (uint)color; // ARGB → BGRA 記憶體排列剛好就是 uint 的位元組序（little-endian）
        new Span<uint>((void*)bmp.GetPixels(), 64).Fill(raw);
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions());
        return data.ToArray();
    }

    [Fact]
    public void 帶P3設定檔的PNG會轉成sRGB()
    {
        // P3 的 (200,100,50) 在 sRGB 裡更飽和：紅更高、綠藍更低。沒轉的話讀到的就是原數值。
        var source = new SKColor(200, 100, 50);
        var png = P3Png(source);
        using var loaded = ImageCodec.LoadBitmap(new MemoryStream(png));
        var c = loaded.GetPixel(4, 4);
        Assert.True(c.Red > source.Red + 5 && c.Green < source.Green - 3,
            $"讀到 {c}，與 P3 原數值 {source} 幾乎一樣：解碼沒有轉成 sRGB，P3 的圖會整張偏淡");
    }

    [Fact]
    public void 沒有設定檔的PNG數值不變()
    {
        var info = new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        bmp.Erase(new SKColor(200, 100, 50));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var loaded = ImageCodec.LoadBitmap(new MemoryStream(data.ToArray()));
        Assert.Equal(new SKColor(200, 100, 50), loaded.GetPixel(4, 4));
    }

    [Fact]
    public void JPEG匯出不做色度次取樣_細紅線不糊()
    {
        using var doc = ImageCodec.CreateBlankDocument(64, 64, SKColors.White);
        var layer = (Layers.RasterLayer)doc.ActiveLayer!;
        for (var x = 8; x < 64; x += 8)
            layer.Surface.Fill(new SKRectI(x, 0, x + 1, 64), SKColors.Red); // 每 8 px 一條 1 px 紅線

        var path = Path.Combine(Path.GetTempPath(), $"mp-jpeg-{Guid.NewGuid():N}.jpg");
        try
        {
            MppFormat.Export(doc, path, jpegQuality: 92);
            using var back = ImageCodec.LoadBitmap(path);
            var line = back.GetPixel(16, 32);
            var beside = back.GetPixel(18, 32);
            // 4:2:0 會把 1 px 紅線的色度攤到 2 px：線本身變粉紅（G/B 明顯上升）、旁邊的白也被染紅
            Assert.True(line.Green < 60 && line.Blue < 60,
                $"紅線變成 {line}：色度被次取樣攤平了（4:2:0）");
            Assert.True(beside.Green > 235 && beside.Blue > 235,
                $"線旁的白變成 {beside}：紅色滲到旁邊的像素");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
