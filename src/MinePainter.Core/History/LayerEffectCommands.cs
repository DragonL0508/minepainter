using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 圖層效果堆疊的可復原操作（整份清單換參考 = 一步 undo）。
/// 對象是 <see cref="LayerNode"/>：一般圖層與群組共用同一套（群組的效果吃的是整組合成後的樣子）。
/// 只有「烙印」需要真的把像素寫回去，所以那一個仍限定點陣圖層。
/// </summary>
public static class LayerEffectCommands
{
    public static void SetEffects(Document doc, HistoryManager history, LayerNode layer,
        IReadOnlyList<LayerEffect> before, IReadOnlyList<LayerEffect> after, string label)
    {
        if (ReferenceEquals(before, after)) return;
        lock (doc.SyncRoot)
        {
            if (!ReferenceEquals(layer.Effects, after)) layer.SetEffects(after);
        }
        history.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: d => { lock (d.SyncRoot) layer.SetEffects(before); },
            redo: d => { lock (d.SyncRoot) layer.SetEffects(after); }));
    }

    public static void Add(Document doc, HistoryManager history, LayerNode layer, LayerEffect effect)
    {
        var before = layer.Effects;
        var after = before.Append(effect).ToList();
        SetEffects(doc, history, layer, before, after, $"效果：{effect.Name}");
    }

    public static void Remove(Document doc, HistoryManager history, LayerNode layer, Guid id)
    {
        var before = layer.Effects;
        var target = before.FirstOrDefault(e => e.Id == id);
        if (target == null) return;
        var after = before.Where(e => e.Id != id).ToList();
        SetEffects(doc, history, layer, before, after, $"移除效果：{target.Name}");
    }

    public static void Replace(Document doc, HistoryManager history, LayerNode layer, LayerEffect replacement, string? label = null)
    {
        var before = layer.Effects;
        var index = IndexOf(before, replacement.Id);
        if (index < 0) return;
        var after = before.ToList();
        after[index] = replacement;
        SetEffects(doc, history, layer, before, after, label ?? $"調整效果：{replacement.Name}");
    }

    public static void SetEnabled(Document doc, HistoryManager history, LayerNode layer, Guid id, bool enabled)
    {
        var target = layer.Effects.FirstOrDefault(e => e.Id == id);
        if (target == null || target.Enabled == enabled) return;
        Replace(doc, history, layer, target with { Enabled = enabled }, (enabled ? "啟用效果：" : "停用效果：") + target.Name);
    }

    /// <summary>delta = -1 往下（更早套用）、+1 往上（更晚套用）。</summary>
    public static void Move(Document doc, HistoryManager history, LayerNode layer, Guid id, int delta)
    {
        var before = layer.Effects;
        var index = IndexOf(before, id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= before.Count) return;
        var after = before.ToList();
        (after[index], after[target]) = (after[target], after[index]);
        SetEffects(doc, history, layer, before, after, $"移動效果：{before[index].Name}");
    }

    /// <summary>
    /// 烙印：把效果堆疊的結果寫進基底像素並清空堆疊（單一步 undo）。
    /// 選取遮罩已在各效果條目內生效，這裡不再看目前的選取。
    /// </summary>
    public static bool Bake(EditorSession session, RasterLayer layer)
    {
        var doc = session.Document;
        if (!layer.HasActiveEffects && layer.Effects.Count == 0) return false;

        LayerEffectRenderer.RenderLayerNow(doc, layer, exact: true); // 烙印寫進像素，不能用預覽

        TileDeltaEntry? pixels = null;
        var before = layer.Effects;
        lock (doc.SyncRoot)
        {
            // 快取是圖層座標、涵蓋整個內容（含畫布外）；烙印範圍 = 基底內容 ∪ 快取內容
            var region = layer.Surface.ContentBounds;
            var fxBounds = layer.FxCache.Surface.ContentBounds;
            if (!fxBounds.IsEmpty) region = region.IsEmpty ? fxBounds : SKRectI.Union(region, fxBounds);
            if (layer.HasActiveEffects && layer.FxCache.Rendered && !region.IsEmpty)
            {
                using var snapshot = layer.Surface.Snapshot();
                CopyRegion(layer.FxCache.Surface, layer.Surface, region);
                pixels = TileDeltaEntry.Capture("烙印效果", layer, snapshot, region);
            }
            layer.SetEffects([]);
        }

        var stack = new ActionHistoryEntry("烙印效果", doc.Bounds,
            undo: d => { lock (d.SyncRoot) layer.SetEffects(before); },
            redo: d => { lock (d.SyncRoot) layer.SetEffects([]); });
        session.History.Push(pixels != null ? new CompositeHistoryEntry("烙印效果", pixels, stack) : stack);
        layer.InvalidateAll();
        return true;
    }

    internal static unsafe void CopyRegion(TileSurface from, TileSurface to, SKRectI rect)
    {
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, rect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            var src = from.GetTileForRead(idx);
            var dst = to.GetTileForWrite(idx);
            var s = src == null ? null : (uint*)src.Pixels;
            var d = (uint*)dst.Pixels;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = (y - tileRect.Top) * Tile.Size + (inter.Left - tileRect.Left);
                if (s == null) new Span<uint>(d + row, inter.Width).Clear();
                else new ReadOnlySpan<uint>(s + row, inter.Width).CopyTo(new Span<uint>(d + row, inter.Width));
            }
            if (dst.IsBlank()) to.RemoveTile(idx);
        }
    }

    private static int IndexOf(IReadOnlyList<LayerEffect> list, Guid id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }
}
