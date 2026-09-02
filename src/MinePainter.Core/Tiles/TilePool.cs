using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MinePainter.Core.Tiles;

/// <summary>
/// tile 像素緩衝區的回收池：避免大量 256KB alloc/free 造成碎片與延遲尖峰。
/// 緩衝區以 64B 對齊配置（SIMD 友善）。執行緒安全。
/// </summary>
public sealed class TilePool : IDisposable
{
    /// <summary>全域共用池；Document 與快取預設都用這個。</summary>
    public static TilePool Shared { get; } = new();

    private readonly ConcurrentBag<IntPtr> _free = new();
    private long _rented;
    private bool _disposed;

    /// <summary>目前借出的緩衝區數（診斷用）。</summary>
    public long RentedCount => Interlocked.Read(ref _rented);
    public int FreeCount => _free.Count;

    public unsafe IntPtr Rent(bool zeroed)
    {
        Interlocked.Increment(ref _rented);
        if (_free.TryTake(out var ptr))
        {
            if (zeroed) new Span<byte>((void*)ptr, Tile.BytesPerTile).Clear();
            return ptr;
        }

        var p = (IntPtr)NativeMemory.AlignedAlloc(Tile.BytesPerTile, 64);
        if (zeroed) new Span<byte>((void*)p, Tile.BytesPerTile).Clear();
        return p;
    }

    public unsafe void Return(IntPtr ptr)
    {
        Interlocked.Decrement(ref _rented);
        if (_disposed)
        {
            NativeMemory.AlignedFree((void*)ptr);
            return;
        }
        _free.Add(ptr);
    }

    public unsafe void Dispose()
    {
        _disposed = true;
        while (_free.TryTake(out var ptr))
            NativeMemory.AlignedFree((void*)ptr);
    }
}
