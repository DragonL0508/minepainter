using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MinePainter.Thumbnails;

/// <summary>
/// 外殼在自己的隔離程序裡初始化縮圖處理常式的方式。一定要實作這個 ——
/// 只實作 IInitializeWithFile 的處理常式必須關掉程序隔離（等於被載進 explorer.exe，
/// 出事會拖垮檔案總管、DLL 也會被鎖住不能更新），實測那條路會回 WTS_E_FAILEDEXTRACTION。
/// </summary>
[GeneratedComInterface, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
internal partial interface IInitializeWithStream
{
    void Initialize(nint stream, uint mode);
}

[GeneratedComInterface, Guid("e357fccd-a995-4576-b01f-234630154e96")]
internal partial interface IThumbnailProvider
{
    void GetThumbnail(uint cx, out nint bitmap, out int alphaType);
}

/// <summary>
/// 檔案總管的 .mpp 縮圖來源。.mpp 是 ZIP 容器，存檔時就已經寫好一張最長邊 256px 的
/// thumbnail.png，這裡只要把那一個 entry 取出來解碼成 HBITMAP —— 不必載入整份文件，
/// 所以資料夾裡有幾百個 .mpp 也不會拖垮檔案總管。
/// </summary>
[GeneratedComClass]
internal sealed partial class MppThumbnailProvider : IInitializeWithStream, IThumbnailProvider
{
    /// <summary>檔案總管在這個 CLSID 底下找我們（登錄檔那邊要寫一樣的）。</summary>
    internal static readonly Guid Clsid = new("f2ac8991-f45f-40c9-aab0-8768c822eec4");

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private Stream? _source;

    public void Initialize(nint stream, uint mode)
    {
        var com = (IStreamCom)Wrappers.GetOrCreateObjectForComInstance(stream, CreateObjectFlags.None);
        _source = new ComStream(com);
    }

    public void GetThumbnail(uint cx, out nint bitmap, out int alphaType)
    {
        alphaType = 1; // WTSAT_RGB：存檔時縮圖已經鋪過白底，沒有透明像素
        if (_source is null) throw new InvalidOperationException("還沒 Initialize。");
        bitmap = Decode(ReadThumbnailEntry(_source));
    }

    private static byte[] ReadThumbnailEntry(Stream file)
    {
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("thumbnail.png")
            ?? throw new FileNotFoundException("這個 .mpp 裡沒有 thumbnail.png。");

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static unsafe nint Decode(byte[] png)
    {
        nint memStream;
        fixed (byte* data = png)
        {
            memStream = Native.SHCreateMemStream(data, (uint)png.Length);
        }
        if (memStream == 0) throw new OutOfMemoryException();

        try
        {
            var factory = (IWICImagingFactory)Native.CreateComInstance(
                Wic.ClsidImagingFactory, Wic.IidImagingFactory);

            factory.CreateDecoderFromStream(memStream, 0, Wic.DecodeMetadataCacheOnDemand, out var decoder);
            decoder.GetFrame(0, out var frame);
            frame.GetSize(out var width, out var height);

            // 存起來的縮圖最長邊就是 256，比檔案總管要的大時由外殼自己縮，
            // 這裡不再串一層 scaler（少一個介面、少一種出錯的方式）
            factory.CreateFormatConverter(out var converter);
            converter.Initialize(frame, Wic.Format32bppBGRA, Wic.DitherTypeNone, 0, 0, Wic.PaletteTypeCustom);

            return CopyToDib(converter, width, height);
        }
        finally
        {
            Marshal.Release(memStream);
        }
    }

    private static unsafe nint CopyToDib(IWICFormatConverter converter, uint width, uint height)
    {
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            biWidth = (int)width,
            biHeight = -(int)height, // 負的＝由上而下，跟 WIC 給的順序一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var bitmap = Native.CreateDIBSection(0, &header, 0, out var bits, 0, 0);
        if (bitmap == 0 || bits == 0) throw new OutOfMemoryException();

        try
        {
            var stride = width * 4;
            converter.CopyPixels(0, stride, stride * height, bits);
            return bitmap;
        }
        catch
        {
            Native.DeleteObject(bitmap);
            throw;
        }
    }
}
