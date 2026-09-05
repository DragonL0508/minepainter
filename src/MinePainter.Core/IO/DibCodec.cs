using System.Numerics;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Windows 剪貼簿的 CF_DIB／CF_DIBV5（BITMAPINFOHEADER 起頭、沒有檔頭的 BMP）→ SKImage。
///
/// 32 bpp 一定自己解：Skia 的 BMP 解碼器把第四個位元組當「未使用」，透明像素 (0,0,0,0) 就成了不透明的黑
/// —— 從 Windows 相簿複製 PNG 進來背景整片黑（使用者 2026-09-06 回報）。alpha 的語意 DIB 沒有規定：
/// 整張 alpha 都是 0 的是舊程式放的 XRGB，當不透明；有任一通道大於 alpha 的是直通 alpha，要先預乘；
/// 其餘當已預乘（WIC／GDI 的慣例）。其他位深補上 BITMAPFILEHEADER 交給 Skia。
/// </summary>
public static class DibCodec
{
    public static SKImage? Decode(byte[] dib)
    {
        if (dib.Length < 40) return null;
        return Decode32(dib) ?? DecodeViaBmp(dib);
    }

    private static unsafe SKImage? Decode32(byte[] dib)
    {
        var biSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var height = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        if (bitCount != 32 || compression is not (0 or 3) || biSize < 40 || biSize > dib.Length) return null;

        uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = 0xFF000000;
        var pixelOffset = biSize;
        if (compression == 3)
        {
            if (biSize >= 56)
            {
                rMask = BitConverter.ToUInt32(dib, 40);
                gMask = BitConverter.ToUInt32(dib, 44);
                bMask = BitConverter.ToUInt32(dib, 48);
                aMask = BitConverter.ToUInt32(dib, 52);
            }
            else
            {
                if (dib.Length < biSize + 12) return null;
                rMask = BitConverter.ToUInt32(dib, biSize);
                gMask = BitConverter.ToUInt32(dib, biSize + 4);
                bMask = BitConverter.ToUInt32(dib, biSize + 8);
                aMask = ~(rMask | gMask | bMask);
                pixelOffset += 12;
            }
        }
        // 只處理整位元組對齊的 8 位元遮罩（實務上 32 bpp 都是），其他交給 Skia
        if (!IsByteMask(rMask) || !IsByteMask(gMask) || !IsByteMask(bMask) || (aMask != 0 && !IsByteMask(aMask))) return null;

        var topDown = height < 0;
        var rows = Math.Abs(height);
        if (width <= 0 || rows <= 0 || (long)width * rows * 4 > int.MaxValue) return null;
        if (pixelOffset + (long)width * rows * 4 > dib.Length) return null;

        var rShift = BitOperations.TrailingZeroCount(rMask);
        var gShift = BitOperations.TrailingZeroCount(gMask);
        var bShift = BitOperations.TrailingZeroCount(bMask);
        var aShift = aMask == 0 ? 0 : BitOperations.TrailingZeroCount(aMask);

        // 先掃一遍決定 alpha 的語意
        var anyAlpha = false;
        var straight = false;
        fixed (byte* basePtr = dib)
        {
            var src = (uint*)(basePtr + pixelOffset);
            var count = width * rows;
            for (var i = 0; i < count; i++)
            {
                var p = src[i];
                var a = aMask == 0 ? 0u : (p & aMask) >> aShift;
                if (a == 0) continue;
                anyAlpha = true;
                if (((p & rMask) >> rShift) > a || ((p & gMask) >> gShift) > a || ((p & bMask) >> bShift) > a)
                {
                    straight = true;
                    break;
                }
            }

            var bitmap = new SKBitmap(new SKImageInfo(width, rows, SKColorType.Bgra8888, SKAlphaType.Premul));
            try
            {
                var dst = (uint*)bitmap.GetPixels();
                for (var y = 0; y < rows; y++)
                {
                    var srcRow = src + (topDown ? y : rows - 1 - y) * width;
                    var dstRow = dst + y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var p = srcRow[x];
                        var r = (p & rMask) >> rShift;
                        var g = (p & gMask) >> gShift;
                        var b = (p & bMask) >> bShift;
                        var a = anyAlpha ? (p & aMask) >> aShift : 255u;
                        if (straight)
                        {
                            r = (r * a + 127) / 255;
                            g = (g * a + 127) / 255;
                            b = (b * a + 127) / 255;
                        }
                        else if (anyAlpha)
                        {
                            // 已預乘但資料不乾淨時夾住，Skia 對「通道大於 alpha」的預乘像素會畫出怪色
                            r = Math.Min(r, a);
                            g = Math.Min(g, a);
                            b = Math.Min(b, a);
                        }
                        dstRow[x] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
                return SKImage.FromBitmap(bitmap);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
    }

    private static bool IsByteMask(uint mask) =>
        mask != 0 && BitOperations.PopCount(mask) == 8 && (mask >> BitOperations.TrailingZeroCount(mask)) == 0xFF;

    /// <summary>補 14 位元組的 BITMAPFILEHEADER 變成 BMP 檔，交給 Skia 解，再統一成 BGRA premul。</summary>
    private static SKImage? DecodeViaBmp(byte[] dib)
    {
        var bmp = ToBmpFile(dib);
        if (bmp == null) return null;
        using var decoded = SKBitmap.Decode(bmp);
        if (decoded == null) return null;

        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawBitmap(decoded, 0, 0);
        return surface.Snapshot();
    }

    /// <summary>CF_DIB(V5) 資料前面補 14 位元組的 BITMAPFILEHEADER。</summary>
    public static byte[]? ToBmpFile(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var biSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var clrUsed = BitConverter.ToInt32(dib, 32);
        if (biSize < 40 || biSize > dib.Length) return null;

        // 像素起點 = 檔頭 + 資訊頭 + (BI_BITFIELDS 的三個遮罩，V4/V5 已含在頭內) + 調色盤
        var masks = biSize == 40 && compression == 3 ? 12 : 0;
        var palette = clrUsed > 0 ? clrUsed * 4 : bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        var pixelOffset = 14 + biSize + masks + palette;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
