using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 變形框 session（移動工具）：把作用中圖層或整個群組的像素與文字物件框起來
/// 移動／縮放／旋轉。與浮動選取同級的「進行中編輯」（IPendingEdit）。
///
/// 核心不變量：**整段 session 期間永遠以「開始時提起的原始像素 × 單一累積矩陣」重取樣** ——
/// 縮小再拉大不會糊；回到恰好原狀（identity）時直接還原快照，一個位元都不差。
/// 只有落地（<see cref="EditorSession.CommitTransform"/>）那一次才真正烙進圖層。
///
/// 效能分兩條路：
/// - 縮放/旋轉改變 → 全量重蓋章（拖曳中 Low、手勢結束補 High）。
/// - **純平移（尺寸與角度沒變）→ 不重蓋章**，位移放進各層的 Offset（蓋章的像素不動）——
///   大圖放大後拖著走的成本從「整片重取樣」變成「改一個位移值」；單一圖層時
///   呼叫端還能再接上拖曳覆疊快路徑（BeginLayerDrag，render thread 直接畫，零重合成）。
///
/// 開始時不動任何像素（零成本）；第一次偏離 identity 才清掉原像素改為蓋章。
/// 只在 UI thread 操作；像素/元素的讀寫都在 Document.SyncRoot 內。
/// </summary>
public sealed class TransformSession : IDisposable
{
    private sealed class Item
    {
        public required RasterLayer Layer;
        public required SKImage? Pixels;         // null = 該層沒有像素（可能只有文字）
        public required SKRectI SrcBounds;       // 像素內容的 doc 範圍（Pixels 的位置；Offset=Base 時）
        public required SKPointI BaseOffset;     // 開始時的圖層 Offset（平移位移疊在它之上）
        public required TileSnapshot Before;
        public required VectorElement[] StartElements;
        public SKRectI LastStamp;                // 目前蓋章的 doc 範圍（Offset=Base 基準；呈現位置再加 OffsetDelta）
    }

    /// <summary>單層內容的尺寸上限（單邊），與整層提起相同的保險。</summary>
    private const int MaxContentSide = 16384;

    private readonly Document _doc;
    private readonly List<Item> _items;
    private bool _disposed;

    // 蓋章狀態：目前圖層裡的像素是用哪組參數蓋出來的
    private (float Sx, float Sy, float Rot, float W, float H) _stampedParams;
    private SKPoint _stampedOrigin;  // 蓋章時 TargetRect 的左上（呈現位置 = 這裡 + OffsetDelta）
    private bool _stampedHigh;       // 蓋章已是最終品質（純平移的 None 視為無損）
    private bool _pixelsStamped;     // 已經動過像素（false = 圖層裡還是原始像素）

    public bool IsGroup { get; }

    /// <summary>開始時的內容外框（像素 ∪ 文字物件；doc 座標，可超出畫布）。</summary>
    public SKRect SourceRect { get; }

    /// <summary>目前的目標矩形（軸對齊；移動/縮放都改這個）。</summary>
    public SKRect TargetRect { get; set; }

    /// <summary>順時針旋轉角度（度），以 TargetRect 中心為軸。</summary>
    public float RotationDeg { get; set; }

    /// <summary>純平移累積的位移（各層 Offset = BaseOffset + 這個值）。</summary>
    public SKPointI OffsetDelta { get; private set; }

    /// <summary>單一圖層的 session 才能走拖曳覆疊快路徑。</summary>
    public RasterLayer? SoleLayer => _items.Count == 1 ? _items[0].Layer : null;

    /// <summary>
    /// 縮放/旋轉手勢期間 render thread 直接畫的預覽（null = 無）。
    /// 拖曳中只換 Matrix，一格 tile 都不重寫、不重合成 —— 大圖的縮放/旋轉才跟得上滑鼠。
    /// </summary>
    public sealed class GestureOverlay
    {
        public required (SKImage Image, SKRectI SrcBounds)[] Items { get; init; }
        public required SKMatrix Matrix { get; init; }

