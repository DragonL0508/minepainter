using SkiaSharp;

namespace MinePainter.Core.Tiles;

/// <summary>8-bit 單通道 tile（筆劃覆蓋度 / 選取遮罩用）。</summary>
public sealed class MaskTile
{
    public const int Size = 256;
    public readonly byte[] Alpha = new byte[Size * Size];
}

/// <summary>
/// 稀疏 8-bit 遮罩面：筆劃覆蓋度緩衝（M2）與選取遮罩（M4）共用。
/// 非執行緒安全 —— 與 TileSurface 相同，由 Document.SyncRoot 保護。
/// </summary>
public sealed class MaskSurface
{
    private readonly Dictionary<TileIndex, MaskTile> _tiles = new();
    private SKRectI _bounds = SKRectI.Empty;

    public int TileCount => _tiles.Count;

    /// <summary>累計被寫入過的像素範圍（doc 座標）。</summary>
    public SKRectI Bounds => _bounds;

    public MaskTile? GetForRead(TileIndex idx) => _tiles.GetValueOrDefault(idx);

    public MaskTile GetForWrite(TileIndex idx)
    {
        if (!_tiles.TryGetValue(idx, out var tile))
        {
            tile = new MaskTile();
            _tiles[idx] = tile;
        }
        return tile;
    }

    public IReadOnlyDictionary<TileIndex, MaskTile> Tiles => _tiles;

    /// <summary>
    /// 以「取大值」把 dab 遮罩蓋上（wash 語意：同筆劃重疊不會加深）。
    /// dab 為 dabW×dabH 的 8-bit 覆蓋度，左上角放在 topLeft（doc 座標）。
    /// clip 非 null 時逐像素乘上其覆蓋度（選取遮罩裁切）。
    /// </summary>
    public void StampMax(ReadOnlySpan<byte> dab, int dabW, int dabH, SKPointI topLeft,
        MaskSurface? clip = null, SKRectI? bounds = null)
    {
        var dabRect = SKRectI.Create(topLeft.X, topLeft.Y, dabW, dabH);
        if (bounds is { } limit)
        {
            dabRect = SKRectI.Intersect(dabRect, limit);
            if (dabRect.Width <= 0 || dabRect.Height <= 0) return;
        }
        Span<byte> clipped = dabW <= 1024 ? stackalloc byte[Math.Max(1, dabW)] : new byte[dabW];

        foreach (var idx in TileIndex.CoveringRect(dabRect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, dabRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            MaskTile? clipTile = null;
            if (clip != null)
            {
                clipTile = clip.GetForRead(idx);
                if (clipTile == null) continue; // 選取外 → 全部裁掉
            }

            var tile = GetForWrite(idx);
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var srcRow = dab.Slice((y - topLeft.Y) * dabW + (inter.Left - topLeft.X), inter.Width);
                var dstRow = tile.Alpha.AsSpan((y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left), inter.Width);

                if (clipTile != null)
                {
                    var clipRow = clipTile.Alpha.AsSpan((y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left), inter.Width);
                    var tmp = clipped[..inter.Width];
                    for (var i = 0; i < inter.Width; i++)
                        tmp[i] = (byte)(srcRow[i] * clipRow[i] / 255);
                    MaxBlend(tmp, dstRow);
                }
                else
                {
                    MaxBlend(srcRow, dstRow);
                }
            }
        }

        _bounds = _bounds.IsEmpty ? dabRect : SKRectI.Union(_bounds, dabRect);
    }

    private static void MaxBlend(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var w = System.Numerics.Vector<byte>.Count;
            for (; i + w <= src.Length; i += w)
            {
                var s = new System.Numerics.Vector<byte>(src.Slice(i, w));
                var d = new System.Numerics.Vector<byte>(dst.Slice(i, w));
                System.Numerics.Vector.Max(s, d).CopyTo(dst.Slice(i, w));
            }
        }
        for (; i < src.Length; i++)
            if (src[i] > dst[i]) dst[i] = src[i];
    }

    /// <summary>
    /// 丟掉覆蓋度全為 0 的格。
    /// 減法／交集後必須做這件事，否則會留下「有 tile 但全空」的遮罩：
    /// IsEmpty 會誤判為 false，工具就會以為有選取卻什麼都畫不出來。
    /// </summary>
    public void RemoveEmptyTiles()
    {
        List<TileIndex>? empty = null;
        foreach (var (idx, tile) in _tiles)
        {
            var blank = true;
            foreach (var a in tile.Alpha)
            {
                if (a == 0) continue;
                blank = false;
                break;
            }
            if (blank) (empty ??= []).Add(idx);
        }

        if (empty == null) return;
        foreach (var idx in empty) _tiles.Remove(idx);
        if (_tiles.Count == 0) _bounds = SKRectI.Empty;
    }

    /// <summary>直接寫入 tile 後補記 bounds（FromPath/Combine 等不經 StampMax 的路徑用）。</summary>
    public void ExtendBounds(SKRectI rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        _bounds = _bounds.IsEmpty ? rect : SKRectI.Union(_bounds, rect);
    }

    /// <summary>
    /// 直接指定 bounds（可以縮小）。
    /// 用於「柵格化後才知道真正覆蓋範圍」的情況 —— 先前只能靠 ExtendBounds 放大，
    /// 於是 Bounds 會比實際覆蓋大個一兩像素，放大檢視時框就對不齊。
    /// </summary>
    public void SetBounds(SKRectI rect) =>
        _bounds = rect.Width <= 0 || rect.Height <= 0 ? SKRectI.Empty : rect;

    public void Clear()
    {
        _tiles.Clear();
        _bounds = SKRectI.Empty;
    }
}
