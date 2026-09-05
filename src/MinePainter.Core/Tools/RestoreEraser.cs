using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>
/// 反向橡皮擦（橡皮擦／去背筆按住 Alt）要還原的「這一輪擦之前的樣子」。
///
/// 快照是引用式的（<see cref="TileSnapshot"/> 對每格 AddRef），成本等同一步 undo，
/// 所以每一輪擦除開始時拍一張就好。「一輪」＝連續的擦除；中間只要插進任何別的歷史步驟
/// （畫了一筆、套了效果、按了 undo…）就作廢重拍 —— 不然 Alt 會把那些改動也一起洗掉。
/// 換圖層、圖層整個 Surface 被換掉（調整影像大小之類）或位移變了，也一律重拍。
/// </summary>
public sealed class EraseBaseline : IDisposable
{
    private TileSnapshot? _snapshot;
    private Guid _layerId;
    private TileSurface? _surface;
    private SKPointI _offset;

    /// <summary>拍完基準之後，我們自己推進歷史的最後一步；不是它＝中間有人插隊。</summary>
    private IHistoryEntry? _lastEntry;

    /// <summary>開始一筆擦除：需要的話重新拍一張基準。在 Document.SyncRoot 內呼叫。</summary>
    public void BeginErase(RasterLayer layer, HistoryManager history)
    {
        if (IsValidFor(layer, history)) return;
        Reset();
        _snapshot = layer.Surface.Snapshot();
        _layerId = layer.Id;
        _surface = layer.Surface;
        _offset = layer.Offset;
        _lastEntry = LastOf(history);
    }

    /// <summary>一筆擦除／還原推完歷史後呼叫：記住那一步，下一筆才知道中間沒別人插隊。</summary>
    public void AfterStroke(HistoryManager history) => _lastEntry = LastOf(history);

    /// <summary>這個圖層現在有東西可以還原嗎。</summary>
    public bool CanRestore(RasterLayer layer, HistoryManager history) => IsValidFor(layer, history);

    /// <summary>基準快照裡的那一格（null＝這一格當時是空的）。</summary>
    public Tile? TileAt(TileIndex idx) => _snapshot?.GetTile(idx);

    private bool IsValidFor(RasterLayer layer, HistoryManager history) =>
        _snapshot != null &&
        _layerId == layer.Id &&
        ReferenceEquals(_surface, layer.Surface) &&
        _offset == layer.Offset &&
        ReferenceEquals(_lastEntry, LastOf(history));

    private static IHistoryEntry? LastOf(HistoryManager history)
    {
        var stack = history.UndoStack;
        return stack.Count > 0 ? stack[^1] : null;
    }

    private void Reset()
    {
        _snapshot?.Dispose();
        _snapshot = null;
        _surface = null;
        _lastEntry = null;
        _layerId = Guid.Empty;
    }

    public void Dispose() => Reset();
}

/// <summary>
/// 反向橡皮擦的筆劃：沿著游標用圓形 dab 把基準快照的像素混回圖層。
///
/// 這條路徑不走 StrokeBuffer —— 那個緩衝只帶「一個顏色 + 一張遮罩」，
/// 畫得出擦除（DstOut）與上色（SrcOver），畫不出「把原本的像素放回來」。
/// 所以直接寫進圖層的 tile（同去背筆的 dab 步調），拖曳中就看得到內容長回來，
/// undo 仍然是整筆一步（落筆前的快照 + TileDeltaEntry）。
/// </summary>
public sealed class RestoreStroke
{
    private TileSnapshot? _before;
    private RasterLayer? _layer;
    private SKPoint _last;
    private float _carry;
    private SKRectI _dirtyDoc;
    private float _radius;
    private float _hardness;
    private float _opacity;

    public bool IsActive { get; private set; }

