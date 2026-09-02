using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Selections;

public enum SelectionCombineMode
{
    Replace,
    Add,
    Subtract,
    Intersect,
}

/// <summary>
/// 選取區：8-bit 覆蓋度遮罩（唯一權威）+ 顯示用輪廓路徑。
/// 無選取（session.Selection == null）= 全選語意。
/// 遮罩在 Document.SyncRoot 內讀寫；OutlinePath 發布後不得再變更
/// （render thread 可能正在讀；淘汰的路徑交給 GC 回收，勿手動 Dispose）。
///
/// **不變量：OutlinePath 一律沿像素邊界，且 OutlinePath.Bounds == Bounds。**
/// 螞蟻線畫的是 OutlinePath、把手框用的是 Bounds，兩者只要有半個像素的差，
/// 放大檢視時就會看到兩個對不齊的框。所以輪廓一律由柵格化後的遮罩重建，
/// 不保留原始的幾何路徑（那可能帶小數座標）。
/// </summary>
public sealed class SelectionMask
{
    public MaskSurface Mask { get; } = new();

    /// <summary>顯示螞蟻線用的輪廓（doc 座標，沿像素邊界）。發布後 immutable。</summary>
    public SKPath? OutlinePath { get; private set; }

    public SKRectI Bounds => Mask.Bounds;
    public bool IsEmpty => Mask.TileCount == 0;

    /// <summary>把矩形對齊到整數像素邊界（選取一律以整像素為單位）。</summary>
    public static SKRect SnapToPixels(SKRect rect)
    {
        var left = MathF.Round(Math.Min(rect.Left, rect.Right));
        var top = MathF.Round(Math.Min(rect.Top, rect.Bottom));
        var right = MathF.Round(Math.Max(rect.Left, rect.Right));
        var bottom = MathF.Round(Math.Max(rect.Top, rect.Bottom));
        return new SKRect(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }

    /// <summary>路徑的整數外接矩形（左上取下界、右下取上界）。</summary>
    private static SKRectI PixelBounds(SKRect r) =>
        new((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top),
            (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));

    /// <summary>取 (x,y) 的選取覆蓋度（doc 座標）。</summary>
    public byte CoverageAt(int x, int y)
    {
        var tile = Mask.GetForRead(TileIndex.FromPixel(x, y));
        if (tile == null) return 0;
        var rect = TileIndex.FromPixel(x, y).ToPixelRect();
        return tile.Alpha[(y - rect.Top) * MaskTile.Size + (x - rect.Left)];
    }

    /// <summary>
    /// 以幾何路徑建立遮罩（抗鋸齒邊 = 軟選取邊界）。
    /// 路徑會先裁切到 <paramref name="docBounds"/> —— 通常是畫布範圍；
    /// 貼上比畫布大的影像時傳畫布∪貼上矩形（浮動期間的選取允許超出畫布，落地才裁回）。
    /// </summary>
    public static SelectionMask FromPath(SKPath path, SKRectI docBounds)
    {
        var mask = new SelectionMask();
        using var clipped = ClipToBounds(path, docBounds);
        // 左上取下界、右下取上界 —— 舊版四個邊都用 Ceiling，左上會整整偏一格
        var rect = SKRectI.Intersect(PixelBounds(clipped.Bounds), docBounds);
        if (rect.Width <= 0 || rect.Height <= 0) return mask; // 完全在畫布外 = 沒有選取
        path = clipped;

        var info = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tile = mask.Mask.GetForWrite(idx);
            var tileRect = idx.ToPixelRect();
            unsafe
            {
                fixed (byte* ptr = tile.Alpha)
                {
                    using var surface = SKSurface.Create(info, (IntPtr)ptr, MaskTile.Size);
                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);
                    canvas.Translate(-tileRect.Left, -tileRect.Top);
                    // 夾到文件範圍
                    canvas.ClipRect(SKRect.Create(docBounds.Left, docBounds.Top, docBounds.Width, docBounds.Height));
                    canvas.DrawPath(path, paint);
                    canvas.Flush();
                }
            }
        }

