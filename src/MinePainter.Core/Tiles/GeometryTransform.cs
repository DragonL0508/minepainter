using SkiaSharp;

namespace MinePainter.Core.Tiles;

/// <summary>畫布層級的幾何操作；全部是 90° 倍數或鏡射，可用整數索引無損完成。</summary>
public enum GeometryOp
{
    FlipHorizontal,
    FlipVertical,
    Rotate90CW,
    Rotate90CCW,
    Rotate180,
}

public static class GeometryTransform
{
    /// <summary>操作後的畫布尺寸（90° 旋轉會寬高互換）。</summary>
    public static SKSizeI ResultSize(GeometryOp op, SKSizeI size) => op switch
    {
        GeometryOp.Rotate90CW or GeometryOp.Rotate90CCW => new SKSizeI(size.Height, size.Width),
        _ => size,
    };

    /// <summary>此操作的反操作（undo 用：翻轉與 180° 是自反，90° 兩向互為反操作）。</summary>
    public static GeometryOp Inverse(GeometryOp op) => op switch
    {
        GeometryOp.Rotate90CW => GeometryOp.Rotate90CCW,
        GeometryOp.Rotate90CCW => GeometryOp.Rotate90CW,
        _ => op, // FlipH / FlipV / Rotate180 自己就是自己的反操作
    };

    /// <summary>把目的座標映射回來源座標（destination-driven，避免縫隙）。</summary>
    public static SKPointI MapBack(GeometryOp op, int dx, int dy, SKSizeI srcSize) => op switch
    {
        GeometryOp.FlipHorizontal => new SKPointI(srcSize.Width - 1 - dx, dy),
        GeometryOp.FlipVertical => new SKPointI(dx, srcSize.Height - 1 - dy),
        GeometryOp.Rotate180 => new SKPointI(srcSize.Width - 1 - dx, srcSize.Height - 1 - dy),
        // 順時針：目的 (dx,dy) ← 來源 (dy, H-1-dx)
        GeometryOp.Rotate90CW => new SKPointI(dy, srcSize.Height - 1 - dx),
        // 逆時針：目的 (dx,dy) ← 來源 (W-1-dy, dx)
        _ => new SKPointI(srcSize.Width - 1 - dy, dx),
    };

    /// <summary>把來源座標映射到目的座標（點/物件位置用）。</summary>
    /// <summary>op 在文件座標上的仿射矩陣（與 <see cref="MapForward"/> 一致）。</summary>
    public static SKMatrix Matrix(GeometryOp op, SKSizeI srcSize) => op switch
    {
        GeometryOp.FlipHorizontal => new SKMatrix(-1, 0, srcSize.Width, 0, 1, 0, 0, 0, 1),
        GeometryOp.FlipVertical => new SKMatrix(1, 0, 0, 0, -1, srcSize.Height, 0, 0, 1),
        GeometryOp.Rotate180 => new SKMatrix(-1, 0, srcSize.Width, 0, -1, srcSize.Height, 0, 0, 1),
        GeometryOp.Rotate90CW => new SKMatrix(0, -1, srcSize.Height, 1, 0, 0, 0, 0, 1),
        _ => new SKMatrix(0, 1, 0, -1, 0, srcSize.Width, 0, 0, 1),
    };

    public static SKPoint MapForward(GeometryOp op, SKPoint p, SKSizeI srcSize) => op switch
    {
        GeometryOp.FlipHorizontal => new SKPoint(srcSize.Width - p.X, p.Y),
        GeometryOp.FlipVertical => new SKPoint(p.X, srcSize.Height - p.Y),
        GeometryOp.Rotate180 => new SKPoint(srcSize.Width - p.X, srcSize.Height - p.Y),
        GeometryOp.Rotate90CW => new SKPoint(srcSize.Height - p.Y, p.X),
        _ => new SKPoint(p.Y, srcSize.Width - p.X),
    };

    /// <summary>
    /// 產生變換後的新 tile 圖。逐目的 tile 填滿再提交，避免對同一 tile 反覆觸發 COW；
    /// 全空的目的 tile 直接不建立（維持稀疏）。
    /// </summary>
    public static unsafe TileSurface Transform(TileSurface source, GeometryOp op, SKSizeI srcSize,
        SKPointI srcOffset, TilePool? pool = null)
    {
        var dstSize = ResultSize(op, srcSize);
        var result = new TileSurface(pool);
        var dstRect = SKRectI.Create(0, 0, dstSize.Width, dstSize.Height);

        // 來源像素查找器（快取最後命中的 tile，同 tile 連續讀是純指標運算）
        var lastTx = int.MinValue;
        var lastTy = int.MinValue;
        uint* srcPixels = null;

        uint ReadSource(int x, int y)
        {
            if (x < 0 || y < 0 || x >= srcSize.Width || y >= srcSize.Height) return 0;
            // 文件座標 → 圖層座標
            var lx = x - srcOffset.X;
            var ly = y - srcOffset.Y;
            var tx = lx >> 8;
            var ty = ly >> 8;
            if (tx != lastTx || ty != lastTy)
            {
                lastTx = tx;
                lastTy = ty;
                var tile = source.GetTileForRead(new TileIndex(tx, ty));
                srcPixels = tile == null ? null : (uint*)tile.Pixels;
            }
            if (srcPixels == null) return 0;
            return srcPixels[((ly & 255) << 8) | (lx & 255)];
        }

        // 一格的暫存（256KB，不能放 stack）；跨 tile 重複使用
        var buffer = new uint[Tile.Size * Tile.Size];

        foreach (var idx in TileIndex.CoveringRect(dstRect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, dstRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            Array.Clear(buffer);
            var any = false;

            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                for (var x = inter.Left; x < inter.Right; x++)
                {
                    var src = MapBack(op, x, y, srcSize);
                    var value = ReadSource(src.X, src.Y);
                    if (value == 0) continue;
                    buffer[((y - tileRect.Top) << 8) | (x - tileRect.Left)] = value;
                    any = true;
                }
            }

            if (!any) continue; // 全空的格不建立，維持稀疏

            var dstTile = result.GetTileForWrite(idx);
            fixed (uint* srcBuf = buffer)
            {
                Buffer.MemoryCopy(srcBuf, (void*)dstTile.Pixels, Tile.BytesPerTile, Tile.BytesPerTile);
            }
        }

        return result;
    }

    /// <summary>對 8-bit 遮罩做同樣的變換（選取範圍跟著畫布一起轉）。</summary>
    public static MaskSurface TransformMask(MaskSurface source, GeometryOp op, SKSizeI srcSize)
    {
        var dstSize = ResultSize(op, srcSize);
        var result = new MaskSurface();
        var dstRect = SKRectI.Create(0, 0, dstSize.Width, dstSize.Height);

        foreach (var idx in TileIndex.CoveringRect(dstRect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, dstRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            MaskTile? dstTile = null;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                for (var x = inter.Left; x < inter.Right; x++)
                {
                    var src = MapBack(op, x, y, srcSize);
                    if (src.X < 0 || src.Y < 0 || src.X >= srcSize.Width || src.Y >= srcSize.Height) continue;

                    var srcTile = source.GetForRead(TileIndex.FromPixel(src.X, src.Y));
                    if (srcTile == null) continue;
                    var value = srcTile.Alpha[((src.Y & 255) << 8) | (src.X & 255)];
                    if (value == 0) continue;

                    dstTile ??= result.GetForWrite(idx);
                    dstTile.Alpha[((y - tileRect.Top) << 8) | (x - tileRect.Left)] = value;
                }
            }
        }

        result.ExtendBounds(dstRect);
        return result;
    }
}
