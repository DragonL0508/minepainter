using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 直接在 GPU 上把圖層樹畫出來（不經過 CPU 合成器）。
///
/// **現有路徑**：合成器 worker 在 CPU 把所有圖層混成一張張 tile、效果堆疊也在 CPU 逐像素算，
/// 算完才上傳成貼圖給 GPU 貼上去。GPU 幾乎閒著（實測一幀 0.6 ms），CPU 那邊一次上百毫秒 ——
/// 「手勢中畫面跟不上」的根就在這裡。
///
/// **這條路徑**：每幀直接走圖層樹，把每層的 tile 當貼圖畫上去，混合／不透明度交給 GPU，
/// 效果堆疊能翻成 Skia 濾鏡的就交給 GPU 算（見 <see cref="GpuEffectFilters"/>）。
/// CPU 合成器仍然是匯出與離線路徑的唯一真相，也是這條路處理不了時的退路。
///
/// **處理不了就整份退回**（回傳 false，呼叫端畫原本的 tile）：進行中的筆劃／浮動內容／
/// 拖曳覆疊／變形覆疊、調整圖層。那些都牽涉合成器內部的狀態，等這條路徑站穩再逐一接手。
/// </summary>
public sealed class GpuLayerRenderer : IDisposable
{
    /// <summary>每一格 tile 的 GPU 貼圖（key＝tile 索引；靠 Tile.Version 判斷要不要重建）。</summary>
    private sealed class LayerImages : IDisposable
    {
        public readonly Dictionary<TileIndex, (int Version, SKImage Image)> Tiles = new();

        public void Dispose()
        {
            foreach (var (_, image) in Tiles.Values) image.Dispose();
            Tiles.Clear();
        }
    }

    private readonly Dictionary<Guid, LayerImages> _images = new();
    private readonly Dictionary<Guid, (IReadOnlyList<LayerEffect> Effects, SKImageFilter? Filter)> _filters = new();

    /// <summary>診斷：上一幀畫了幾格、用了幾個 GPU 濾鏡。</summary>
    public int LastTiles { get; private set; }
    public int LastFilters { get; private set; }

    /// <summary>
    /// 試著畫。回傳 false＝這份文件目前的狀態這條路處理不了，呼叫端請走原本的 tile 路徑。
    /// 必須在 render thread、Document.SyncRoot 內呼叫。
    /// </summary>
    public bool TryDraw(SKCanvas canvas, EditorSession session, SKRectI visibleDoc)
    {
        if (!CanHandle(session)) return false;
        LastTiles = 0;
        LastFilters = 0;
        DrawGroup(canvas, session.Document.Root, visibleDoc);
        return true;
    }

    /// <summary>這條路徑還沒接手的狀態：有任何一個就整份退回原本的合成器。</summary>
    private static bool CanHandle(EditorSession session)
    {
        if (session.StrokeBuffer.IsActive) return false;      // 進行中的筆劃
        if (session.Floating != null) return false;           // 浮動內容
        if (session.LayerOverlay != null) return false;       // 圖層拖曳覆疊
        if (session.ElementOverlay != null) return false;     // 物件拖曳覆疊
        if (session.Transform?.Overlay != null) return false; // 變形手勢覆疊
        if (session.Ghost != null) return false;              // 落地殘影

        foreach (var node in session.Document.Descendants())
        {
            if (node is AdjustmentLayer) return false; // 調整圖層要拿「下方已累積的結果」，之後再接
        }
        return true;
    }

