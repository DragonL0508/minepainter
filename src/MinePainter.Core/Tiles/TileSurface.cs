using SkiaSharp;

namespace MinePainter.Core.Tiles;

/// <summary>
/// 稀疏 tile 圖：只有非透明區域佔記憶體。
/// 非執行緒安全 —— 呼叫者以 Document.SyncRoot 保護（Snapshot 也須在鎖內取）。
/// </summary>
public sealed class TileSurface : IDisposable
{
    private readonly TilePool _pool;
    private readonly Dictionary<TileIndex, Tile> _tiles = new();

    // ExactContentBounds 的快取鍵：任何取得寫入權的操作都會 +1。
    // 注意這追蹤的是「取得寫入權」而非實際寫入 —— 持有 GetTileForWrite 回傳的 tile
    // 之後再改像素不會再 +1；實務上每一批寫入都會重新走 GetTileForWrite（COW 檢查需要）。
    private int _revision;
    private (int Revision, SKRectI Bounds) _exactCache = (-1, SKRectI.Empty);

    public TileSurface(TilePool? pool = null) => _pool = pool ?? TilePool.Shared;

    public int TileCount => _tiles.Count;
    public IReadOnlyDictionary<TileIndex, Tile> Tiles => _tiles;

    /// <summary>tile 粒度的內容邊界（文件像素座標）；無內容回傳 Empty。</summary>
    public SKRectI ContentBounds
    {
        get
        {
            if (_tiles.Count == 0) return SKRectI.Empty;
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (var idx in _tiles.Keys)
            {
                l = Math.Min(l, idx.X * Tile.Size);
                t = Math.Min(t, idx.Y * Tile.Size);
                r = Math.Max(r, (idx.X + 1) * Tile.Size);
                b = Math.Max(b, (idx.Y + 1) * Tile.Size);
            }
            return new SKRectI(l, t, r, b);
        }
    }

    /// <summary>
    /// 逐像素掃出的精確內容邊界（文件像素座標）；無內容回傳 Empty。
    /// <see cref="ContentBounds"/> 是 tile 對齊的保守外擴，夠用於失效與重繪；
    /// 要顯示給使用者（圖層內容框）或做「裁切至內容」時才用這個。
    /// 結果按寫入版本快取 —— 內容沒變時重複呼叫是 O(1)，移動工具每次重算把手框都會經過這裡。
    /// </summary>
    public unsafe SKRectI ExactContentBounds()
    {
        if (_exactCache.Revision == _revision) return _exactCache.Bounds;

        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;

        foreach (var (idx, tile) in _tiles)
        {
            var origin = idx.ToPixelRect();
            var p = (uint*)tile.Pixels;

            for (var y = 0; y < Tile.Size; y++)
            {
                var row = p + y * Tile.Size;
                int rowLeft = -1, rowRight = -1;
                for (var x = 0; x < Tile.Size; x++)
                {
                    if (row[x] == 0) continue; // premultiplied：全零 = 全透明
                    if (rowLeft < 0) rowLeft = x;
                    rowRight = x;
                }
                if (rowLeft < 0) continue;

                l = Math.Min(l, origin.Left + rowLeft);
                r = Math.Max(r, origin.Left + rowRight + 1);
                t = Math.Min(t, origin.Top + y);
                b = Math.Max(b, origin.Top + y + 1);
            }
        }

        var result = l == int.MaxValue ? SKRectI.Empty : new SKRectI(l, t, r, b);
        _exactCache = (_revision, result);
        return result;
    }

    /// <summary>讀取用；null = 該格全透明。</summary>
    public Tile? GetTileForRead(TileIndex idx) => _tiles.GetValueOrDefault(idx);

    /// <summary>寫入用：缺格就建零 tile；共享中（快照持有）就先 Clone —— COW 核心。</summary>
    public Tile GetTileForWrite(TileIndex idx)
    {
        _revision++;
        if (!_tiles.TryGetValue(idx, out var tile))
        {
            tile = Tile.Rent(_pool);
            _tiles[idx] = tile;
        }
        else if (tile.IsShared)
        {
            var clone = tile.Clone(_pool);
            tile.Release();
            _tiles[idx] = clone;
            tile = clone;
        }
        return tile;
    }

    /// <summary>移除並釋放一格（例如 commit 後發現全透明）。</summary>
    public void RemoveTile(TileIndex idx)
    {
        if (_tiles.Remove(idx, out var tile))
        {
            _revision++;
            tile.Release();
        }
    }

