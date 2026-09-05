using MinePainter.Core.IO;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 剪貼簿 DIB 解碼。32 bpp 的透明像素在 Skia 的 BMP 解碼器會變成不透明黑
/// （使用者 2026-09-06：從 Windows 相簿複製 PNG 進來背景變黑），所以 alpha 要自己讀。
/// </summary>
public class DibCodecTests
{
    /// <summary>BITMAPINFOHEADER（40 位元組）+ 32 bpp 像素（由下而上）。</summary>
    private static byte[] Dib32(int width, int height, uint[] bottomUpPixels, bool v5 = false, bool bitfields = false)
    {
        var headerSize = v5 ? 124 : 40;
        var dib = new byte[headerSize + (bitfields && !v5 ? 12 : 0) + width * height * 4];
        BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        BitConverter.GetBytes(bitfields ? 3 : 0).CopyTo(dib, 16);
        var pixelOffset = headerSize;
        if (bitfields)
        {
            var maskOffset = v5 ? 40 : headerSize;
            BitConverter.GetBytes(0x00FF0000u).CopyTo(dib, maskOffset);
            BitConverter.GetBytes(0x0000FF00u).CopyTo(dib, maskOffset + 4);
            BitConverter.GetBytes(0x000000FFu).CopyTo(dib, maskOffset + 8);
            if (v5) BitConverter.GetBytes(0xFF000000u).CopyTo(dib, 52);
            else pixelOffset += 12;
        }
        for (var i = 0; i < bottomUpPixels.Length; i++)
            BitConverter.GetBytes(bottomUpPixels[i]).CopyTo(dib, pixelOffset + i * 4);
        return dib;
    }

    private static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bmp = SKBitmap.FromImage(image);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void Decode_KeepsTransparencyOf32bppDib()
    {
        // 2×1：左邊全透明、右邊不透明紅（DIBV5 + BI_BITFIELDS，Windows 相簿的寫法）
        var dib = Dib32(2, 1, [0x00000000u, 0xFFFF0000u], v5: true, bitfields: true);

        using var image = DibCodec.Decode(dib);
        Assert.NotNull(image);
        Assert.Equal(0, Pixel(image, 0, 0).Alpha);
        Assert.Equal(new SKColor(255, 0, 0, 255), Pixel(image, 1, 0));
    }

    [Fact]
    public void Decode_TreatsAllZeroAlphaAsOpaque()
    {
        // 舊程式放的 XRGB：alpha 全 0 不代表透明
        var dib = Dib32(2, 1, [0x00112233u, 0x00FFFFFFu]);

        using var image = DibCodec.Decode(dib);
        Assert.NotNull(image);
        Assert.Equal(new SKColor(0x11, 0x22, 0x33, 255), Pixel(image, 0, 0));
        Assert.Equal(SKColors.White, Pixel(image, 1, 0));
    }

    [Fact]
    public void Decode_PremultipliesStraightAlpha()
    {
        // 半透明白（直通 alpha：通道 255 > alpha 128）→ 預乘後通道應接近 128
        var dib = Dib32(1, 1, [0x80FFFFFFu]);

        using var image = DibCodec.Decode(dib);
        Assert.NotNull(image);
        using var bmp = SKBitmap.FromImage(image);
        var span = bmp.GetPixelSpan();
        Assert.Equal(0x80, span[3]);
        Assert.InRange(span[0], 127, 129);
    }

    [Fact]
    public void Decode_FlipsBottomUpRows()
    {
        // 1×2：檔案裡第一列是圖的「最下面」
        var dib = Dib32(1, 2, [0xFF0000FFu, 0xFFFF0000u]);

        using var image = DibCodec.Decode(dib);
        Assert.NotNull(image);
        Assert.Equal(new SKColor(255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal(new SKColor(0, 0, 255, 255), Pixel(image, 0, 1));
    }

    [Fact]
    public void Decode_FallsBackToSkiaForOtherDepths()
    {
        // 24 bpp 走補檔頭交給 Skia 的路
        var width = 2;
        var stride = 8; // 2×3 = 6 → 補到 4 的倍數
        var dib = new byte[40 + stride];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)24).CopyTo(dib, 14);
        dib[40] = 0; dib[41] = 0; dib[42] = 255;   // 紅（BGR）
        dib[43] = 0; dib[44] = 255; dib[45] = 0;   // 綠

        using var image = DibCodec.Decode(dib);
        Assert.NotNull(image);
        Assert.Equal(new SKColor(255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal(new SKColor(0, 255, 0, 255), Pixel(image, 1, 0));
    }
}
