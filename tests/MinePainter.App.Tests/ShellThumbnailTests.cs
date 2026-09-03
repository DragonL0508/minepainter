using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MinePainter.Core.IO;
using SkiaSharp;
using Xunit;

#pragma warning disable CA1416

[GeneratedComInterface, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
internal partial interface IShellItemImageFactory
{
    void GetImage(SIZE size, int flags, out nint bitmap);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}

/// <summary>
/// .mpp 縮圖走的是真正的外殼管線（檔案總管取縮圖用的同一個 API），所以只有在
/// 這台機器裝過 MinePainter、縮圖處理常式註冊好時才有意義；沒註冊就跳過（CI 上就是這樣）。
/// </summary>
public partial class ShellThumbnailTests
{
    private const int SIIGBF_THUMBNAILONLY = 0x08; // 沒有縮圖處理常式就直接失敗，不會拿圖示來混

    private static bool HandlerRegistered =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\.mpp\ShellEx\{e357fccd-a995-4576-b01f-234630154e96}") is not null;

    [Fact]
    public void ExplorerPipelineProducesThumbnail()
    {
        if (!HandlerRegistered) return;

        var mpp = Path.Combine(Path.GetTempPath(), $"shell-probe-{Guid.NewGuid():N}.mpp");
        MppFormat.Save(ImageCodec.CreateBlankDocument(400, 200, SKColors.CornflowerBlue), mpp);

        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            var hr = SHCreateItemFromParsingName(mpp, 0, in iid, out var ptr);
            Assert.Equal(0, hr);

            var wrappers = new StrategyBasedComWrappers();
            var factory = (IShellItemImageFactory)wrappers.GetOrCreateObjectForComInstance(
                ptr, CreateObjectFlags.None);
            Marshal.Release(ptr);

            factory.GetImage(new SIZE { cx = 256, cy = 256 }, SIIGBF_THUMBNAILONLY, out var hbitmap);
            Assert.NotEqual(0, hbitmap);

            // 400x200 的文件 → 256x128；是圖示的話比例不會是這樣
            var bmp = new BITMAP();
            Assert.NotEqual(0, GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bmp));
            Assert.Equal(256, bmp.bmWidth);
            Assert.Equal(128, bmp.bmHeight);

            DeleteObject(hbitmap);
        }
        finally
        {
            File.Delete(mpp);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindCtx, in Guid iid, out nint item);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int size, ref BITMAP bmp);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);
}