    /// <summary>對所有 tile AddRef 的 O(tile 數) 快照。呼叫者用畢須 Dispose。</summary>
    public TileSnapshot Snapshot()
    {
        var dict = new Dictionary<TileIndex, Tile>(_tiles.Count);
        foreach (var (idx, tile) in _tiles)
        {
            tile.AddRef();
            dict[idx] = tile;
        }
        return new TileSnapshot(dict);
    }

    /// <summary>只快照與 rect 相交的格。</summary>
    public TileSnapshot Snapshot(SKRectI rect)
    {
        var dict = new Dictionary<TileIndex, Tile>();
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            if (_tiles.TryGetValue(idx, out var tile))
            {
                tile.AddRef();
                dict[idx] = tile;
            }
        }
        return new TileSnapshot(dict);
    }

    /// <summary>把快照的 tile 裝回（undo/redo 用）：整格替換，接手快照的引用。</summary>
    public void RestoreTile(TileIndex idx, Tile? tile)
    {
        _revision++;
        if (_tiles.Remove(idx, out var old)) old.Release();
        if (tile != null)
        {
            tile.AddRef();
            _tiles[idx] = tile;
        }
    }

    /// <summary>從 SKPixmap 匯入像素（來源須為 BGRA8888 premul），寫到 destPos 起。</summary>
    public void CopyFrom(SKPixmap src, SKPointI destPos)
    {
        if (src.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException($"來源必須是 Bgra8888，實際為 {src.ColorType}");

        var destRect = SKRectI.Create(destPos.X, destPos.Y, src.Width, src.Height);
        foreach (var idx in TileIndex.CoveringRect(destRect))
        {
            var tileRect = idx.ToPixelRect();
            if (!tileRect.IntersectsWith(destRect)) continue;
            var inter = SKRectI.Intersect(tileRect, destRect);

            var tile = GetTileForWrite(idx);
            using var dstPixmap = tile.AsPixmap();
            unsafe
            {
                var srcBase = (byte*)src.GetPixels();
                var dstBase = (byte*)dstPixmap.GetPixels();
                for (var y = inter.Top; y < inter.Bottom; y++)
                {
                    var srcRow = srcBase + (y - destPos.Y) * src.RowBytes + (inter.Left - destPos.X) * 4;
                    var dstRow = dstBase + (y - tileRect.Top) * Tile.RowBytes + (inter.Left - tileRect.Left) * 4;
                    Buffer.MemoryCopy(srcRow, dstRow, inter.Width * 4, inter.Width * 4);
                }
            }
        }
    }

    /// <summary>以純色填滿矩形（premul 寫入）。</summary>
    public void Fill(SKRectI rect, SKColor color)
    {
        var premul = SKPreMultipliedColor(color);
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            var tile = GetTileForWrite(idx);
            unsafe
            {
                var basePtr = (uint*)tile.Pixels;
                for (var y = inter.Top; y < inter.Bottom; y++)
                {
                    var row = basePtr + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                    new Span<uint>(row, inter.Width).Fill(premul);
                }
            }
        }
    }

    private static uint SKPreMultipliedColor(SKColor c)
    {
        uint a = c.Alpha;
        uint r = (uint)(c.Red * a / 255);
        uint g = (uint)(c.Green * a / 255);
        uint b = (uint)(c.Blue * a / 255);
        return (a << 24) | (r << 16) | (g << 8) | b; // BGRA 記憶體序 = ARGB little-endian
    }

    public void Dispose()
    {
        foreach (var tile in _tiles.Values) tile.Release();
        _tiles.Clear();
    }
}

/// <summary>TileSurface 的引用式快照；Dispose 釋放所有引用。</summary>
public sealed class TileSnapshot : IDisposable
{
    private readonly Dictionary<TileIndex, Tile> _tiles;
    private bool _disposed;

    internal TileSnapshot(Dictionary<TileIndex, Tile> tiles) => _tiles = tiles;

    public IReadOnlyDictionary<TileIndex, Tile> Tiles => _tiles;

    public Tile? GetTile(TileIndex idx) => _tiles.GetValueOrDefault(idx);

    /// <summary>估算持有的記憶體（未去重共享）。</summary>
    public long MemoryCost => (long)_tiles.Count * Tile.BytesPerTile;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tile in _tiles.Values) tile.Release();
        _tiles.Clear();
    }
}
