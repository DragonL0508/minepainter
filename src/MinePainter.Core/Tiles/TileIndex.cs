using SkiaSharp;

namespace MinePainter.Core.Tiles;

/// <summary>tile 格座標（文件像素座標 / 256）。</summary>
public readonly record struct TileIndex(int X, int Y)
{
    /// <summary>此 tile 覆蓋的文件像素範圍。</summary>
    public SKRectI ToPixelRect() => SKRectI.Create(X * Tile.Size, Y * Tile.Size, Tile.Size, Tile.Size);

    public static TileIndex FromPixel(int px, int py) =>
        new((int)Math.Floor(px / (double)Tile.Size), (int)Math.Floor(py / (double)Tile.Size));

    /// <summary>枚舉與像素矩形相交的所有 tile 格。</summary>
    public static IEnumerable<TileIndex> CoveringRect(SKRectI rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) yield break;
        var t0 = FromPixel(rect.Left, rect.Top);
        var t1 = FromPixel(rect.Right - 1, rect.Bottom - 1);
        for (var y = t0.Y; y <= t1.Y; y++)
        for (var x = t0.X; x <= t1.X; x++)
            yield return new TileIndex(x, y);
    }

    public override string ToString() => $"({X},{Y})";
}
