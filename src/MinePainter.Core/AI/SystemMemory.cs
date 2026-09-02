using System.Runtime.InteropServices;

namespace MinePainter.Core.AI;

/// <summary>
/// 開算前要知道「這台機器現在還剩多少記憶體」。
/// 系統記憶體用 GlobalMemoryStatusEx；顯示卡用 DXGI 的 QueryVideoMemoryInfo（DirectML 的 device N
/// 就是 IDXGIFactory1::EnumAdapters1 的第 N 張，所以這裡的列舉順序跟 DirectML 對得起來）。
/// 非 Windows 一律回報「不知道」，呼叫端就當作沒有 GPU 預算。
/// </summary>
public static class SystemMemory
{
    /// <summary>目前可用的實體記憶體；查不到回 0。</summary>
    public static ulong AvailablePhysicalBytes
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.AvailPhys : 0;
        }
    }

    /// <summary>實體記憶體總量；查不到回 0。</summary>
    public static ulong TotalPhysicalBytes
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
        }
    }

    /// <summary>一張顯示卡：Index 就是 DirectML 的 device id。</summary>
    public sealed record GpuAdapter(int Index, string Name, ulong DedicatedVideoMemory, bool IsSoftware)
    {
        /// <summary>目前這張卡的專屬顯示記憶體還剩多少（budget - 已用）；查不到時退回 DedicatedVideoMemory。</summary>
        public ulong AvailableVideoMemory { get; init; }
    }

    /// <summary>
    /// 列舉顯示卡（含目前可用的 VRAM）。查不到或非 Windows 回空清單。
    /// 軟體轉譯器（Microsoft Basic Render Driver）也會列出來，選卡時要自己排除。
    /// </summary>
    public static IReadOnlyList<GpuAdapter> EnumerateGpus()
    {
        var list = new List<GpuAdapter>();
        if (!OperatingSystem.IsWindows()) return list;

        var iid = IidFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        try
        {
            var enumAdapters1 = VTable<EnumAdapters1Fn>(factory, 12);
            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factory, i, out var adapter) != 0) break;
                try
                {
                    if (VTable<GetDesc1Fn>(adapter, 10)(adapter, out var desc) != 0) continue;
                    var software = (desc.Flags & DxgiAdapterFlagSoftware) != 0;
                    list.Add(new GpuAdapter((int)i, desc.Description, (ulong)desc.DedicatedVideoMemory, software)
                    {
                        AvailableVideoMemory = QueryAvailableVideoMemory(adapter) ?? (ulong)desc.DedicatedVideoMemory,
                    });
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (DllNotFoundException) { /* 沒有 dxgi.dll：當作沒有 GPU */ }
        catch (EntryPointNotFoundException) { }
        finally { Marshal.Release(factory); }
        return list;
    }

    /// <summary>DirectML 會用到的那張卡（非軟體、專屬 VRAM 最大）；沒有回 null。</summary>
    public static GpuAdapter? PreferredGpu()
    {
        GpuAdapter? best = null;
        foreach (var g in EnumerateGpus())
        {
            if (g.IsSoftware || g.DedicatedVideoMemory == 0) continue;
            if (best == null || g.DedicatedVideoMemory > best.DedicatedVideoMemory) best = g;
        }
        return best;
    }

    /// <summary>IDXGIAdapter3::QueryVideoMemoryInfo(LOCAL)；舊介面查不到回 null。</summary>
    private static ulong? QueryAvailableVideoMemory(IntPtr adapter)
    {
        var iid = IidAdapter3;
        if (Marshal.QueryInterface(adapter, ref iid, out var adapter3) != 0) return null;
        try
        {
            if (VTable<QueryVideoMemoryInfoFn>(adapter3, 14)(adapter3, 0, MemorySegmentLocal, out var info) != 0)
                return null;
            return info.Budget > info.CurrentUsage ? info.Budget - info.CurrentUsage : 0;
        }
        finally { Marshal.Release(adapter3); }
    }

    private static T VTable<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    private const uint DxgiAdapterFlagSoftware = 2;
    private const int MemorySegmentLocal = 0;
    private static Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static Guid IidAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out AdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoFn(IntPtr self, uint nodeIndex, int segmentGroup,
        out VideoMemoryInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId;
        public int Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }
}
