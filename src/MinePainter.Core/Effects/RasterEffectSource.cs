using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>效果運算的不可變來源：鎖內只取得 COW tile 與物件引用，複製像素／點陣化在鎖外執行。</summary>
internal sealed class RasterEffectSource : IDisposable
{
    private readonly TileSnapshot _tiles;
    private readonly VectorElement[] _elements;
    private readonly SKRectI _region;
    private readonly SKPointI _offset;

    private RasterEffectSource(TileSnapshot tiles, VectorElement[] elements, SKRectI region, SKPointI offset)
        => (_tiles, _elements, _region, _offset) = (tiles, elements, region, offset);

    /// <summary>呼叫端持有 Document.SyncRoot；隱藏狀態與位移必須與 tile 屬於同一份快照。</summary>
    public static RasterEffectSource Capture(RasterLayer layer, SKRectI region)
    {
        var elements = layer.Elements.Where(e => !layer.IsElementHidden(e.Id)).ToArray();
        return new RasterEffectSource(layer.Surface.Snapshot(region), elements, region, layer.Offset);
    }

    /// <summary>只讀取已固定的來源，不存取仍在編輯中的圖層。</summary>
    public unsafe uint[] Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rect = _region;
        var pixels = new uint[Math.Max(0, rect.Width * rect.Height)];
        foreach (var (idx, tile) in _tiles.Tiles)
        {
            ct.ThrowIfCancellationRequested();
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            var src = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = src + (y - tileRect.Top) * Tile.Size + inter.Left - tileRect.Left;
                var to = (y - rect.Top) * rect.Width + inter.Left - rect.Left;
                new ReadOnlySpan<uint>(row, inter.Width).CopyTo(pixels.AsSpan(to, inter.Width));
            }
        }
        if (_elements.Length == 0 || rect.IsEmpty) return pixels;
        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, (IntPtr)ptr, info.RowBytes);
            if (surface == null) return pixels;
            var canvas = surface.Canvas;
            canvas.Translate(-rect.Left - _offset.X, -rect.Top - _offset.Y);
            var docRect = new SKRectI(rect.Left + _offset.X, rect.Top + _offset.Y,
                rect.Right + _offset.X, rect.Bottom + _offset.Y);
            foreach (var element in _elements)
            {
                ct.ThrowIfCancellationRequested();
                if (element.Bounds.IntersectsWith(docRect)) element.Render(canvas);
            }
            canvas.Flush();
        }
        return pixels;
    }

    public void Dispose() => _tiles.Dispose();
}
