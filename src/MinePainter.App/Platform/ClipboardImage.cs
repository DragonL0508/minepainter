using System.Runtime.InteropServices;
using SkiaSharp;

namespace MinePainter.App.Platform;

/// <summary>
/// 剪貼簿影像進出（Windows 專用；其他平台一律回報失敗）。
/// Avalonia 11 的 IClipboard 沒有一等影像支援，直接走 Win32：
/// - 讀：優先 "PNG"（保留 alpha；Chrome/GIMP/paint.net 都會放），
///   退而求其次 CF_DIBV5 → CF_DIB（螢幕截圖只有這個），用「補 BITMAPFILEHEADER
///   變成 BMP 檔」的老把戲交給 Skia 解碼。
/// - 寫：同時放 "PNG"（含 alpha）與 CF_DIB（鋪白底 —— DIB 沒有 alpha 語意，
///   不鋪底的話透明區在只認 DIB 的程式裡會變黑）。
/// 只在 UI 執行緒呼叫（Win32 剪貼簿假設 STA）。
/// </summary>
internal static class ClipboardImage
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static readonly uint PngFormat =
        OperatingSystem.IsWindows() ? RegisterClipboardFormatW("PNG") : 0;

    /// <summary>把影像放上剪貼簿。回傳是否成功。</summary>
    public static bool TrySetImage(SKImage image)
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var dib = BuildDib(image);

        if (!TryOpen()) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var ok = SetData(PngFormat, png.ToArray());
            ok |= SetData(CF_DIB, dib); // 兩個格式擇一成功即可
            return ok;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>從剪貼簿取影像（BGRA premul）。沒有影像或失敗時回傳 null。</summary>
    public static SKImage? TryGetImage()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!TryOpen()) return null;

        byte[]? fileBytes;
        try
        {
            // PNG 優先（有 alpha）；DIB 補上 BITMAPFILEHEADER 就是 BMP 檔
            fileBytes = GetBytes(PngFormat);
            if (fileBytes == null && (GetBytes(CF_DIBV5) ?? GetBytes(CF_DIB)) is { } dib)
                fileBytes = DibToBmpFile(dib);
        }
        finally
        {
            CloseClipboard();
        }
        if (fileBytes == null) return null;

        using var decoded = SKBitmap.Decode(fileBytes);
        if (decoded == null) return null;

        // 統一成 BGRA premul（Skia 解出來的格式依來源而異）
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawBitmap(decoded, 0, 0);
        return surface.Snapshot();
    }

    // ---- DIB ⇄ BMP ----

    /// <summary>32bpp BI_RGB、由下而上、鋪白底的 CF_DIB 資料。</summary>
    private static byte[] BuildDib(SKImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White); // alpha 落地成白底（premul + 不透明 = 直通值）
            canvas.DrawImage(image, 0, 0);
        }

        var stride = w * 4;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);          // biSize
        BitConverter.GetBytes(w).CopyTo(dib, 4);           // biWidth
        BitConverter.GetBytes(h).CopyTo(dib, 8);           // biHeight（正值 = 由下而上）
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);  // biPlanes
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14); // biBitCount
        BitConverter.GetBytes(0).CopyTo(dib, 16);          // biCompression = BI_RGB
        BitConverter.GetBytes(stride * h).CopyTo(dib, 20); // biSizeImage

        var pixels = bmp.GetPixels();
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(pixels + y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        }
        return dib;
    }

    /// <summary>CF_DIB(V5) 資料前面補 14 位元組的 BITMAPFILEHEADER，變成 Skia 可解的 BMP 檔。</summary>
    private static byte[]? DibToBmpFile(byte[] dib)
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

    // ---- Win32 ----

    /// <summary>剪貼簿可能被別的程式短暫鎖住，重試幾次。</summary>
    private static bool TryOpen()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private static byte[]? GetBytes(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return null;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool SetData(uint format, byte[] bytes)
    {
        if (format == 0) return false;
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return false;

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        GlobalUnlock(handle);

        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle); // 只有失敗時才歸我們釋放；成功後擁有權在系統
            return false;
        }
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
