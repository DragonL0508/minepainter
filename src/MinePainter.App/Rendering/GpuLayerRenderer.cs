using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 直接在 GPU 上把圖層樹畫出來（不經過 CPU 合成器）。
///
/// **現有路徑**：合成器 worker 在 CPU 把所有圖層混成一張張 tile、效果堆疊也在 CPU 逐像素算，
/// 算完才上傳成貼圖給 GPU 貼上去。GPU 幾乎閒著（實測一幀 0.6 ms），CPU 那邊一次上百毫秒 ——
/// 「手勢中畫面跟不上」的根就在這裡。
///
/// **這條路徑**：每幀直接走圖層樹，把每層的 tile 當貼圖畫上去，混合／不透明度交給 GPU。
/// 效果堆疊仍舊由 CPU 算（DisplaySurface）—— 畫面看到的與匯出得到的因此永遠是同一份。
/// CPU 合成器仍然是匯出與離線路徑的唯一真相，也是這條路處理不了時的退路。
///
/// **一律啟用**；真的遇到處理不了的狀態就整份退回（<see cref="TryDraw"/> 回傳 false，
/// 呼叫端改畫合成器的 tile）。目前沒有這樣的狀態 —— 進行中的筆劃、浮動內容、拖曳與變形手勢、
/// 落地殘影、調整圖層都已經接手 —— 但退路留著，之後加新東西時才有地方站。
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
    private readonly RotatedTextCache _rotatedText = new();
    private readonly Dictionary<Guid, (Core.Adjustments.IAdjustment Adjustment, SKColorFilter Filter)> _adjustments = new();

    /// <summary>診斷：上一幀畫了幾格。</summary>
    public int LastTiles { get; private set; }

    /// <summary>診斷／測試：上一幀有幾個文字物件是貼快照畫的（見 <see cref="RotatedTextCache"/>）。</summary>
    public int LastCachedTextDraws { get; private set; }

    /// <summary>
    /// 試著畫。回傳 false＝這份文件目前的狀態這條路處理不了，呼叫端請走原本的 tile 路徑。
    /// 必須在 render thread、Document.SyncRoot 內呼叫。
    /// </summary>
    public bool TryDraw(SKCanvas canvas, EditorSession session, SKRectI visibleDoc)
    {
        if (!CanHandle(session)) return false;
        LastTiles = 0;
        LastCachedTextDraws = 0;
        DrawGroup(canvas, session, session.Document.Root, visibleDoc);
        return true;
    }

    /// <summary>這條路徑還沒接手的狀態：有任何一個就整份退回原本的合成器。</summary>
    private static bool CanHandle(EditorSession session)
    {
        // 變形手勢的覆疊由 CanvasDrawOperation.DrawTransformOverlay 另外畫（在所有圖層之上，
        // 而覆疊本來就只在「上面沒有看得見的東西」時才成立），這裡照常畫圖層樹即可 ——
        // 被變形的那層此刻沒有像素（手勢開始時已經拆下來），畫出來也是空的。

        return true;
    }

    /// <summary>
    /// 手勢中的物件覆疊（「物件＋效果」的快照，跟著滑鼠走／轉／縮）。
    /// 舊路徑把它畫在所有圖層之上；這裡照層序畫在它自己那一層的位置，上面有東西也不會被蓋錯。
    /// </summary>
    private static void DrawElementOverlay(SKCanvas canvas, EditorSession.ElementDragOverlay overlay)
    {
        var rect = overlay.CurrentRect; // 只讀一次：UI thread 正在改它
        var rotation = overlay.Rotation;
        var pivot = overlay.Pivot;      // 物件真正的旋轉軸心，不是這張圖的中心
        var image = overlay.Image!;
        var transformed = rotation != 0 || image.Width != overlay.Bounds.Width ||
                          rect.Width != overlay.Bounds.Width || rect.Height != overlay.Bounds.Height;
        using var paint = new SKPaint
        {
            FilterQuality = transformed ? SKFilterQuality.Low : SKFilterQuality.None,
            IsAntialias = transformed,
        };
        if (rotation != 0)
        {
            canvas.Save();
            canvas.RotateDegrees(rotation, pivot.X, pivot.Y);
        }
        canvas.DrawImage(image, rect, paint);
        if (rotation != 0) canvas.Restore();
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
        // 整組套過效果的那份已經算好了就直接畫它（外框／陰影包住整組，而不是每個子層各一份）
        if (group.EffectsRendered)
        {
            DrawSurface(canvas, GroupImages(group), group.FxCache.Surface, SKPointI.Empty, visibleDoc,
                group.Opacity, group.BlendMode);
            return;
        }

        var isolate = group.Opacity < 1f || group.BlendMode != BlendMode.Normal;
        if (isolate)
        {
            using var paint = LayerPaint(group, null);
            canvas.SaveLayer(paint);
        }
        DrawGroup(canvas, session, group, visibleDoc);
        if (isolate) canvas.Restore();
    }

    private void DrawRaster(SKCanvas canvas, EditorSession session, RasterLayer raster, SKRectI visibleDoc)
    {
        // 效果一律拿 CPU 算好的那份（DisplaySurface）——「畫面看到的」與「匯出得到的」是同一份。
        // 曾經試過把效果翻成 Skia 濾鏡交給 GPU 算，但 Skia 的 dilate 是方形核心，
        // 而外框走的是精確歐氏距離場：15px 的外框會把中文筆畫糊成一塊塊方塊（使用者回報）。
        var source = raster.DisplaySurface;
        var elementsInSource = raster.EffectsRendered; // CPU 快取已含物件

        var stroke = session.StrokeBuffer;
        var strokeHere = stroke.IsActive && stroke.TargetLayerId == raster.Id && !stroke.DirtyBounds.IsEmpty;
        var floating = session.Floating;
        var floatingHere = floating != null && floating.LayerId == raster.Id;

        // 橡皮擦的 DstOut 一定要在隔離層裡擦，否則會擦穿到下方圖層
        var isolate = raster.Opacity < 1f || raster.BlendMode != BlendMode.Normal ||
                      (strokeHere && stroke.IsEraser);
        if (isolate)
        {
            using var paint = LayerPaint(raster, null);
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
        if (session.ElementOverlay is { Image: not null } drag && ReferenceEquals(drag.Layer, raster))
            DrawElementOverlay(canvas, drag);

        if (!elementsInSource)
        {
            // 變形手勢進行中：文字物件每幀都被換成新角度的實例（TransformSession.UpdateElements），
            // 每幀因此都要重排版、重描邊、重模糊一次 —— 走快照那條路（見 RotatedTextCache）。
            var gesture = session.Transform is { GestureActive: true };
            foreach (var element in raster.Elements)
            {
                if (raster.IsElementHidden(element.Id)) continue;
                if (gesture && element is TextElement text && _rotatedText.TryDraw(canvas, text))
                {
                    LastCachedTextDraws++;
                    continue;
                }
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
        DrawSurface(canvas, Images(raster.Id), surface, offset, visibleDoc, opacity, BlendMode.Normal);
    }

    /// <summary>把一張 tile surface 畫上去（只畫看得到的那幾格；每格一張貼圖，靠 Tile.Version 判斷要不要重傳）。</summary>
    private void DrawSurface(SKCanvas canvas, LayerImages cache, TileSurface surface, SKPointI offset,
        SKRectI visibleDoc, float opacity, BlendMode blend)
    {

        // 只畫看得到的那幾格
        var layerRect = new SKRectI(
            visibleDoc.Left - offset.X, visibleDoc.Top - offset.Y,
            visibleDoc.Right - offset.X, visibleDoc.Bottom - offset.Y);

        using var paint = opacity >= 1f && blend == BlendMode.Normal
            ? null
            : new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)(opacity * 255)),
                BlendMode = blend.ToSkia(),
            };

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

    private LayerImages GroupImages(GroupLayer group) => Images(group.Id);

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

    public void Dispose()
    {
        foreach (var cache in _images.Values) cache.Dispose();
        _images.Clear();
        foreach (var (_, filter) in _adjustments.Values) filter.Dispose();
        _adjustments.Clear();
        _rotatedText.Dispose();
    }
}

