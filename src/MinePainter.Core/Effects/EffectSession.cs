using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.Effects;

/// <summary>
/// 一次「效果／調整」套用的生命週期（paint.net 的效果對話框流程）：
/// 開始時快照圖層（COW，零拷貝），每次預覽都從快照讀來源、算完寫回圖層（受選取遮罩混合），
/// 確定時以快照 vs 現況擷取 TileDeltaEntry 進 history，取消時把快照裝回。
/// 來源永遠是快照，所以連續預覽不會累積。
/// </summary>
public sealed class EffectSession : IEffectPreviewTarget, IDisposable
{
    private readonly EditorSession _session;
    private readonly RasterLayer _layer;
    private readonly TileSnapshot _before;
    private readonly SKRectI _regionDoc;
    private readonly SKRectI _regionLayer;
    private readonly byte[]? _selectionMask; // Region 大小；null = 無選取（全 255）
    private readonly Dictionary<int, (SKRectI Rect, uint[] Pixels)> _srcCache = new();
    private long[]? _histogram;
    private bool _disposed;

    public RasterLayer Layer => _layer;
    public SKRectI Region => _regionDoc;
    public bool IsEmpty => _regionDoc.Width <= 0 || _regionDoc.Height <= 0;

    public EffectSession(EditorSession session, RasterLayer layer)
    {
        _session = session;
        _layer = layer;
        var doc = session.Document;

        lock (doc.SyncRoot)
        {
            _before = layer.Surface.Snapshot();
            var selection = session.Selection is { IsEmpty: false } s ? s : null;
            _regionDoc = SKRectI.Intersect(selection?.Bounds ?? doc.Bounds, doc.Bounds);
            if (_regionDoc.Width <= 0 || _regionDoc.Height <= 0) _regionDoc = SKRectI.Empty;
            _regionLayer = ToLayer(_regionDoc);
            _selectionMask = selection != null && !IsEmpty ? BuildSelectionMask(selection, _regionDoc) : null;
        }
    }

    private SKRectI ToLayer(SKRectI doc) => new(
        doc.Left - _layer.Offset.X, doc.Top - _layer.Offset.Y,
        doc.Right - _layer.Offset.X, doc.Bottom - _layer.Offset.Y);