    private void DrawGroup(SKCanvas canvas, GroupLayer group, SKRectI visibleDoc)
    {
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;
            switch (child)
            {
                case RasterLayer raster:
                    DrawRaster(canvas, raster, visibleDoc);
                    break;
                case GroupLayer nested:
                    DrawNestedGroup(canvas, nested, visibleDoc);
                    break;
            }
        }
    }

    private void DrawNestedGroup(SKCanvas canvas, GroupLayer group, SKRectI visibleDoc)
    {
        var filter = FilterFor(group);
        var isolate = group.Opacity < 1f || group.BlendMode != BlendMode.Normal || filter != null;
        if (isolate)
        {
            using var paint = LayerPaint(group, filter);
            canvas.SaveLayer(paint);
        }
        DrawGroup(canvas, group, visibleDoc);
        if (isolate) canvas.Restore();
    }

    private void DrawRaster(SKCanvas canvas, RasterLayer raster, SKRectI visibleDoc)
    {
        var filter = FilterFor(raster);

        // 效果能交給 GPU 就畫「原始內容 + 濾鏡」；否則用 CPU 算好的那份（DisplaySurface）。
        var source = filter != null ? raster.Surface : raster.DisplaySurface;
        var elementsInSource = filter == null && raster.EffectsRendered; // CPU 快取已含物件

        var isolate = raster.Opacity < 1f || raster.BlendMode != BlendMode.Normal || filter != null;
        if (isolate)
        {
            using var paint = LayerPaint(raster, filter);
            canvas.SaveLayer(paint);
        }

        DrawTiles(canvas, raster, source, visibleDoc, isolate ? 1f : raster.Opacity);
        if (!elementsInSource)
        {
            foreach (var element in raster.Elements)
            {
                if (element.Id == raster.HiddenElementId) continue;
                element.Render(canvas);
            }
        }

        if (isolate) canvas.Restore();
    }

    private SKPaint LayerPaint(LayerNode node, SKImageFilter? filter) => new()
    {
        Color = new SKColor(255, 255, 255, (byte)(node.Opacity * 255)),
        BlendMode = node.BlendMode.ToSkia(),
        ImageFilter = filter,
    };

    private void DrawTiles(SKCanvas canvas, RasterLayer raster, TileSurface surface, SKRectI visibleDoc, float opacity)
    {
        var offset = surface == raster.DisplaySurface && raster.EffectsRendered
            ? raster.EffectOffset
            : raster.Offset;

        // 只畫看得到的那幾格
        var layerRect = new SKRectI(
            visibleDoc.Left - offset.X, visibleDoc.Top - offset.Y,
            visibleDoc.Right - offset.X, visibleDoc.Bottom - offset.Y);

        using var paint = opacity >= 1f
            ? null
            : new SKPaint { Color = new SKColor(255, 255, 255, (byte)(opacity * 255)) };

        var cache = Images(raster.Id);
        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = surface.GetTileForRead(idx);
            if (tile == null) continue;
            var image = ImageFor(cache, idx, tile);
            if (image == null) continue;
            var rect = idx.ToPixelRect();
            canvas.DrawImage(image, rect.Left + offset.X, rect.Top + offset.Y, paint);
            LastTiles++;
        }
    }

    private LayerImages Images(Guid layerId)
    {
        if (_images.TryGetValue(layerId, out var cache)) return cache;
        cache = new LayerImages();
        _images[layerId] = cache;
        return cache;
    }

    /// <summary>這一格的貼圖；內容版本變了就重建（Skia 會沿用同一個 SKImage 的貼圖）。</summary>
    private static SKImage? ImageFor(LayerImages cache, TileIndex idx, Tile tile)
    {
        if (cache.Tiles.TryGetValue(idx, out var entry))
        {
            if (entry.Version == tile.Version) return entry.Image;
            entry.Image.Dispose();
            cache.Tiles.Remove(idx);
        }
        using var pixmap = tile.AsPixmap();
        // 複製一份：tile 的記憶體會被繼續改寫，貼圖不能指著它
        var image = SKImage.FromPixelCopy(pixmap);
        if (image == null) return null;
        cache.Tiles[idx] = (tile.Version, image);
        return image;
    }

    /// <summary>這層的 GPU 濾鏡（效果清單沒換就沿用；翻不出來是 null）。</summary>
    private SKImageFilter? FilterFor(LayerNode node)
    {
        if (!node.HasActiveEffects) return null;
        if (_filters.TryGetValue(node.Id, out var cached) && ReferenceEquals(cached.Effects, node.Effects))
            return cached.Filter;

        cached.Filter?.Dispose();
        var filter = GpuEffectFilters.Build(node.Effects);
        _filters[node.Id] = (node.Effects, filter);
        if (filter != null) LastFilters++;
        return filter;
    }

    public void Dispose()
    {
        foreach (var cache in _images.Values) cache.Dispose();
        _images.Clear();
        foreach (var (_, filter) in _filters.Values) filter?.Dispose();
        _filters.Clear();
    }
}