/// <summary>
/// 手勢中「轉起來的文字」的快照快取。
///
/// 旋轉手勢每一幀都是一個新角度，而 Skia 的字形遮罩是按矩陣快取的 —— 換個角度就等於整份
/// 重新點陣化一次，帶外光暈的字還要多描粗一圈再模糊。實測 120px、帶光暈／陰影／兩層外框的
/// 一行字：角度每幀都變是 12.7 ms/幀，角度不變只要 2.1 ms/幀，差的那 10 ms 全是重做遮罩。
///
/// 這裡把「同一個字、只是還沒轉」的那一份點陣化起來，之後每幀只是換個角度貼同一張圖。
/// 幾何是完全等價的：<see cref="TextElement.Render"/> 的外層變換就是
/// 位移(Position) → 旋轉(Rotation) → 其餘，所以「未旋轉、擺在原點的那份圖」
/// 繞 Position 轉 Rotation 度，就是原本會畫出來的東西（差別只在多了一次重取樣）。
///
/// 只在手勢中用：放開之後照舊走 <see cref="TextElement.Render"/> 精算，匯出與合成器更完全碰不到。
/// 只有 render thread 會碰它。
/// </summary>
public sealed class RotatedTextCache : IDisposable
{
    /// <summary>一個文字物件的快照（key＝「未旋轉、擺在原點」的樣子）。</summary>
    private sealed class Entry
    {
        // 連續兩幀看到同一份「未旋轉的樣子」才值得點陣化：旋轉手勢每幀都一樣（只有角度與
        // 位置在變，兩者都不在 key 裡），縮放手勢則每幀都不一樣 —— 後者點陣化只是白做一次工。
        public TextElement? Pending;
        public float PendingScale;

