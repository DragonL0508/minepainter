using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 點陣編輯的 undo：記錄受影響 tile 的 before/after 引用（COW 保證不被後續編輯汙染）。
/// null = 該格當時不存在（透明）。
/// </summary>
public sealed class TileDeltaEntry : IHistoryEntry
{
    private readonly Guid _layerId;
    private readonly Dictionary<TileIndex, (Tile? Before, Tile? After)> _deltas;
    private bool _disposed;

    public string Label { get; }
    public SKRectI DirtyRect { get; }
    public long MemoryCost { get; }

    private TileDeltaEntry(string label, Guid layerId,
        Dictionary<TileIndex, (Tile?, Tile?)> deltas, SKRectI dirtyRect)
    {
        Label = label;
        _layerId = layerId;
        _deltas = deltas;
        DirtyRect = dirtyRect;

        long cost = 0;
        foreach (var (before, after) in deltas.Values)
        {
            if (before != null) cost += Tile.BytesPerTile;
            if (after != null) cost += Tile.BytesPerTile;
        }
        MemoryCost = cost;
    }

    /// <summary>
    /// 從「編輯前快照 vs 目前表面」擷取受影響格的差異。須在 Document.SyncRoot 內呼叫。
    /// affectedRect 為圖層座標。
    /// </summary>
    public static TileDeltaEntry? Capture(string label, RasterLayer layer,
        TileSnapshot before, SKRectI affectedRect)
    {
        var deltas = new Dictionary<TileIndex, (Tile?, Tile?)>();
        foreach (var idx in TileIndex.CoveringRect(affectedRect))
        {
            var b = before.GetTile(idx);
            var a = layer.Surface.GetTileForRead(idx);
            if (ReferenceEquals(b, a)) continue; // 沒動到（COW：動過的一定換了實體）

            b?.AddRef();
            a?.AddRef();
            deltas[idx] = (b, a);
        }

        if (deltas.Count == 0) return null;

        var dirtyDoc = new SKRectI(
            affectedRect.Left + layer.Offset.X, affectedRect.Top + layer.Offset.Y,
            affectedRect.Right + layer.Offset.X, affectedRect.Bottom + layer.Offset.Y);
        return new TileDeltaEntry(label, layer.Id, deltas, dirtyDoc);
    }

    public void Undo(Document doc) => Apply(doc, useBefore: true);

    public void Redo(Document doc) => Apply(doc, useBefore: false);

    private void Apply(Document doc, bool useBefore)
    {
        if (doc.FindLayer(_layerId) is not RasterLayer layer)
            throw new InvalidOperationException("undo 目標圖層不存在。");

        foreach (var (idx, (before, after)) in _deltas)
            layer.Surface.RestoreTile(idx, useBefore ? before : after);

        layer.Invalidate(DirtyRect); // 含祖先群組快取標髒
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (before, after) in _deltas.Values)
        {
            before?.Release();
            after?.Release();
        }
        _deltas.Clear();
    }
}