        /// <summary>手勢已結束、蓋章已寫入：等合成器把 <see cref="HandoverRegion"/> 畫完才收掉（不閃）。</summary>
        public required bool HandingOver { get; init; }

        public required SKRectI HandoverRegion { get; init; }
    }

    private volatile GestureOverlay? _overlay;
    private bool _gestureOverlay;      // 手勢覆疊進行中（像素已從合成結果拿掉）
    private bool _overlayEverPublished;

    /// <summary>render thread 每幀讀。</summary>
    public GestureOverlay? Overlay => _overlay;

    public bool IsIdentity =>
        TargetRect == SourceRect && Math.Abs(RotationDeg) < 0.01f;

    private TransformSession(Document doc, List<Item> items, SKRect sourceRect, bool isGroup)
    {
        _doc = doc;
        _items = items;
        SourceRect = sourceRect;
        TargetRect = sourceRect;
        IsGroup = isGroup;
        ResetStampStateToOriginal();
    }

    /// <summary>「原始像素」本身就是 identity 參數的無損蓋章 —— 純移動的 session 從頭到尾不必重蓋。</summary>
    private void ResetStampStateToOriginal()
    {
        _stampedParams = (1f, 1f, 0f, SourceRect.Width, SourceRect.Height);
        _stampedOrigin = new SKPoint(SourceRect.Left, SourceRect.Top);
        _stampedHigh = true;
        _pixelsStamped = false;
        OffsetDelta = SKPointI.Empty;
    }

    /// <summary>SourceRect → 目前狀態 的完整映射（縮放平移在前、旋轉在後）。</summary>
    public SKMatrix Matrix
    {
        get
        {
            var (sx, sy) = Scales;
            var m = SKMatrix.CreateScaleTranslation(sx, sy,
                TargetRect.Left - SourceRect.Left * sx,
                TargetRect.Top - SourceRect.Top * sy);
            if (Math.Abs(RotationDeg) > 0.01f)
            {
                m = SKMatrix.Concat(
                    SKMatrix.CreateRotationDegrees(RotationDeg, TargetRect.MidX, TargetRect.MidY), m);
            }
            return m;
        }
    }

    private (float Sx, float Sy) Scales => (
        SourceRect.Width > 0.5f ? TargetRect.Width / SourceRect.Width : 1f,
        SourceRect.Height > 0.5f ? TargetRect.Height / SourceRect.Height : 1f);

    /// <summary>
    /// 對作用中圖層（或群組 = 所有子孫點陣圖層）開始變形。
    /// 不動像素，只做快照；無內容或內容過大時回傳 null（reason 帶原因）。
    /// </summary>
    public static TransformSession? Begin(Document doc, LayerNode target, out string? reason)
    {
        reason = null;
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: Collect(g, layers); break;
            default:
                reason = "此圖層類型無法變形";
                return null;
        }

        var items = new List<Item>();
        SKRect? source = null;
        void Accumulate(SKRect r) =>
            source = source is { } a
                ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                    Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                : r;

        lock (doc.SyncRoot)
        {
            foreach (var layer in layers)
            {
                var content = layer.Surface.ExactContentBounds();
                var hasPixels = content.Width > 0 && content.Height > 0;
                if (hasPixels && (content.Width > MaxContentSide || content.Height > MaxContentSide))
                {
                    reason = "圖層內容過大，無法變形";
                    DisposeItems(items);
                    return null;
                }

                SKImage? pixels = null;
                var docRect = SKRectI.Empty;
                if (hasPixels)
                {
                    docRect = new SKRectI(
                        content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                        content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y);
                    var info = new SKImageInfo(docRect.Width, docRect.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info);
                    if (surface == null) continue;
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.Save();
                    surface.Canvas.Translate(-docRect.Left, -docRect.Top);
                    Selections.FloatingSelection.DrawLayerPixels(layer, surface.Canvas, docRect);
                    surface.Canvas.Restore();
                    surface.Canvas.Flush();
                    pixels = surface.Snapshot();
                    Accumulate(new SKRect(docRect.Left, docRect.Top, docRect.Right, docRect.Bottom));
                }

                var elements = layer.HasElements ? layer.Elements.ToArray() : Array.Empty<VectorElement>();
                foreach (var el in elements)
                {
                    var b = el.Bounds;
                    Accumulate(new SKRect(b.Left, b.Top, b.Right, b.Bottom));
                }

                if (pixels == null && elements.Length == 0) continue;
                items.Add(new Item
                {
                    Layer = layer,
                    Pixels = pixels,
                    SrcBounds = docRect,
                    BaseOffset = layer.Offset,
                    Before = layer.Surface.Snapshot(),
                    StartElements = elements,
                    LastStamp = docRect,
                });
            }
        }

