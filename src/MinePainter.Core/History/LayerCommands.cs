using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>
/// 圖層樹操作：執行 + 產生對應 undo entry 一次完成。
/// 所有方法都在 UI thread 呼叫；內部自行取 Document.SyncRoot。
/// </summary>
public static class LayerCommands
{
    public static void InsertLayer(Document doc, HistoryManager history,
        GroupLayer parent, int index, LayerNode layer, string label = "新增圖層")
    {
        lock (doc.SyncRoot)
        {
            parent.Insert(index, layer);
        }

        history.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: d =>
            {
                if (ReferenceEquals(d.ActiveLayer, layer)) d.ActiveLayer = parent;
                parent.Remove(layer);
            },
            redo: _ => parent.Insert(Math.Min(index, parent.Children.Count), layer),
            onDispose: () =>
            {
                if (layer.Document == null) DisposeSubtree(layer);
            }));
    }

    public static void RemoveLayer(Document doc, HistoryManager history, LayerNode layer)
    {
        var parent = layer.Parent
            ?? throw new InvalidOperationException("節點沒有父群組，無法移除。");
        int index;

        lock (doc.SyncRoot)
        {
            index = parent.IndexOf(layer);
            if (ReferenceEquals(doc.ActiveLayer, layer)) doc.ActiveLayer = null;
            parent.RemoveAt(index);
        }

        history.Push(new ActionHistoryEntry("刪除圖層", doc.Bounds,
            undo: _ => parent.Insert(Math.Min(index, parent.Children.Count), layer),
            redo: d =>
            {
                if (ReferenceEquals(d.ActiveLayer, layer)) d.ActiveLayer = null;
                parent.Remove(layer);
            },
            onDispose: () =>
            {
                if (layer.Document == null) DisposeSubtree(layer);
            }));
    }

    /// <summary>在樹中搬移節點（同層排序或跨群組）。</summary>
    public static void MoveNode(Document doc, HistoryManager history,
        LayerNode node, GroupLayer newParent, int newIndex, string label = "移動圖層")
    {
        var oldParent = node.Parent
            ?? throw new InvalidOperationException("節點沒有父群組。");
        int oldIndex;

        lock (doc.SyncRoot)
        {
            oldIndex = oldParent.IndexOf(node);
            oldParent.RemoveAt(oldIndex);
            newParent.Insert(Math.Min(newIndex, newParent.Children.Count), node);
        }

        history.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: _ =>
            {
                newParent.Remove(node);
                oldParent.Insert(Math.Min(oldIndex, oldParent.Children.Count), node);
            },
            redo: _ =>
            {
                oldParent.Remove(node);
                newParent.Insert(Math.Min(newIndex, newParent.Children.Count), node);
            }));
    }

    /// <summary>把節點包進新群組（原位置替換）。</summary>
    public static GroupLayer WrapInGroup(Document doc, HistoryManager history, LayerNode node)
    {
        var parent = node.Parent
            ?? throw new InvalidOperationException("節點沒有父群組。");
        var group = new GroupLayer { Name = "群組" };
        int index;

        lock (doc.SyncRoot)
        {
            index = parent.IndexOf(node);
            parent.RemoveAt(index);
            parent.Insert(index, group);
            group.Add(node);
        }

        history.Push(new ActionHistoryEntry("群組化", doc.Bounds,
            undo: d =>
            {
                if (ReferenceEquals(d.ActiveLayer, group)) d.ActiveLayer = node;
                group.Remove(node);
                parent.Remove(group);
                parent.Insert(Math.Min(index, parent.Children.Count), node);
            },
            redo: _ =>
            {
                parent.Remove(node);
                parent.Insert(Math.Min(index, parent.Children.Count), group);
                group.Add(node);
            },
            onDispose: () =>
            {
                if (group.Document == null && group.Children.Count == 0) group.Dispose();
            }));
        return group;
    }

    public static void SetOpacity(Document doc, HistoryManager history, LayerNode node, float value)
    {
        var old = node.Opacity;
        if (Math.Abs(old - value) < 0.001f) return;

        lock (doc.SyncRoot)
        {
            node.Opacity = value;
        }
        node.InvalidateAll();

        history.Push(new ActionHistoryEntry("圖層不透明度", doc.Bounds,
            undo: _ => { node.Opacity = old; node.InvalidateAll(); },
            redo: _ => { node.Opacity = value; node.InvalidateAll(); }));
    }

    public static void SetVisible(Document doc, HistoryManager history, LayerNode node, bool value)
    {
        if (node.IsVisible == value) return;

        lock (doc.SyncRoot)
        {
            node.IsVisible = value;
        }
        node.InvalidateAll();

        history.Push(new ActionHistoryEntry(value ? "顯示圖層" : "隱藏圖層", doc.Bounds,
            undo: _ => { node.IsVisible = !value; node.InvalidateAll(); },
            redo: _ => { node.IsVisible = value; node.InvalidateAll(); }));
    }

    public static void SetBlendMode(Document doc, HistoryManager history, LayerNode node, BlendMode value)
    {
        var old = node.BlendMode;
        if (old == value) return;

        lock (doc.SyncRoot)
        {
            node.BlendMode = value;
        }
        node.InvalidateAll();

        history.Push(new ActionHistoryEntry("混合模式", doc.Bounds,
            undo: _ => { node.BlendMode = old; node.InvalidateAll(); },
            redo: _ => { node.BlendMode = value; node.InvalidateAll(); }));
    }

    /// <summary>換調整圖層參數（值已由 UI 即時套用時，用 pushOnly: true 只補 entry）。</summary>
    public static void SetAdjustment(Document doc, HistoryManager history,
        AdjustmentLayer layer, Adjustments.IAdjustment oldValue, Adjustments.IAdjustment newValue)
    {
        if (ReferenceEquals(oldValue, newValue)) return;

        if (!ReferenceEquals(layer.Adjustment, newValue))
        {
            lock (doc.SyncRoot)
            {
                layer.Adjustment = newValue;
            }
            layer.InvalidateAll();
        }

        history.Push(new ActionHistoryEntry($"調整：{newValue.DisplayName}", doc.Bounds,
            undo: _ => { layer.Adjustment = oldValue; layer.InvalidateAll(); },
            redo: _ => { layer.Adjustment = newValue; layer.InvalidateAll(); }));
    }

    public static void Rename(Document doc, HistoryManager history, LayerNode node, string name)
    {
        var old = node.Name;
        if (old == name) return;

        lock (doc.SyncRoot)
        {
            node.Name = name;
        }

        history.Push(new ActionHistoryEntry("重新命名", SKRectI.Empty,
            undo: _ => node.Name = old,
            redo: _ => node.Name = name));
    }

    /// <summary>複製圖層（含像素、物件與所有合成屬性），插在原圖層上方。</summary>
    public static RasterLayer? DuplicateLayer(Document doc, HistoryManager history, RasterLayer source)
    {
        var parent = source.Parent;
        if (parent == null) return null;

        var copy = new RasterLayer
        {
            Name = $"{source.Name} 複本",
            IsVisible = source.IsVisible,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode, // Pinta 這裡漏了，別跟著漏
            Offset = source.Offset,
        };

        lock (doc.SyncRoot)
        {
            foreach (var (idx, tile) in source.Surface.Tiles)
            {
                var dst = copy.Surface.GetTileForWrite(idx);
                tile.PixelSpan.CopyTo(dst.PixelSpan);
            }
            foreach (var element in source.Elements)
                copy.AddElement(element with { Id = Guid.NewGuid() });

            // 非破壞性效果堆疊也跟著複製（LayerEffect 與 IEffect 都是不可變的，遮罩共用同一份即可）
            if (source.HasEffects)
                copy.SetEffects([.. source.Effects.Select(fx => fx with { Id = Guid.NewGuid() })]);

            // 原始高清來源也複製一份（快速模式輸出時複本才能一樣從原圖重畫）
            if (source.ValidPixelSource is { } pixelSource)
            {
                var own = pixelSource.Copy();
                own.Revision = copy.Surface.Revision;
                copy.SetPixelSource(own);
            }
        }

        var index = parent.IndexOf(source) + 1;
        InsertLayer(doc, history, parent, index, copy, "複製圖層");
        lock (doc.SyncRoot) doc.ActiveLayer = copy;
        return copy;
    }

    /// <summary>
    /// 把一個物件複製到「原圖層上面一格」的新圖層並切過去（移動工具 Alt 拖曳）。
    /// 物件自己的座標不變，所以複本就疊在原件上，接著被拖走的是複本。
    /// </summary>
    public static (RasterLayer Layer, VectorElement Element)? DuplicateElementToNewLayer(
        Document doc, HistoryManager history, RasterLayer source, VectorElement element)
    {
        var parent = source.Parent;
        if (parent == null) return null;

        var clone = element with { Id = Guid.NewGuid() };
        var layer = new RasterLayer
        {
            Name = element is TextElement text
                ? VectorCommands.TextLayerNameFor(text.Text)
                : $"{source.Name} 複本",
            Offset = source.Offset,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
        };
        lock (doc.SyncRoot)
        {
            layer.AddElement(clone);
            // 圖層效果（外框、陰影…）是物件外觀的一部分：複本沒帶就「複製到一半」
            // （使用者 2026-09-06 回報 Alt 拖曳複製文字沒複製到特效）
            if (source.HasEffects)
                layer.SetEffects([.. source.Effects.Select(fx => fx with { Id = Guid.NewGuid() })]);
        }

        InsertLayer(doc, history, parent, parent.IndexOf(source) + 1, layer, "複製物件到新圖層");
        lock (doc.SyncRoot) doc.ActiveLayer = layer;
        return (layer, clone);
    }

    /// <summary>
    /// 向下合併：把上層以自己的混合模式與不透明度烘進下層，
    /// 下層自己的混合模式與不透明度保持不變（相對於更下方圖層的關係不該改變）。
    /// </summary>
    public static bool MergeLayerDown(Document doc, HistoryManager history, RasterLayer source)
    {
        var parent = source.Parent;
        if (parent == null) return false;
        var index = parent.IndexOf(source);
        if (index <= 0) return false;
        if (parent.Children[index - 1] is not RasterLayer target) return false;

        // 合併結果一定是純像素圖層（文字圖層不變式）：下層自己的文字先烙進像素，
        // 上層的像素與文字再以上層的混合模式／不透明度烘上去；文字物件不搬家。
        TileDeltaEntry? pixelEntry;
        VectorElement[] targetElements;
        lock (doc.SyncRoot)
        {
            using var before = target.Surface.Snapshot();
            targetElements = target.Elements.ToArray();
            var affected = MergeInto(source, target, targetElements);
            pixelEntry = affected.IsEmpty
                ? null
                : TileDeltaEntry.Capture("合併圖層", target, before, affected);
            foreach (var element in targetElements) target.RemoveElement(element.Id);
        }

        var elementEntry = new ActionHistoryEntry("合併物件", SKRectI.Empty,
            undo: _ =>
            {
                foreach (var element in targetElements) target.AddElement(element);
            },
            redo: _ =>
            {
                foreach (var element in targetElements) target.RemoveElement(element.Id);
            });

        lock (doc.SyncRoot)
        {
            if (ReferenceEquals(doc.ActiveLayer, source)) doc.ActiveLayer = target;
            parent.RemoveAt(index);
        }

        var removeEntry = new ActionHistoryEntry("移除已合併圖層", doc.Bounds,
            undo: _ => parent.Insert(Math.Min(index, parent.Children.Count), source),
            redo: d =>
            {
                if (ReferenceEquals(d.ActiveLayer, source)) d.ActiveLayer = target;
                parent.Remove(source);
            });

        history.Push(pixelEntry != null
            ? new CompositeHistoryEntry("向下合併圖層", pixelEntry, elementEntry, removeEntry)
            : new CompositeHistoryEntry("向下合併圖層", elementEntry, removeEntry));
        target.InvalidateAll();
        return true;
    }

    /// <summary>
    /// 把圖層上的文字物件烙成像素（PS 的「點陣化文字」）：文字畫進本層像素、物件移除，
    /// 單一步 undo（像素差異 + 物件放回）。沒有文字物件回傳 false。
    /// 文字物件本來就在像素之上合成，直接以 SrcOver 畫上去結果與原本畫面相同。
    /// </summary>
    public static bool FlattenText(Document doc, HistoryManager history, RasterLayer layer)
    {
        VectorElement[] elements;
        TileDeltaEntry? pixelEntry;
        SKRectI docRect;
        lock (doc.SyncRoot)
        {
            elements = layer.Elements.ToArray();
            if (elements.Length == 0) return false;

            using var before = layer.Surface.Snapshot();
            var layerRect = RasterizeElementsLocked(layer, elements);
            docRect = layerRect.IsEmpty ? SKRectI.Empty : new SKRectI(
                layerRect.Left + layer.Offset.X, layerRect.Top + layer.Offset.Y,
                layerRect.Right + layer.Offset.X, layerRect.Bottom + layer.Offset.Y);
            pixelEntry = layerRect.IsEmpty ? null : TileDeltaEntry.Capture("平面化文字", layer, before, layerRect);

            foreach (var el in elements) layer.RemoveElement(el.Id);
        }

        var elementEntry = new ActionHistoryEntry("移除文字物件", docRect,
            undo: _ =>
            {
                foreach (var el in elements) layer.AddElement(el); // 依原順序放回，疊放次序不變
            },
            redo: _ =>
            {
                foreach (var el in elements) layer.RemoveElement(el.Id);
            });

        history.Push(pixelEntry != null
            ? new CompositeHistoryEntry("平面化文字", pixelEntry, elementEntry)
            : elementEntry);
        if (!docRect.IsEmpty) layer.Invalidate(docRect);
        return true;
    }

    /// <summary>
    /// 把物件以 SrcOver 畫進圖層像素（不移除物件、不記 history；在 SyncRoot 內）。
    /// 回傳受影響範圍（圖層座標；沒有物件範圍時為 Empty）。
    /// </summary>
    internal static SKRectI RasterizeElementsLocked(RasterLayer layer, IReadOnlyList<VectorElement> elements)
    {
        var docRect = SKRectI.Empty;
        foreach (var el in elements)
        {
            if (el.Bounds.IsEmpty) continue;
            docRect = docRect.IsEmpty ? el.Bounds : SKRectI.Union(docRect, el.Bounds);
        }
        if (docRect.IsEmpty) return SKRectI.Empty;

        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tileRect = idx.ToPixelRect();
            var tileDoc = new SKRectI(
                tileRect.Left + layer.Offset.X, tileRect.Top + layer.Offset.Y,
                tileRect.Right + layer.Offset.X, tileRect.Bottom + layer.Offset.Y);
            // 沒有文字碰到的格子不要建（空 tile 也會進 undo 記錄）
            if (!elements.Any(el => el.Bounds.IntersectsWith(tileDoc))) continue;

            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
            foreach (var el in elements)
            {
                if (el.Bounds.IntersectsWith(tileDoc)) el.Render(canvas);
            }
            canvas.Flush();
        }
        return layerRect;
    }

    /// <summary>
    /// 把 target 自己的文字、再把 source 的像素與文字（含 source 的 opacity/blend）畫進 target 像素；
    /// 回傳受影響範圍（target 圖層座標）。
    /// </summary>
    private static SKRectI MergeInto(RasterLayer source, RasterLayer target, VectorElement[] targetElements)
    {
        var docBounds = source.Surface.ContentBounds;
        if (!docBounds.IsEmpty)
        {
            docBounds = new SKRectI(
                docBounds.Left + source.Offset.X, docBounds.Top + source.Offset.Y,
                docBounds.Right + source.Offset.X, docBounds.Bottom + source.Offset.Y);
        }
        foreach (var el in source.Elements.Concat(targetElements))
        {
            var b = el.Bounds;
            if (b.IsEmpty) continue;
            docBounds = docBounds.IsEmpty ? b : SKRectI.Union(docBounds, b);
        }
        if (docBounds.IsEmpty) return SKRectI.Empty;

        var targetRect = new SKRectI(
            docBounds.Left - target.Offset.X, docBounds.Top - target.Offset.Y,
            docBounds.Right - target.Offset.X, docBounds.Bottom - target.Offset.Y);

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)(source.Opacity * 255)),
            BlendMode = source.BlendMode.ToSkia(),
        };
        var sourceElements = source.Elements.ToArray();

        foreach (var idx in TileIndex.CoveringRect(targetRect))
        {
            var tileRect = idx.ToPixelRect();
            var tileDoc = new SKRectI(
                tileRect.Left + target.Offset.X, tileRect.Top + target.Offset.Y,
                tileRect.Right + target.Offset.X, tileRect.Bottom + target.Offset.Y);
            var touched = source.Surface.Tiles.Keys.Any(s =>
            {
                var r = s.ToPixelRect();
                return new SKRectI(r.Left + source.Offset.X, r.Top + source.Offset.Y,
                    r.Right + source.Offset.X, r.Bottom + source.Offset.Y).IntersectsWith(tileDoc);
            }) || sourceElements.Concat(targetElements).Any(el => el.Bounds.IntersectsWith(tileDoc));
            if (!touched) continue; // 沒東西落在這格：別建空 tile（會白進 undo）

            var tile = target.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            canvas.Translate(-tileRect.Left - target.Offset.X, -tileRect.Top - target.Offset.Y);

            // 1) 下層自己的文字（原本就合成在它像素之上）
            foreach (var el in targetElements)
            {
                if (el.Bounds.IntersectsWith(tileDoc)) el.Render(canvas);
            }

            // 2) 上層（像素＋文字）以上層的 opacity/blend 整體疊上（隔離層：物件與像素重疊處不算兩次）
            canvas.SaveLayer(paint);
            foreach (var (srcIdx, srcTile) in source.Surface.Tiles)
            {
                var srcRect = srcIdx.ToPixelRect();
                using var pixmap = srcTile.AsPixmap();
                using var img = SKImage.FromPixels(pixmap);
                canvas.DrawImage(img, srcRect.Left + source.Offset.X, srcRect.Top + source.Offset.Y);
            }
            foreach (var el in sourceElements)
            {
                if (el.Bounds.IntersectsWith(tileDoc)) el.Render(canvas);
            }
            canvas.Restore();
            canvas.Flush();
            if (tile.IsBlank()) target.Surface.RemoveTile(idx);
        }
        return targetRect;
    }

    /// <summary>
    /// 平面化：把整份文件合成成單一圖層，取代原本的所有節點。
    /// undo 只要把舊的節點清單放回去（零像素拷貝）。
    /// </summary>
    public static bool Flatten(Document doc, HistoryManager history)
    {
        List<LayerNode> oldChildren;
        lock (doc.SyncRoot)
        {
            oldChildren = doc.Root.Children.ToList();
            if (oldChildren.Count <= 1 && oldChildren.FirstOrDefault() is RasterLayer { HasElements: false })
                return false; // 已經是單一純點陣圖層
        }

        var flattened = new RasterLayer { Name = "背景" };
        using (var composite = Compositing.Compositor.RenderComposite(doc))
        {
            var info = new SKImageInfo(doc.Width, doc.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            composite.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
            using var pixmap = bitmap.PeekPixels();
            flattened.Surface.CopyFrom(pixmap, SKPointI.Empty);
        }

        void ApplyFlatten()
        {
            lock (doc.SyncRoot)
            {
                while (doc.Root.Children.Count > 0) doc.Root.RemoveAt(doc.Root.Children.Count - 1);
                doc.Root.Add(flattened);
                doc.ActiveLayer = flattened;
            }
        }

        void RestoreLayers()
        {
            lock (doc.SyncRoot)
            {
                doc.Root.Remove(flattened);
                foreach (var child in oldChildren) doc.Root.Add(child);
                doc.ActiveLayer = oldChildren.LastOrDefault();
            }
        }

        ApplyFlatten();
        history.Push(new ActionHistoryEntry("平面化", doc.Bounds,
            undo: _ => RestoreLayers(),
            redo: _ => ApplyFlatten()));
        return true;
    }

    private static void DisposeSubtree(LayerNode node)
    {
        if (node is GroupLayer group)
        {
            foreach (var child in group.Children) DisposeSubtree(child);
            group.Dispose();
        }
        else if (node is IDisposable d)
        {
            d.Dispose();
        }
    }
}
