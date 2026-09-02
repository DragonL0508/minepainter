using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Selections;

/// <summary>
/// scanline flood fill：從種子點沿相近顏色擴散，輸出成選取遮罩（doc 座標）。
/// 以輸出遮罩本身作為 visited 集（0 = 未訪），tile 指標逐行快取，避免每像素雜湊/字典成本。
/// 在 Document.SyncRoot 內呼叫。
/// </summary>
public static class FloodFiller
{
    /// <summary>
    /// 於圖層上執行 flood fill。seed 為 doc 座標；tolerance 0..255（各通道最大差）。
    /// 回傳 doc 座標的遮罩（含圖層 offset 校正）。
    /// </summary>
    public static unsafe SelectionMask Fill(RasterLayer layer, SKPointI seedDoc, byte tolerance, SKRectI docBounds)
    {
        var mask = new SelectionMask();
        var off = layer.Offset;
        var seed = new SKPointI(seedDoc.X - off.X, seedDoc.Y - off.Y);
        // 可填範圍 = 文件範圍（轉圖層座標；offset 為正時可為負值座標）
        var limit = new SKRectI(
            docBounds.Left - off.X, docBounds.Top - off.Y,
            docBounds.Right - off.X, docBounds.Bottom - off.Y);
        if (seed.X < limit.Left || seed.X >= limit.Right || seed.Y < limit.Top || seed.Y >= limit.Bottom)
            return mask;

        var reader = new PixelReader(layer.Surface);
        var writer = new MaskWriter(mask, off);
        var target = reader.Get(seed.X, seed.Y);

        var stack = new Stack<SKPointI>();
        stack.Push(seed);

        int minX = seed.X, maxX = seed.X, minY = seed.Y, maxY = seed.Y;

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (writer.IsSet(p.X, p.Y)) continue;
            if (!Matches(reader.Get(p.X, p.Y), target, tolerance)) continue;

            // 向左右擴出整條 run
            var x0 = p.X;
            while (x0 - 1 >= limit.Left && !writer.IsSet(x0 - 1, p.Y) && Matches(reader.Get(x0 - 1, p.Y), target, tolerance))
                x0--;
            var x1 = p.X;
            while (x1 + 1 < limit.Right && !writer.IsSet(x1 + 1, p.Y) && Matches(reader.Get(x1 + 1, p.Y), target, tolerance))
                x1++;

            writer.SetRun(x0, x1, p.Y);
            minX = Math.Min(minX, x0); maxX = Math.Max(maxX, x1);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);

            // 掃上下相鄰行：每個連續匹配段只推段首
            ScanRow(p.Y - 1);
            ScanRow(p.Y + 1);

            void ScanRow(int y)
            {
                if (y < limit.Top || y >= limit.Bottom) return;
                var inSpan = false;
                for (var x = x0; x <= x1; x++)
                {
                    var match = !writer.IsSet(x, y) && Matches(reader.Get(x, y), target, tolerance);
                    if (match && !inSpan)
                    {
                        stack.Push(new SKPointI(x, y));
                        inSpan = true;
                    }
                    else if (!match)
                    {
                        inSpan = false;
                    }
                }
            }
        }

        mask.Mask.ExtendBounds(new SKRectI(
            minX + off.X, minY + off.Y, maxX + 1 + off.X, maxY + 1 + off.Y));
        mask.RebuildOutlineFromMask();
        return mask;
    }

    private static bool Matches(uint c, uint target, byte tolerance)
    {
        if (c == target) return true;
        if (tolerance == 0) return false;

        var db = Math.Abs((int)(c & 0xFF) - (int)(target & 0xFF));
        var dg = Math.Abs((int)((c >> 8) & 0xFF) - (int)((target >> 8) & 0xFF));
        var dr = Math.Abs((int)((c >> 16) & 0xFF) - (int)((target >> 16) & 0xFF));
        var da = Math.Abs((int)((c >> 24) & 0xFF) - (int)((target >> 24) & 0xFF));
        return Math.Max(Math.Max(db, dg), Math.Max(dr, da)) <= tolerance;
    }

    /// <summary>圖層像素讀取器：快取最後命中的 tile，同 tile 連續讀為純指標運算。</summary>
    private unsafe struct PixelReader(TileSurface surface)
    {
        private int _tx = int.MinValue, _ty = int.MinValue;
        private uint* _pixels; // null = 該格透明

        public uint Get(int x, int y)
        {
            var tx = x >> 8;
            var ty = y >> 8;
            if (tx != _tx || ty != _ty)
            {
                _tx = tx; _ty = ty;
                var tile = surface.GetTileForRead(new TileIndex(tx, ty));
                _pixels = tile == null ? null : (uint*)tile.Pixels;
            }
            if (_pixels == null) return 0;
            return _pixels[((y & 255) << 8) | (x & 255)];
        }
    }

    /// <summary>遮罩寫入/查詢器（圖層座標介面，內部转 doc 座標），快取最後命中的 tile。</summary>
    private struct MaskWriter(SelectionMask mask, SKPointI offset)
    {
        private int _tx = int.MinValue, _ty = int.MinValue;
        private byte[]? _alpha;
        private bool _writable;

        public bool IsSet(int lx, int ly)
        {
            var x = lx + offset.X;
            var y = ly + offset.Y;
            Ensure(x >> 8, y >> 8, forWrite: false);
            if (_alpha == null) return false;
            return _alpha[((y & 255) << 8) | (x & 255)] != 0;
        }

        public void SetRun(int lx0, int lx1, int ly)
        {
            var y = ly + offset.Y;
            var x = lx0 + offset.X;
            var xEnd = lx1 + offset.X;
            while (x <= xEnd)
            {
                Ensure(x >> 8, y >> 8, forWrite: true);
                var rowBase = (y & 255) << 8;
                var runEnd = Math.Min(xEnd, (x | 255)); // 本 tile 內的結尾
                _alpha.AsSpan(rowBase | (x & 255), runEnd - x + 1).Fill(255);
                x = runEnd + 1;
            }
        }

        private void Ensure(int tx, int ty, bool forWrite)
        {
            if (tx == _tx && ty == _ty && (_writable || !forWrite) && (_alpha != null || !forWrite)) return;
            _tx = tx; _ty = ty;
            if (forWrite)
            {
                _alpha = mask.Mask.GetForWrite(new TileIndex(tx, ty)).Alpha;
                _writable = true;
            }
            else
            {
                _alpha = mask.Mask.GetForRead(new TileIndex(tx, ty))?.Alpha;
                _writable = _alpha != null;
            }
        }
    }
}