    /// <summary>落筆。回傳 false＝這一輪沒有基準可還原（呼叫端自己提示）。</summary>
    public bool Begin(EditorSession session, RasterLayer layer, SKPoint p,
        float radius, float hardness, float opacity)
    {
        var doc = session.Document;
        if (!session.EraseBaseline.CanRestore(layer, session.History)) return false;

        _layer = layer;
        _radius = Math.Max(0.5f, radius);
        _hardness = Math.Clamp(hardness, 0f, 1f);
        _opacity = Math.Clamp(opacity, 0f, 1f);
        _last = p;
        _carry = 0f;
        _dirtyDoc = SKRectI.Empty;
        IsActive = true;

        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            _before = layer.Surface.Snapshot();
            dirty = Dab(session, p);
        }
        if (!dirty.IsEmpty) layer.Invalidate(dirty);
        return true;
    }

    public void Continue(EditorSession session, SKPoint p)
    {
        if (!IsActive || _layer == null) return;
        SKRectI dirty;
        lock (session.Document.SyncRoot) dirty = Advance(session, p);
        if (!dirty.IsEmpty) _layer.Invalidate(dirty);
    }

    /// <summary>放開：推一步 undo（沒動到任何像素就不推）。</summary>
    public void End(EditorSession session, SKPoint p)
    {
        if (!IsActive) return;
        IsActive = false;
        var layer = _layer;
        _layer = null;

        TileDeltaEntry? entry = null;
        lock (session.Document.SyncRoot)
        {
            if (layer != null) Advance(session, p);
            if (layer != null && layer.Document == session.Document && !_dirtyDoc.IsEmpty)
            {
                var affected = new SKRectI(
                    _dirtyDoc.Left - layer.Offset.X, _dirtyDoc.Top - layer.Offset.Y,
                    _dirtyDoc.Right - layer.Offset.X, _dirtyDoc.Bottom - layer.Offset.Y);
                entry = TileDeltaEntry.Capture("反向橡皮擦", layer, _before!, affected);
            }
            _before?.Dispose();
            _before = null;
        }

        if (entry != null)
        {
            session.History.Push(entry);
            session.EraseBaseline.AfterStroke(session.History);
        }
        if (!_dirtyDoc.IsEmpty) layer?.Invalidate(_dirtyDoc);
    }

    /// <summary>沿 _last→p 以固定間距落 dab（同去背筆：間距 = 半徑/4，最少 1px）。</summary>
    private SKRectI Advance(EditorSession session, SKPoint p)
    {
        var spacing = Math.Max(1f, _radius * 0.25f);
        var dx = p.X - _last.X;
        var dy = p.Y - _last.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return SKRectI.Empty;

        var dirty = SKRectI.Empty;
        var t = spacing - _carry;
        while (t <= len)
        {
            var q = new SKPoint(_last.X + dx * (t / len), _last.Y + dy * (t / len));
            dirty = Union(dirty, Dab(session, q));
            t += spacing;
        }
        _carry = len - (t - spacing);
        _last = p;
        return dirty;
    }

    /// <summary>單個 dab：圈內每個像素依覆蓋度往基準色靠回去（premul 線性內插）。</summary>
    private unsafe SKRectI Dab(EditorSession session, SKPoint center)
    {
        var doc = session.Document;
        var layer = _layer!;
        var baseline = session.EraseBaseline;
        var selection = session.Selection;

        var left = (int)MathF.Floor(center.X - _radius - 1f);
        var top = (int)MathF.Floor(center.Y - _radius - 1f);
        var right = (int)MathF.Ceiling(center.X + _radius + 1f);
        var bottom = (int)MathF.Ceiling(center.Y + _radius + 1f);
        var docRect = SKRectI.Intersect(new SKRectI(left, top, right, bottom), doc.Bounds);
        if (docRect.Width <= 0 || docRect.Height <= 0) return SKRectI.Empty;

        var off = layer.Offset;
        var layerRect = new SKRectI(
            docRect.Left - off.X, docRect.Top - off.Y, docRect.Right - off.X, docRect.Bottom - off.Y);

        var touched = false;
        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tileRect = idx.ToPixelRect();
            var part = SKRectI.Intersect(tileRect, layerRect);
            if (part.Width <= 0 || part.Height <= 0) continue;

            var source = baseline.TileAt(idx);
            if (source == null && layer.Surface.GetTileForRead(idx) == null) continue; // 兩邊都空

            var tile = layer.Surface.GetTileForWrite(idx);
            var pixels = (uint*)tile.Pixels;
            var src = source == null ? null : (uint*)source.Pixels;

            for (var y = part.Top; y < part.Bottom; y++)
            {
                var docY = y + off.Y;
                var dy = docY + 0.5f - center.Y;
                var row = (y - tileRect.Top) << 8;
                for (var x = part.Left; x < part.Right; x++)
                {
                    var docX = x + off.X;
                    var dx = docX + 0.5f - center.X;
                    var coverage = BrushEngine.Coverage(MathF.Sqrt(dx * dx + dy * dy), _radius, _hardness) * _opacity;
                    if (coverage <= 0f) continue;

                    if (selection != null)
                    {
                        coverage *= selection.CoverageAt(docX, docY) / 255f;
                        if (coverage <= 0f) continue;
                    }

                    var i = row | (x - tileRect.Left);
                    var target = src == null ? 0u : src[i];
                    var blended = Lerp(pixels[i], target, coverage);
                    if (blended == pixels[i]) continue;
                    pixels[i] = blended;
                    touched = true;
                }
            }
        }

        if (!touched) return SKRectI.Empty;
        _dirtyDoc = Union(_dirtyDoc, docRect);
        return docRect;
    }

    /// <summary>premul BGRA 的逐通道內插（t = 1 完全回到基準）。</summary>
    private static uint Lerp(uint from, uint to, float t)
    {
        if (t >= 1f) return to;
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var a = (int)((from >> shift) & 0xFF);
            var b = (int)((to >> shift) & 0xFF);
            var v = (uint)(a + (b - a) * t + 0.5f);
            result |= Math.Min(v, 255u) << shift;
        }
        return result;
    }

    private static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);
}
