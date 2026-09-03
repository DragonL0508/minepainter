using System.Runtime.InteropServices;
using SkiaSharp;

namespace MinePainter.App.Platform;

/// <summary>
/// 讀取螢幕上游標所在像素的顏色（Win32 GetPixel）。
/// 效果視窗是 modal，畫布收不到點擊，所以吸色走「整個螢幕」——
/// 不管游標停在畫布、圖層面板還是別的視窗，看到什麼顏色就吸什麼顏色。
/// </summary>
internal static class ScreenColorSampler
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>目前游標下的像素；讀不到（非 Windows／螢幕外）回傳 null。</summary>
    public static SKColor? SampleUnderCursor()
    {
        if (!IsSupported) return null;
        if (!GetCursorPos(out var p)) return null;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            var raw = GetPixel(dc, p.X, p.Y);
            if (raw == 0xFFFFFFFF) return null; // CLR_INVALID
            // COLORREF = 0x00BBGGRR
            return new SKColor((byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>滑鼠左鍵目前是否按著（給「點一下進入吸色模式、再點一下取色」用）。</summary>
    public static bool IsLeftButtonDown() =>
        IsSupported && (GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>Esc 目前是否按著（取消吸色模式）。</summary>
    public static bool IsEscapeDown() =>
        IsSupported && (GetAsyncKeyState(0x1B) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
