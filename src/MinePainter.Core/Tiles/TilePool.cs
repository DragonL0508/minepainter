using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MinePainter.Core.Tiles;

/// <summary>
/// tile 像素緩衝區的回收池：避免大量 256KB alloc/free 造成碎片與延遲尖峰。
/// 緩衝區以 64B 對齊配置（SIMD 友善）。執行緒安全。
///
/// 池子有上限（<see cref="MaxFreeTiles"/>）：關分頁、淘汰歷史、丟掉背景分頁的合成快取
/// 這類「一次還回上千格」的場合，沒有上限的話這些記憶體會永遠留在行程裡下不來。
/// 上限之內仍然全部留著 —— 筆劃／拖曳的每秒數千次借還要的就是那個緩衝。
/// </summary>
public sealed class TilePool : IDisposable
{
    /// <summary>全域共用池；Document 與快取預設都用這個。</summary>
    public static TilePool Shared { get; } = new();

    private readonly ConcurrentBag<IntPtr> _free = new();
    private long _rented;
    private int _freeCount;
    private bool _disposed;

    /// <summary>閒置緩衝區的保留上限（格數；256 格 = 64 MB）。超出的直接還給系統。</summary>
    public int MaxFreeTiles { get; set; } = 256;

    /// <summary>目前借出的緩衝區數（診斷用）。</summary>
    public long RentedCount => Interlocked.Read(ref _rented);
    public int FreeCount => Volatile.Read(ref _freeCount);

    public unsafe IntPtr Rent(bool zeroed)
    {
        Interlocked.Increment(ref _rented);
        if (_free.TryTake(out var ptr))
        {
            Interlocked.Decrement(ref _freeCount);
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
        if (_disposed || Interlocked.Increment(ref _freeCount) > MaxFreeTiles)
        {
            Interlocked.Decrement(ref _freeCount);
            NativeMemory.AlignedFree((void*)ptr);
            return;
        }
        _free.Add(ptr);
    }

    /// <summary>
    /// 把閒置緩衝區還給系統，只留 <paramref name="keep"/> 格。
    /// 關分頁／分頁切到背景後呼叫 —— 那時剛還回來一大批，留著也用不到。
    /// </summary>
    public unsafe void Trim(int keep = 0)
    {
        while (Volatile.Read(ref _freeCount) > keep && _free.TryTake(out var ptr))
        {
            Interlocked.Decrement(ref _freeCount);
            NativeMemory.AlignedFree((void*)ptr);
        }
    }

    public unsafe void Dispose()
    {
        _disposed = true;
        while (_free.TryTake(out var ptr))
        {
            Interlocked.Decrement(ref _freeCount);
            NativeMemory.AlignedFree((void*)ptr);
        }
    }
}
