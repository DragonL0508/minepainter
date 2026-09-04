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
public sealed unsafe class GpuLayerRenderer : IDisposable
{
    /// <summary>每一格 tile 的 GPU 貼圖（key＝tile 索引；靠 Tile.Version 判斷要不要重建）。</summary>
    private sealed class LayerImages : IDisposable
    {
        public readonly Dictionary<TileIndex, (long Version, SKImage Image)> Tiles = new();

        public void Dispose()
        {
            foreach (var (_, image) in Tiles.Values) image.Dispose();
            Tiles.Clear();
        }
    }

    private readonly Dictionary<Guid, LayerImages> _images = new();
    private readonly Dictionary<Guid, (IReadOnlyList<LayerEffect> Effects, SKImageFilter? Filter)> _filters = new();
    private readonly Dictionary<Guid, (Core.Adjustments.IAdjustment Adjustment, SKColorFilter Filter)> _adjustments = new();

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
        DrawGroup(canvas, session, session.Document.Root, visibleDoc);
        return true;
    }

    /// <summary>這條路徑還沒接手的狀態：有任何一個就整份退回原本的合成器。</summary>
    private static bool CanHandle(EditorSession session)
    {
        // 變形手勢的覆疊由 CanvasDrawOperation.DrawTransformOverlay 另外畫（在所有圖層之上，
        // 而覆疊本來就只在「上面沒有看得見的東西」時才成立），這裡照常畫圖層樹即可 ——
        // 被變形的那層此刻沒有像素（手勢開始時已經拆下來），畫出來也是空的。

        // 拖曳中的物件，若那層的效果進不了 GPU 濾鏡，畫面上拿的會是 CPU 效果快取 ——
        // 而那份裡面已經烙著物件的「原位置」，拆不開，只好整份退回舊路。
        if (session.ElementOverlay is { } dragging && dragging.Layer.EffectsRendered &&
            !GpuEffectFilters.CanTranslate(dragging.Layer.Effects))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 拖曳中的物件：**不用快照**，直接把原件套上手勢的變換畫出來。
    ///
    /// 快照那套（先把「物件＋效果」拍成一張圖、手勢中只挪那張圖）本來是為了閃避
    /// 「每動一步就要重算效果堆疊」的 CPU 成本。效果現在是 GPU 濾鏡，那個成本沒了 ——
    /// 直接畫真正的物件，外框／陰影跟著即時算，手勢中看到的就是最終結果。
    /// </summary>
    private static SKMatrix OverlayMatrix(EditorSession.ElementDragOverlay overlay)
    {
        var b = overlay.Bounds;
        var cur = overlay.CurrentRect;
        var sx = b.Width > 0 ? cur.Width / b.Width : 1f;
        var sy = b.Height > 0 ? cur.Height / b.Height : 1f;
        var m = SKMatrix.CreateScaleTranslation(sx, sy, cur.Left - b.Left * sx, cur.Top - b.Top * sy);
        var rotation = overlay.Rotation;
        if (rotation != 0)
            m = SKMatrix.Concat(SKMatrix.CreateRotationDegrees(rotation, cur.MidX, cur.MidY), m);
        return m;
    }

    private void DrawGroup(SKCanvas canvas, EditorSession session, GroupLayer group, SKRectI visibleDoc)
        => DrawRange(canvas, session, group.Children, group.Children.Count, visibleDoc);

    /// <summary>
    /// 畫這個群組的前 <paramref name="count"/ > 個子層。
    ///
    /// 調整圖層作用在「同群組內、它下方的合成結果」上（與 CPU 合成器同語意）。GPU 這邊的做法是
    /// 把下方那一段包進一個 SaveLayer、收起來的時候套色彩濾鏡 —— 收起來那一刻濾鏡吃到的正好是
    /// 那一段的合成結果。由最上面那個調整圖層往下遞迴，巢狀的調整層自然就一層層套回去。
    /// </summary>
    private void DrawRange(SKCanvas canvas, EditorSession session, IReadOnlyList<LayerNode> children,
        int count, SKRectI visibleDoc)
    {
        var at = -1;
        for (var i = count - 1; i >= 0; i--)
        {
            if (children[i] is AdjustmentLayer { IsVisible: true } a && a.Opacity > 0) { at = i; break; }
        }

        if (at >= 0)
        {
            var adjustment = (AdjustmentLayer)children[at];
            var full = adjustment.Opacity >= 1f;
            // 不透明度＜1 ＝ 調整強度：先畫一份沒套到的底，再把套過的疊上去
            if (!full) DrawRange(canvas, session, children, at, visibleDoc);
            using var paint = new SKPaint
            {
                ColorFilter = AdjustmentFilter(adjustment),
                Color = SKColors.White.WithAlpha((byte)(adjustment.Opacity * 255)),
            };
            canvas.SaveLayer(paint);
            DrawRange(canvas, session, children, at, visibleDoc);
            canvas.Restore();
        }

        for (var i = at + 1; i < count; i++)
        {
            var child = children[i];
            if (!child.IsVisible || child.Opacity <= 0) continue;
            switch (child)
            {
                case RasterLayer raster:
                    DrawRaster(canvas, session, raster, visibleDoc);
                    break;
                case GroupLayer nested:
                    DrawNestedGroup(canvas, session, nested, visibleDoc);
                    break;
            }
        }
    }

    /// <summary>這個調整圖層的色彩濾鏡（參數沒換就沿用 —— 曲線／色階每次都要建 256 格表）。</summary>
    private SKColorFilter AdjustmentFilter(AdjustmentLayer layer)
    {
        if (_adjustments.TryGetValue(layer.Id, out var cached) &&
            ReferenceEquals(cached.Adjustment, layer.Adjustment))
        {
            return cached.Filter;
        }
        cached.Filter?.Dispose();
        var filter = layer.Adjustment.CreateColorFilter();
        _adjustments[layer.Id] = (layer.Adjustment, filter);
        return filter;
    }

    private void DrawNestedGroup(SKCanvas canvas, EditorSession session, GroupLayer group, SKRectI visibleDoc)
    {
        var filter = FilterFor(group);
        var isolate = group.Opacity < 1f || group.BlendMode != BlendMode.Normal || filter != null;
        if (isolate)
        {
            using var paint = LayerPaint(group, filter);
            canvas.SaveLayer(paint);
        }
        DrawGroup(canvas, session, group, visibleDoc);
        if (isolate) canvas.Restore();
    }

    private void DrawRaster(SKCanvas canvas, EditorSession session, RasterLayer raster, SKRectI visibleDoc)
    {
        var filter = FilterFor(raster);

        // 效果能交給 GPU 就畫「原始內容 + 濾鏡」；否則用 CPU 算好的那份（DisplaySurface）。
        var source = filter != null ? raster.Surface : raster.DisplaySurface;
        var elementsInSource = filter == null && raster.EffectsRendered; // CPU 快取已含物件

        var stroke = session.StrokeBuffer;
        var strokeHere = stroke.IsActive && stroke.TargetLayerId == raster.Id && !stroke.DirtyBounds.IsEmpty;
        var floating = session.Floating;
        var floatingHere = floating != null && floating.LayerId == raster.Id;

        // 橡皮擦的 DstOut 一定要在隔離層裡擦，否則會擦穿到下方圖層
        var isolate = raster.Opacity < 1f || raster.BlendMode != BlendMode.Normal || filter != null ||
                      (strokeHere && stroke.IsEraser);
        if (isolate)
        {
            using var paint = LayerPaint(raster, filter);
            canvas.SaveLayer(paint);
        }

        if (GestureItem(session, raster) is { } item)
        {
            // 變形手勢中的這一層：像素已經拆下來了，改用手勢矩陣把那張圖畫在**這個層序位置**
            // （舊路徑只能畫在所有圖層之上，所以上面一有東西就只好退回逐步蓋章）
            DrawGesture(canvas, session.Transform!.Overlay!, item);
        }
        else
        {
            DrawTiles(canvas, raster, source, visibleDoc, isolate ? 1f : raster.Opacity);
        }
        if (strokeHere) DrawStroke(canvas, stroke);
        if (floatingHere) floating!.DrawInto(canvas, preview: true);

        if (!elementsInSource)
        {
            var overlay = session.ElementOverlay;
            var dragging = overlay != null && ReferenceEquals(overlay.Layer, raster);
            foreach (var element in raster.Elements)
            {
                if (dragging && element.Id == overlay!.ElementId)
                {
                    // 手勢中的那個物件：原件套上手勢的變換直接畫（不用快照，效果即時跟著算）
                    var m = OverlayMatrix(overlay);
                    canvas.Save();
                    canvas.Concat(ref m);
                    element.Render(canvas);
                    canvas.Restore();
                    continue;
                }
                if (element.Id == raster.HiddenElementId) continue;
                element.Render(canvas);
            }
        }

        if (isolate) canvas.Restore();
    }

    /// <summary>這一層現在是不是變形手勢的一員（交接中的殘影不算 —— 那時像素已經蓋回層裡了）。</summary>
    private static (RasterLayer Layer, SKImage Image, SKRectI SrcBounds)? GestureItem(
        EditorSession session, RasterLayer raster)
    {
        if (session.Transform?.Overlay is not { HandingOver: false } overlay) return null;
        foreach (var item in overlay.Items)
        {
            if (ReferenceEquals(item.Layer, raster)) return item;
        }
        return null;
    }

    private static void DrawGesture(SKCanvas canvas, TransformSession.GestureOverlay overlay,
        (RasterLayer Layer, SKImage Image, SKRectI SrcBounds) item)
    {
        var m = overlay.Matrix;
        if (overlay.Warp is { } warp)
        {
            warp.Draw(canvas, item.Image, item.SrcBounds, m, SKFilterQuality.Low);
            return;
        }
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        canvas.Save();
        canvas.Concat(ref m);
        canvas.DrawImage(item.Image, item.SrcBounds.Left, item.SrcBounds.Top, paint);
        canvas.Restore();
    }

    /// <summary>進行中的筆劃：遮罩本身就是一張張 Alpha8 的圖，照 doc 座標貼上去即可。</summary>
    private static unsafe void DrawStroke(SKCanvas canvas, StrokeBuffer stroke)
    {
        using var paint = new SKPaint
        {
            Color = stroke.IsEraser
                ? SKColors.White.WithAlpha((byte)(stroke.Opacity * 255))
                : stroke.Color.WithAlpha((byte)(stroke.Color.Alpha * stroke.Opacity)),
            BlendMode = stroke.IsEraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
        };
        var info = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);
        foreach (var (idx, tile) in stroke.Mask.Tiles)
        {
            var rect = idx.ToPixelRect();
            fixed (byte* ptr = tile.Alpha)
            {
                using var image = SKImage.FromPixels(info, (IntPtr)ptr, MaskTile.Size);
                canvas.DrawImage(image, rect.Left, rect.Top, paint);
            }
        }
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
        foreach (var (_, filter) in _adjustments.Values) filter.Dispose();
        _adjustments.Clear();
    }
}
