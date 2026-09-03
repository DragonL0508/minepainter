using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MinePainter.Thumbnails;

[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    void CreateInstance(nint outer, in Guid iid, out nint instance);
    void LockServer([MarshalAs(UnmanagedType.Bool)] bool @lock);
}

[GeneratedComClass]
internal sealed partial class MppThumbnailFactory : IClassFactory
{
    public void CreateInstance(nint outer, in Guid iid, out nint instance)
    {
        if (outer != 0) throw new COMException("不支援聚合。", CLASS_E_NOAGGREGATION);

        instance = Native.WrapForCom(new MppThumbnailProvider(), in iid);
        if (instance == 0) throw new COMException("要的介面沒有實作。", E_NOINTERFACE);
    }

    public void LockServer(bool @lock)
    {
    }

    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
}

/// <summary>
/// COM 伺服器的進入點。NativeAOT 把它們編成真正的 DLL 匯出，所以檔案總管的
/// dllhost 可以直接載入，使用者的電腦不需要裝 .NET。
/// </summary>
internal static class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static unsafe int DllGetClassObject(Guid* clsid, Guid* iid, nint* instance)
    {
        if (instance is null) return E_POINTER;
        *instance = 0;
        if (clsid is null || iid is null) return E_POINTER;
        if (*clsid != MppThumbnailProvider.Clsid) return CLASS_E_CLASSNOTAVAILABLE;

        try
        {
            var ptr = Native.WrapForCom(new MppThumbnailFactory(), in *iid);
            if (ptr == 0) return E_NOINTERFACE;
            *instance = ptr;
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : E_FAIL;
        }
    }

    /// <summary>回 S_FALSE：讓外殼把 DLL 留在記憶體，省下每張縮圖重載一次的成本。</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => 1;

    private const int E_POINTER = unchecked((int)0x80004003);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
}
