using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MinePainter.Thumbnails;

internal static unsafe partial class Native
{
    /// <summary>把原生 COM 指標包成源產生互通用得到的物件（AOT 相容）。</summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    internal static object CreateComInstance(Guid clsid, Guid iid)
    {
        var hr = CoCreateInstance(in clsid, 0, CLSCTX_INPROC_SERVER, in iid, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>把管理物件包成 COM 介面指標（給 DllGetClassObject 用）。</summary>
    internal static nint WrapForCom(object instance, in Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            var hr = Marshal.QueryInterface(unknown, in iid, out var itf);
            return hr == 0 ? itf : 0;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private const int CLSCTX_INPROC_SERVER = 1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid clsid, nint outer, int context, in Guid iid, out nint instance);

    [LibraryImport("shlwapi.dll")]
    internal static partial nint SHCreateMemStream(byte* data, uint size);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateDIBSection(
        nint dc, BITMAPINFOHEADER* header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