    private static byte[] BuildSelectionMask(SelectionMask selection, SKRectI region)
    {
        var mask = new byte[region.Width * region.Height];
        foreach (var (idx, tile) in selection.Mask.Tiles)
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, region);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                Array.Copy(tile.Alpha, (y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left),
                    mask, (y - region.Top) * region.Width + (inter.Left - region.Left), inter.Width);
            }
        }
        return mask;
    }

    /// <summary>建立渲染上下文（來源依 margin 從快照讀出並快取）。可在背景執行緒呼叫。</summary>
    public EffectContext CreateContext(IEffect effect, CancellationToken ct = default)
    {
        var doc = _session.Document;
        var margin = effect.SourceMargin;
        SKRectI srcDoc;
        if (margin == EffectContext.WholeLayer)
        {
            srcDoc = doc.Bounds;
        }
        else
        {
            srcDoc = _regionDoc;
            srcDoc.Inflate(Math.Max(0, margin), Math.Max(0, margin));
            srcDoc = SKRectI.Intersect(srcDoc, doc.Bounds);
        }

        (SKRectI Rect, uint[] Pixels) src;
        lock (_srcCache)
        {
            if (!_srcCache.TryGetValue(margin, out src) || src.Rect != srcDoc)
            {
                src = (srcDoc, ReadSource(srcDoc));
                _srcCache[margin] = src;
            }
        }

        return new EffectContext(_regionDoc, srcDoc, src.Pixels, new SKSizeI(doc.Width, doc.Height))
        {
            PrimaryColor = _session.Foreground,
            SecondaryColor = SKColors.White,
            Cancellation = ct,
        };
    }

    private unsafe uint[] ReadSource(SKRectI docRect)
    {
        var pixels = new uint[docRect.Width * docRect.Height];
        var layerRect = ToLayer(docRect);
        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = _before.GetTile(idx);
            if (tile == null) continue;
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, layerRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;

            var src = (uint*)tile.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var srcRow = src + (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                var dstIndex = (y - layerRect.Top) * docRect.Width + (inter.Left - layerRect.Left);
                new ReadOnlySpan<uint>(srcRow, inter.Width).CopyTo(pixels.AsSpan(dstIndex, inter.Width));
            }
        }
        return pixels;
    }

    /// <summary>
    /// 渲染並寫回圖層（預覽）。在背景執行緒呼叫；取消時丟 OperationCanceledException、不動圖層。
    /// 回傳後呼叫端負責 <see cref="Invalidate"/>（或直接用 <see cref="RenderAndApply"/>）。
    /// </summary>
    public void RenderAndApply(IEffect effect, CancellationToken ct = default)
    {
        if (IsEmpty) return;
        var ctx = CreateContext(effect, ct);
        effect.Render(ctx);
        ct.ThrowIfCancellationRequested();
        Apply(ctx.Dst);
        Invalidate();
    }

    /// <summary>把目標像素（Region 大小）寫進圖層，依選取遮罩與快照混合。</summary>
    public unsafe void Apply(uint[] dst)
    {
        if (IsEmpty) return;
        var doc = _session.Document;
        var w = _regionDoc.Width;
        lock (doc.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(_regionLayer))
            {
                var tileRect = idx.ToPixelRect();
                var inter = SKRectI.Intersect(tileRect, _regionLayer);
                if (inter.Width <= 0 || inter.Height <= 0) continue;

                var beforeTile = _before.GetTile(idx);
                var tile = _layer.Surface.GetTileForWrite(idx);
                var dstPtr = (uint*)tile.Pixels;
                var beforePtr = beforeTile == null ? null : (uint*)beforeTile.Pixels;

                for (var y = inter.Top; y < inter.Bottom; y++)
                {
                    var rowLocal = (y - _regionLayer.Top) * w;
                    var tileRow = (y - tileRect.Top) * Tile.Size;
                    for (var x = inter.Left; x < inter.Right; x++)
                    {
                        var local = rowLocal + (x - _regionLayer.Left);
                        var value = dst[local];
                        if (_selectionMask != null)
                        {
                            var m = _selectionMask[local];
                            if (m == 0)
                            {
                                value = beforePtr == null ? 0 : beforePtr[tileRow + (x - tileRect.Left)];
                            }
                            else if (m < 255)
                            {
                                var before = beforePtr == null ? 0 : beforePtr[tileRow + (x - tileRect.Left)];
                                value = EffectMath.Lerp256(before, value, m + (m >> 7));
                            }
                        }
                        dstPtr[tileRow + (x - tileRect.Left)] = value;
                    }
                }

                if (tile.IsBlank()) _layer.Surface.RemoveTile(idx);
            }
        }
    }

    public void Invalidate() => _layer.Invalidate(_regionDoc);

    void IEffectPreviewTarget.Preview(IEffect effect, CancellationToken ct) => RenderAndApply(effect, ct);

    /// <summary>確定：擷取差異進 history。回傳 false = 沒有任何像素改變。</summary>
    public bool Commit(string label)
    {
        if (IsEmpty) return false;
        var doc = _session.Document;
        TileDeltaEntry? entry;
        lock (doc.SyncRoot)
        {
            entry = TileDeltaEntry.Capture(label, _layer, _before, _regionLayer);
        }
        if (entry == null) return false;
        _session.History.Push(entry);
        Invalidate();
        return true;
    }

    /// <summary>取消：把快照裝回受影響的格。</summary>
    public void Cancel()
    {
        if (IsEmpty) return;
        var doc = _session.Document;
        lock (doc.SyncRoot)
        {
            foreach (var idx in TileIndex.CoveringRect(_regionLayer))
            {
                var before = _before.GetTile(idx);
                if (ReferenceEquals(before, _layer.Surface.GetTileForRead(idx))) continue;
                _layer.Surface.RestoreTile(idx, before);
            }
        }
        Invalidate();
    }

    /// <summary>來源範圍縮成小圖（選點器的底圖），最長邊 ≤ maxSize；caller 負責 Dispose。</summary>
    public unsafe SKBitmap RenderThumbnail(int maxSize)
    {
        var w = Math.Max(1, _regionDoc.Width);
        var h = Math.Max(1, _regionDoc.Height);
        var scale = Math.Min(1f, maxSize / (float)Math.Max(w, h));
        var tw = Math.Max(1, (int)MathF.Round(w * scale));
        var th = Math.Max(1, (int)MathF.Round(h * scale));
        var thumb = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (IsEmpty) return thumb;

        var pixels = ReadSource(_regionDoc);
        using var canvas = new SKCanvas(thumb);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        canvas.Clear(SKColors.Transparent);
        fixed (uint* ptr = pixels)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var image = SKImage.FromPixels(info, (IntPtr)ptr, w * 4);
            canvas.DrawImage(image, SKRect.Create(0, 0, tw, th), paint);
            canvas.Flush();
        }
        return thumb;
    }

    /// <summary>來源範圍的 RGB 合併直方圖（straight 色、只計 alpha &gt; 0；色階／自動色階用）。</summary>
    public long[] Histogram()
    {
        if (_histogram != null) return _histogram;
        var hist = new long[256];
        if (!IsEmpty)
        {
            var pixels = ReadSource(_regionDoc);
            for (var i = 0; i < pixels.Length; i++)
            {
                if (_selectionMask != null && _selectionMask[i] == 0) continue;
                var p = pixels[i];
                if (EffectMath.A(p) == 0) continue;
                EffectMath.Unpremul(p, out var b, out var g, out var r, out _);
                hist[r]++;
                hist[g]++;
                hist[b]++;
            }
        }
        _histogram = hist;
        return hist;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _before.Dispose();
    }
}
