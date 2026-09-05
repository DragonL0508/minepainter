using System.Runtime.InteropServices;

namespace MinePainter.Core.AI;

/// <summary>
/// 系統記憶體查詢（GlobalMemoryStatusEx）。history 的記憶體上限依總量決定。
/// 非 Windows 一律回報 0（不知道）。
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
}