        mask.NoteBounds(rect);
        // 輪廓一律由柵格化結果重建，才會沿像素邊界、且與 Bounds 完全一致
        mask.RebuildOutlineFromMask();
        return mask;
    }

    /// <summary>把路徑裁切到畫布矩形；回傳新路徑（呼叫者接手擁有權）。</summary>
    private static SKPath ClipToBounds(SKPath path, SKRectI docBounds)
    {
        var bounds = SKRectI.Ceiling(path.Bounds);
        if (docBounds.Contains(bounds)) return new SKPath(path); // 本來就在畫布內

        using var clip = new SKPath();
        clip.AddRect(SKRect.Create(docBounds.Left, docBounds.Top, docBounds.Width, docBounds.Height));
        return path.Op(clip, SKPathOp.Intersect) ?? new SKPath(path);
    }

    /// <summary>
    /// 從遮罩重建輪廓路徑（threshold 128），並把 Bounds 收緊成輪廓的實際範圍。
    /// 這是維持「螞蟻線與把手框對齊」不變量的地方 —— 兩者都源自這裡的結果。
    /// </summary>
    public void RebuildOutlineFromMask()
    {
        using var region = new SKRegion();
        foreach (var (idx, tile) in Mask.Tiles)
        {
            var rect = idx.ToPixelRect();
            for (var y = 0; y < MaskTile.Size; y++)
            {
                var row = tile.Alpha.AsSpan(y * MaskTile.Size, MaskTile.Size);
                var x = 0;
                while (x < MaskTile.Size)
                {
                    while (x < MaskTile.Size && row[x] < 128) x++;
                    if (x >= MaskTile.Size) break;
                    var start = x;
                    while (x < MaskTile.Size && row[x] >= 128) x++;
                    region.Op(new SKRectI(rect.Left + start, rect.Top + y, rect.Left + x, rect.Top + y + 1), SKRegionOperation.Union);
                }
            }
        }

        // 空區域（例如全選被自己減光）時 GetBoundaryPath 回 null
        var outline = region.GetBoundaryPath();
        OutlinePath = outline;
        // Bounds 收緊成輪廓的實際範圍（原本只能靠 ExtendBounds 放大，
        // 會比真正的覆蓋大一兩像素，放大檢視時框就對不齊）
        Mask.SetBounds(outline == null ? SKRectI.Empty : PixelBounds(outline.Bounds));
    }

    /// <summary>與另一個遮罩合併（other 會被讀取，不被修改）。</summary>
    public static SelectionMask? Combine(SelectionMask? current, SelectionMask incoming, SelectionCombineMode mode)
    {
        if (mode == SelectionCombineMode.Replace || current == null || current.IsEmpty)
        {
            return mode == SelectionCombineMode.Subtract && (current == null || current.IsEmpty)
                ? current // 從無選取中減去 = 不變
                : (mode == SelectionCombineMode.Intersect && (current == null || current.IsEmpty)
                    ? current
                    : incoming);
        }

        var result = current.Clone();
        switch (mode)
        {
            case SelectionCombineMode.Add:
                foreach (var (idx, src) in incoming.Mask.Tiles)
                {
                    var dst = result.Mask.GetForWrite(idx);
                    for (var i = 0; i < dst.Alpha.Length; i++)
                        if (src.Alpha[i] > dst.Alpha[i]) dst.Alpha[i] = src.Alpha[i];
                }
                result.NoteBounds(incoming.Bounds);
                break;

            case SelectionCombineMode.Subtract:
                foreach (var (idx, src) in incoming.Mask.Tiles)
                {
                    var dst = result.Mask.GetForRead(idx);
                    if (dst == null) continue;
                    for (var i = 0; i < dst.Alpha.Length; i++)
                        dst.Alpha[i] = (byte)(dst.Alpha[i] * (255 - src.Alpha[i]) / 255);
                }
                break;

            case SelectionCombineMode.Intersect:
                foreach (var (idx, dst) in result.Mask.Tiles)
                {
                    var src = incoming.Mask.GetForRead(idx);
                    if (src == null)
                    {
                        Array.Clear(dst.Alpha);
                        continue;
                    }
                    for (var i = 0; i < dst.Alpha.Length; i++)
                        if (src.Alpha[i] < dst.Alpha[i]) dst.Alpha[i] = src.Alpha[i];
                }
                break;
        }

        // 減法／交集可能把整格清空 —— 丟掉空格，IsEmpty 才會正確
        result.Mask.RemoveEmptyTiles();
        result.RebuildOutlineFromMask();
        return result;
    }

    /// <summary>
    /// 把選取範圍縮放/平移到新的矩形（拖把手用）。
    /// 以輪廓路徑做變換後重新柵格化，所以矩形、套索、魔術棒的選取都適用。
    /// </summary>
    public SelectionMask? TransformedTo(SKRect targetRect, SKRectI docBounds)
    {
        var src = Bounds;
        if (src.Width <= 0 || src.Height <= 0) return null;
        if (targetRect.Width < 1 || targetRect.Height < 1) return null;
        if (OutlinePath is not { } outline) return null;

        var matrix = SKMatrix.CreateScaleTranslation(
            targetRect.Width / src.Width,
            targetRect.Height / src.Height,
            targetRect.Left - src.Left * (targetRect.Width / src.Width),
            targetRect.Top - src.Top * (targetRect.Height / src.Height));

        using var transformed = new SKPath();
        outline.Transform(matrix, transformed);
        return FromPath(transformed, docBounds);
    }

    /// <summary>
    /// 回傳裁切到 <paramref name="docBounds"/> 內的版本；已在範圍內就回傳自身。
    /// 唯一會產生超出畫布之遮罩的是「貼上比畫布大的影像」，落地時走這裡裁回。
    /// </summary>
    public SelectionMask ClippedTo(SKRectI docBounds)
    {
        if (IsEmpty || docBounds.Contains(Bounds) || OutlinePath is not { } outline) return this;
        return FromPath(outline, docBounds);
    }

    /// <summary>從既有的遮罩面建立選取（幾何變換後用），輪廓由遮罩重建。</summary>
    public static SelectionMask? FromMaskSurface(MaskSurface mask)
    {
        if (mask.TileCount == 0) return null;
        var result = new SelectionMask();
        foreach (var (idx, tile) in mask.Tiles)
        {
            var dst = result.Mask.GetForWrite(idx);
            tile.Alpha.CopyTo(dst.Alpha, 0);
        }
        result.Mask.ExtendBounds(mask.Bounds);
        result.RebuildOutlineFromMask();
        return result;
    }

    public SelectionMask Clone()
    {
        var clone = new SelectionMask();
        foreach (var (idx, tile) in Mask.Tiles)
        {
            var dst = clone.Mask.GetForWrite(idx);
            tile.Alpha.CopyTo(dst.Alpha, 0);
        }
        clone.NoteBounds(Bounds);
        clone.OutlinePath = OutlinePath != null ? new SKPath(OutlinePath) : null;
        return clone;
    }

    private void NoteBounds(SKRectI rect) => Mask.ExtendBounds(rect);
}
