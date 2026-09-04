using SkiaSharp;

namespace MinePainter.Core.Tiles;

/// <summary>
/// 一塊 256×256 BGRA32 premultiplied 的像素緩衝區，配合引用計數做 copy-on-write。
/// AddRef 即快照 —— undo 快照、群組快取、背景存檔全靠這個性質。
///
/// 規約：
/// - 建立時 refCount = 1，擁有者呼叫 Release() 歸還。
/// - 共享（IsShared）的 tile 絕不可寫入；要寫先 Clone。
/// - 寫入權由外部鎖（Document.SyncRoot）保證，refCount 本身用 Interlocked。
/// </summary>
public sealed unsafe class Tile
{
    public const int Size = 256;
    public const int RowBytes = Size * 4;
    public const int BytesPerTile = Size * RowBytes; // 256 KB

    public static readonly SKImageInfo Info =
        new(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);

    private readonly TilePool _pool;
    private IntPtr _pixels;
    private int _refCount;

    private Tile(TilePool pool, IntPtr pixels)
    {
        _pool = pool;
        _pixels = pixels;
        _refCount = 1;
    }

    public static Tile Rent(TilePool pool, bool zeroed = true) => new(pool, pool.Rent(zeroed));

    private static long _versionSeed;

    /// <summary>
    /// 內容版本：每次有人取得寫入權就換一個新號。顯示端（GPU 貼圖快取）靠它知道「這格要不要重傳」——
    /// 不能用物件識別，就地寫入時 Tile 實例不會換。
    ///
    /// 號碼取自全域遞增的種子，**不是每格自己數**：同一格的 Tile 實例會換人
    /// （寫時複製、undo 還原、從 pool 借新的），各數各的就會出現「不同內容、同一個號碼」，
    /// 顯示端因此貼出上一份的畫面（提起選取後原處的洞不見、undo 後畫面沒跟著回去）。
    /// </summary>
    public long Version { get; private set; } = Interlocked.Increment(ref _versionSeed);

    internal void BumpVersion() => Version = Interlocked.Increment(ref _versionSeed);

    public bool IsShared => Volatile.Read(ref _refCount) > 1;
    public bool IsAlive => Volatile.Read(ref _refCount) > 0;

    public IntPtr Pixels
    {
        get
        {
            ThrowIfDead();
            return _pixels;
        }
    }

    public Span<byte> PixelSpan
    {
        get
        {
            ThrowIfDead();
            return new Span<byte>((void*)_pixels, BytesPerTile);
        }
    }

    public void AddRef()
    {
        ThrowIfDead();
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// 已經釋放就回 false 而不是丟例外。合成器讀別人的 tile 時用這個佔住一份引用：
    /// 佔得住就保證這塊緩衝在畫完之前不會被還回池子（拿到的內容可能是舊的，但不會是別人的）。
    /// </summary>
    public bool TryAddRef()
    {
        while (true)
        {
            var count = Volatile.Read(ref _refCount);
            if (count <= 0) return false;
            if (Interlocked.CompareExchange(ref _refCount, count + 1, count) == count) return true;
        }
    }

    public void Release()
    {
        var count = Interlocked.Decrement(ref _refCount);
        if (count == 0)
        {
            var p = Interlocked.Exchange(ref _pixels, IntPtr.Zero);
            if (p != IntPtr.Zero) _pool.Return(p);
        }
        else if (count < 0)
        {
            throw new InvalidOperationException("Tile 被過度 Release（refCount < 0）。");
        }
    }

    /// <summary>複製一份內容相同、refCount=1 的新 tile（COW 的寫入分支）。</summary>
    public Tile Clone(TilePool pool)
    {
        ThrowIfDead();
        var copy = Rent(pool, zeroed: false);
        PixelSpan.CopyTo(copy.PixelSpan);
        return copy;
    }

    /// <summary>零拷貝包裝成 SKPixmap 給 Skia 讀寫；呼叫者須確保 tile 存活期間內使用。</summary>
    public SKPixmap AsPixmap()
    {
        ThrowIfDead();
        return new SKPixmap(Info, _pixels, RowBytes);
    }

    /// <summary>整塊是否全透明（premul 下 = 全零）。以 64-bit 步進掃描。</summary>
    public bool IsBlank()
    {
        ThrowIfDead();
        var p = (ulong*)_pixels;
        for (var i = 0; i < BytesPerTile / sizeof(ulong); i++)
            if (p[i] != 0) return false;
        return true;
    }

    private void ThrowIfDead()
    {
        if (Volatile.Read(ref _refCount) <= 0)
            throw new ObjectDisposedException(nameof(Tile), "存取已釋放的 tile。");
    }
}