        public TextElement? Key;
        public float Scale;
        public SKImage? Image;
        public SKRectI Bounds;
        public long Used;

        public void Drop()
        {
            Image?.Dispose();
            Image = null;
            Key = null;
        }
    }

    /// <summary>同時最多留幾份（一層有好幾個文字物件一起被變形時，彼此不要互相把對方擠掉）。</summary>
    private const int Capacity = 4;

    /// <summary>單張快照的像素上限（超過就不快取，照舊每幀精算）。</summary>
    private const long MaxPixels = 24L * 1024 * 1024;

    private readonly Dictionary<Guid, Entry> _entries = new();
    private long _clock;

    public bool TryDraw(SKCanvas canvas, TextElement text)
    {
        if (Math.Abs(text.Rotation) < 0.01f) return false;       // 沒轉就沒有這個問題
        if (text.Deform is { IsIdentity: false }) return false;  // 透視／彎曲自己有一份快取
        if (string.IsNullOrEmpty(text.Text)) return false;

        var scale = DeviceScale(canvas);
        if (scale <= 0f) return false;

        // key＝「這個字未旋轉、擺在原點」的樣子：旋轉手勢中它逐幀不變（位置與角度都不在裡面）
        var flat = text with { Rotation = 0f, Position = SKPoint.Empty };
        var entry = EntryFor(text.Id);

        if (entry.Image == null || entry.Key != flat || entry.Scale != scale)
        {
            if (entry.Pending != flat || entry.PendingScale != scale)
            {
                entry.Pending = flat;
                entry.PendingScale = scale;
                return false; // 這一幀先照舊畫；下一幀還是同一份才點陣化
            }
            if (!Rasterize(entry, flat, scale)) return false;
        }

        entry.Used = ++_clock;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
        canvas.Save();
        canvas.Translate(text.Position.X, text.Position.Y);
        canvas.RotateDegrees(text.Rotation);
        canvas.Scale(1f / entry.Scale, 1f / entry.Scale);
        canvas.DrawImage(entry.Image, entry.Bounds.Left * entry.Scale, entry.Bounds.Top * entry.Scale, paint);
        canvas.Restore();
        return true;
    }

    private Entry EntryFor(Guid id)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        if (_entries.Count >= Capacity)
        {
            // 最久沒用到的那份讓位（同一層一次轉一堆文字時才會用到）
            var oldest = Guid.Empty;
            var oldestUsed = long.MaxValue;
            foreach (var (key, e) in _entries)
            {
                if (e.Used >= oldestUsed) continue;
                oldestUsed = e.Used;
                oldest = key;
            }
            _entries[oldest].Drop();
            _entries.Remove(oldest);
        }
        entry = new Entry();
        _entries[id] = entry;
        return entry;
    }

    /// <summary>畫布目前的縮放（忽略旋轉），量化成 1/8 —— 縮放值抖一點不要害 key 一直失效。</summary>
    private static float DeviceScale(SKCanvas canvas)
    {
        var m = canvas.TotalMatrix;
        var det = Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY);
        if (det <= 0f || !float.IsFinite(det)) return 0f;
        var scale = MathF.Sqrt(det);
        if (scale < 1f / 16f || scale > 8f) return 0f; // 太小沒必要、太大快照會爆
        return MathF.Round(scale * 8f) / 8f;
    }

    private static bool Rasterize(Entry entry, TextElement flat, float scale)
    {
        var bounds = flat.Bounds; // 未旋轉、擺在原點時的 doc 外框（含效果外擴）
        var w = (int)MathF.Ceiling(bounds.Width * scale);
        var h = (int)MathF.Ceiling(bounds.Height * scale);
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) return false;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface == null) return false;
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(scale);
        c.Translate(-bounds.Left, -bounds.Top);
        flat.Render(c);
        c.Flush();

        entry.Image?.Dispose();
        entry.Image = surface.Snapshot();
        entry.Key = flat;
        entry.Scale = scale;
        entry.Bounds = bounds;
        return true;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Drop();
        _entries.Clear();
    }
}