        if (items.Count == 0 || source is not { } src || src.Width < 1 || src.Height < 1)
        {
            reason ??= "沒有可變形的內容";
            DisposeItems(items);
            return null;
        }
        return new TransformSession(doc, items, src, target is GroupLayer);
    }

    private static void Collect(GroupLayer group, List<RasterLayer> into)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case RasterLayer r: into.Add(r); break;
                case GroupLayer g: Collect(g, into); break;
            }
        }
    }

    /// <summary>
    /// 縮放/旋轉手勢開始：符合覆疊條件（各層上方都沒有看得見的東西）時，
    /// 把像素從合成結果拿掉一次，改由 render thread 每幀以目前矩陣直接畫。
    /// 條件不成立就維持逐步蓋章的合成器路徑（畫面正確優先於流暢）。
    /// </summary>
    public void BeginGesturePreview()
    {
        if (_disposed || _gestureOverlay) return;

        lock (_doc.SyncRoot)
        {
            foreach (var item in _items)
            {
                if (!Selections.FloatingSelection.CanOverlay(item.Layer)) return;
            }
        }

        _gestureOverlay = true;
        _pixelsStamped = true; // 像素被拿掉了，就算手勢回到 identity 也得還原快照
        foreach (var item in _items)
        {
            lock (_doc.SyncRoot)
            {
                ClearPixelTiles(item.Layer);
            }
            var display = OffsetRect(item.LastStamp, OffsetDelta);
            if (!display.IsEmpty) item.Layer.Invalidate(display);
            item.LastStamp = SKRectI.Empty;
        }
        PublishOverlay(handingOver: false);
    }

    /// <summary>
    /// 手勢結束：走覆疊時補一次 High 蓋章（覆疊殘影等合成器追上才收，不閃）；
    /// 沒走覆疊就照舊補 High。
    /// </summary>
    public void EndGesture()
    {
        if (_disposed) return;
        if (!_gestureOverlay)
        {
            Apply(preview: false);
            return;
        }
        _gestureOverlay = false;

        if (IsIdentity)
        {
            RestoreOriginal();
        }
        else
        {
            var (sx, sy) = Scales;
            var rot = Math.Abs(RotationDeg) < 0.01f ? 0f : RotationDeg;
            StampAll(preview: false, sx, sy, rot);
        }
        PublishOverlay(handingOver: true);
    }

    private void PublishOverlay(bool handingOver)
    {
        var items = _items.Where(i => i.Pixels != null)
            .Select(i => (i.Pixels!, i.SrcBounds)).ToArray();
        if (items.Length == 0)
        {
            _overlay = null;
            return;
        }

        var region = SKRectI.Empty;
        if (handingOver)
        {
            foreach (var item in _items)
            {
                var display = OffsetRect(item.LastStamp, OffsetDelta);
                if (display.IsEmpty) continue;
                region = region.IsEmpty ? display : SKRectI.Union(region, display);
            }
        }

        _overlayEverPublished = true;
        _overlay = new GestureOverlay
        {
            Items = items,
            Matrix = Matrix,
            HandingOver = handingOver,
            HandoverRegion = region,
        };
    }

    /// <summary>UI thread 每幀：合成器把蓋章區域畫完了，就收掉手勢覆疊的殘影。</summary>
    internal void CollectOverlay(Compositor compositor)
    {
        var state = _overlay;
        if (state is { HandingOver: true } &&
            (state.HandoverRegion.IsEmpty || compositor.IsRegionClean(state.HandoverRegion)))
        {
            _overlay = null;
        }
    }

    public void Apply(bool preview) => Apply(preview, null);

    /// <summary>
    /// 把目前的 TargetRect/RotationDeg 套到畫面上。
    /// <paramref name="pixelsHandledExternally"/>：回傳 true 的圖層像素改由外部呈現
    /// （拖曳覆疊快路徑），純平移時就不失效它的像素區域。
    /// </summary>
    public void Apply(bool preview, Func<RasterLayer, bool>? pixelsHandledExternally)
    {
        if (_disposed) return;

        // 手勢覆疊中：像素由 render thread 以目前矩陣直接畫，這裡只發布新矩陣、更新文字物件
        if (_gestureOverlay)
        {
            PublishOverlay(handingOver: false);
            UpdateElements();
            return;
        }

        if (IsIdentity)
        {
            if (_pixelsStamped || OffsetDelta != SKPointI.Empty) RestoreOriginal();
            return;
        }

        var (sx, sy) = Scales;
        var rot = Math.Abs(RotationDeg) < 0.01f ? 0f : RotationDeg;

        // 尺寸與角度沒變 → 純平移：不重蓋章（不重取樣），位移放進各層 Offset
        var s = _stampedParams;
        if (Math.Abs(s.Sx - sx) < 0.0001f && Math.Abs(s.Sy - sy) < 0.0001f &&
            Math.Abs(s.Rot - rot) < 0.01f &&
            Math.Abs(s.W - TargetRect.Width) < 0.5f && Math.Abs(s.H - TargetRect.Height) < 0.5f &&
            (preview || _stampedHigh))
        {
            var delta = new SKPointI(
                (int)MathF.Round(TargetRect.Left - _stampedOrigin.X),
                (int)MathF.Round(TargetRect.Top - _stampedOrigin.Y));
            TranslateTo(delta, pixelsHandledExternally);
            return;
        }

        StampAll(preview, sx, sy, rot);
    }

    /// <summary>純平移：只改各層 Offset 與文字物件位置，像素蓋章原地不動。</summary>
    private void TranslateTo(SKPointI delta, Func<RasterLayer, bool>? pixelsHandledExternally)
    {
        var old = OffsetDelta;
        if (old == delta) return;
        OffsetDelta = delta;

        var m = Matrix;
        var (sx, sy) = Scales;
        foreach (var item in _items)
        {
            var external = pixelsHandledExternally?.Invoke(item.Layer) == true;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = new SKPointI(
                    item.BaseOffset.X + delta.X, item.BaseOffset.Y + delta.Y);
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(start.TransformedBy(m, sx, sy, RotationDeg));
                }
            }

            if (!external && !item.LastStamp.IsEmpty)
            {
                item.Layer.Invalidate(SKRectI.Union(
                    OffsetRect(item.LastStamp, old), OffsetRect(item.LastStamp, delta)));
            }
        }
    }

    /// <summary>縮放/旋轉變了：位移歸零、以累積矩陣全量重蓋章。</summary>
    private void StampAll(bool preview, float sx, float sy, float rot)
    {
        var oldDelta = OffsetDelta;
        OffsetDelta = SKPointI.Empty;
        _stampedParams = (sx, sy, rot, TargetRect.Width, TargetRect.Height);
        _stampedOrigin = new SKPoint(TargetRect.Left, TargetRect.Top);
        var pureTranslate = Math.Abs(sx - 1f) < 0.0001f && Math.Abs(sy - 1f) < 0.0001f && rot == 0f;
        _stampedHigh = pureTranslate || !preview;
        _pixelsStamped = true;

        var m = Matrix;
        foreach (var item in _items)
        {
            var newStamp = SKRectI.Empty;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = item.BaseOffset; // 蓋章一律以基準位移為準
                ClearPixelTiles(item.Layer);

                if (item.Pixels != null)
                {
                    var mapped = m.MapRect(new SKRect(
                        item.SrcBounds.Left, item.SrcBounds.Top,
                        item.SrcBounds.Right, item.SrcBounds.Bottom));
                    newStamp = SKRectI.Ceiling(mapped);
                    newStamp.Inflate(2, 2); // 重取樣的邊緣餘裕
                    Stamp(item, m, newStamp, pureTranslate, preview);
                }

                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(start.TransformedBy(m, sx, sy, RotationDeg));
                }
            }

            var oldDisplay = OffsetRect(item.LastStamp, oldDelta);
            var dirty = oldDisplay.IsEmpty ? newStamp
                : newStamp.IsEmpty ? oldDisplay : SKRectI.Union(oldDisplay, newStamp);
            if (!dirty.IsEmpty) item.Layer.Invalidate(dirty);
            item.LastStamp = newStamp;
        }
    }

    private static SKRectI OffsetRect(SKRectI r, SKPointI d) =>
        r.IsEmpty ? r : new SKRectI(r.Left + d.X, r.Top + d.Y, r.Right + d.X, r.Bottom + d.Y);

    /// <summary>把文字物件更新到目前的累積矩陣（一律從起始快照換算）。</summary>
    private void UpdateElements()
    {
        var m = Matrix;
        var (sx, sy) = Scales;
        foreach (var item in _items)
        {
            lock (_doc.SyncRoot)
            {
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(start.TransformedBy(m, sx, sy, RotationDeg));
                }
            }
        }
    }

    private void Stamp(Item item, SKMatrix m, SKRectI docStamp, bool pureTranslate, bool preview)
    {
        var layer = item.Layer;
        var layerRect = new SKRectI(
            docStamp.Left - item.BaseOffset.X, docStamp.Top - item.BaseOffset.Y,
            docStamp.Right - item.BaseOffset.X, docStamp.Bottom - item.BaseOffset.Y);

        using var paint = new SKPaint
        {
            FilterQuality = pureTranslate ? SKFilterQuality.None
                : preview ? SKFilterQuality.Low
                : SKFilterQuality.High,
            IsAntialias = !pureTranslate,
        };

        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = idx.ToPixelRect();
            canvas.Translate(-tileRect.Left - item.BaseOffset.X, -tileRect.Top - item.BaseOffset.Y);
            canvas.Concat(ref m);
            canvas.DrawImage(item.Pixels, item.SrcBounds.Left, item.SrcBounds.Top, paint);
            canvas.Flush();

            if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
        }
    }

    /// <summary>回到開始時的原狀：還原像素快照、位移與文字物件（無損）。</summary>
    public void RestoreOriginal()
    {
        if (_disposed) return;
        foreach (var item in _items)
        {
            var touchedPixels = _pixelsStamped;
            lock (_doc.SyncRoot)
            {
                item.Layer.Offset = item.BaseOffset;
                if (touchedPixels)
                {
                    ClearPixelTiles(item.Layer);
                    foreach (var (idx, tile) in item.Before.Tiles)
                        item.Layer.Surface.RestoreTile(idx, tile);
                }
                foreach (var start in item.StartElements)
                {
                    if (item.Layer.FindElement(start.Id) != null)
                        item.Layer.ReplaceElement(start);
                }
            }

            if (touchedPixels || OffsetDelta != SKPointI.Empty)
            {
                var display = OffsetRect(item.LastStamp, OffsetDelta);
                var dirty = display.IsEmpty ? item.SrcBounds
                    : item.SrcBounds.IsEmpty ? display : SKRectI.Union(display, item.SrcBounds);
                if (!dirty.IsEmpty) item.Layer.Invalidate(dirty);
            }
            item.LastStamp = item.SrcBounds;
        }
        ResetStampStateToOriginal();
    }

    /// <summary>
    /// session 期間圖層的像素完全歸本 session 管（開始時的內容已全部提起），
    /// 清空 = 移除所有 tile。
    /// </summary>
    private static void ClearPixelTiles(RasterLayer layer)
    {
        if (layer.Surface.TileCount == 0) return;
        foreach (var idx in layer.Surface.Tiles.Keys.ToList())
            layer.Surface.RemoveTile(idx);
    }

    /// <summary>
    /// 落地：以 High 品質蓋最後一章（純平移已無損則不重蓋），回傳單一 undo 步驟
    /// （各層像素差異 + 位移 + 文字物件變更）。identity 時回傳 null（呼叫端直接還原）。
    /// </summary>
    internal IHistoryEntry? BuildCommit(string label)
    {
        if (IsIdentity && OffsetDelta == SKPointI.Empty) return null;
        Apply(preview: false);

        var entries = new List<IHistoryEntry>();
        foreach (var item in _items)
        {
            var layer = item.Layer;

            if (_pixelsStamped)
            {
                TileDeltaEntry? pixelEntry;
                lock (_doc.SyncRoot)
                {
                    var affected = item.SrcBounds.IsEmpty ? item.LastStamp
                        : item.LastStamp.IsEmpty ? item.SrcBounds
                        : SKRectI.Union(item.SrcBounds, item.LastStamp);
                    var layerRect = new SKRectI(
                        affected.Left - item.BaseOffset.X, affected.Top - item.BaseOffset.Y,
                        affected.Right - item.BaseOffset.X, affected.Bottom - item.BaseOffset.Y);
                    pixelEntry = TileDeltaEntry.Capture(label, layer, item.Before, layerRect);
                }
                if (pixelEntry != null) entries.Add(pixelEntry);
            }

            // 純平移落在 Offset：記位移變更
            if (OffsetDelta != SKPointI.Empty)
            {
                var oldOffset = item.BaseOffset;
                var newOffset = new SKPointI(
                    item.BaseOffset.X + OffsetDelta.X, item.BaseOffset.Y + OffsetDelta.Y);
                entries.Add(new ActionHistoryEntry(label, SKRectI.Empty,
                    undo: _ => { layer.Offset = oldOffset; layer.InvalidateAll(); },
                    redo: _ => { layer.Offset = newOffset; layer.InvalidateAll(); }));
            }

            // 文字物件：舊/新成對記錄（同 Id 替換）
            var olds = item.StartElements;
            if (olds.Length > 0)
            {
                var news = new VectorElement?[olds.Length];
                var changed = false;
                lock (_doc.SyncRoot)
                {
                    for (var i = 0; i < olds.Length; i++)
                    {
                        news[i] = layer.FindElement(olds[i].Id);
                        changed |= news[i] != null && !Equals(news[i], olds[i]);
                    }
                }
                if (changed)
                {
                    var pairs = olds.Zip(news).Where(p => p.Second != null)
                        .Select(p => (Old: p.First, New: p.Second!)).ToArray();
                    entries.Add(new ActionHistoryEntry(label, SKRectI.Empty,
                        undo: _ =>
                        {
                            foreach (var (o, _) in pairs) layer.ReplaceElement(o);
                        },
                        redo: _ =>
                        {
                            foreach (var (_, n) in pairs) layer.ReplaceElement(n);
                        }));
                }
            }
        }

        return entries.Count switch
        {
            0 => null,
            1 => entries[0],
            _ => new CompositeHistoryEntry(label, entries.ToArray()),
        };
    }

    public void Dispose() => DisposeCore(null);

    /// <summary>
    /// session 結束時用這個：發布過覆疊的話 render thread 可能還在畫那些影像，
    /// 交給退役佇列延後釋放，不能就地 Dispose。
    /// </summary>
    internal void DisposeDeferred(Compositor compositor) => DisposeCore(compositor);

    private void DisposeCore(Compositor? compositor)
    {
        if (_disposed) return;
        _disposed = true;
        _overlay = null;
        foreach (var item in _items)
        {
            if (item.Pixels != null)
            {
                if (_overlayEverPublished && compositor != null) compositor.Retire(item.Pixels);
                else item.Pixels.Dispose();
            }
            item.Before.Dispose();
        }
        _items.Clear();
    }

    private static void DisposeItems(List<Item> items)
    {
        foreach (var item in items)
        {
            item.Pixels?.Dispose();
            item.Before.Dispose();
        }
        items.Clear();
    }
}
