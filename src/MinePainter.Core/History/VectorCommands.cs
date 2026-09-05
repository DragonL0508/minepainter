using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;

namespace MinePainter.Core.History;

/// <summary>向量元素操作：執行 + undo entry。皆在 UI thread 呼叫。</summary>
public static class VectorCommands
{
    public static void AddElement(Document doc, HistoryManager history,
        RasterLayer layer, VectorElement element, string label)
    {
        lock (doc.SyncRoot)
        {
            layer.AddElement(element);
        }

        history.Push(new ActionHistoryEntry(label, element.Bounds,
            undo: _ => layer.RemoveElement(element.Id),
            redo: _ => layer.AddElement(element)));
    }

    public static void RemoveElement(Document doc, HistoryManager history,
        RasterLayer layer, VectorElement element, string label = "刪除元素")
    {
        lock (doc.SyncRoot)
        {
            layer.RemoveElement(element.Id);
        }

        history.Push(new ActionHistoryEntry(label, element.Bounds,
            undo: _ => layer.AddElement(element),
            redo: _ => layer.RemoveElement(element.Id)));
    }

    /// <summary>
    /// 以新實例替換（同 Id）。newElement 若已被 UI 即時套用（live preview），此方法只補 entry。
    /// </summary>
    public static void ReplaceElement(Document doc, HistoryManager history,
        RasterLayer layer, VectorElement oldElement, VectorElement newElement, string label)
    {
        if (oldElement.Id != newElement.Id)
            throw new ArgumentException("替換必須保持同一個元素 Id。");
        if (Equals(oldElement, newElement)) return;

        lock (doc.SyncRoot)
        {
            if (!ReferenceEquals(layer.FindElement(newElement.Id), newElement))
                layer.ReplaceElement(newElement);
        }

        history.Push(new ActionHistoryEntry(label, newElement.Bounds,
            undo: _ => layer.ReplaceElement(oldElement),
            redo: _ => layer.ReplaceElement(newElement)));
    }

    /// <summary>
    /// 整份文件換字型：把用著 <paramref name="mapping"/> 裡各個家族的文字物件改成對應的新家族，
    /// 記成**一步** undo。開檔時發現缺字型、使用者當場選了替代字型就走這裡。
    /// 回傳實際換掉的文字物件數。
    /// </summary>
    public static int ReplaceFontFamilies(Document doc, HistoryManager history,
        IReadOnlyDictionary<string, string> mapping, string label)
    {
        if (mapping.Count == 0) return 0;

        var changes = new List<(RasterLayer Layer, TextElement Before, TextElement After)>();
        lock (doc.SyncRoot)
        {
            foreach (var node in doc.Descendants())
            {
                if (node is not RasterLayer layer) continue;
                foreach (var element in layer.Elements)
                {
                    if (element is not TextElement text) continue;
                    if (!mapping.TryGetValue(text.FontFamily, out var replacement)) continue;
                    if (string.IsNullOrEmpty(replacement) || replacement == text.FontFamily) continue;
                    changes.Add((layer, text, text with { FontFamily = replacement }));
                }
            }
            foreach (var (layer, _, after) in changes) layer.ReplaceElement(after);
        }
        if (changes.Count == 0) return 0;

        var snapshot = changes.ToArray();
        history.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: _ =>
            {
                foreach (var (layer, before, _) in snapshot) layer.ReplaceElement(before);
            },
            redo: _ =>
            {
                foreach (var (layer, _, after) in snapshot) layer.ReplaceElement(after);
            }));
        return changes.Count;
    }

    /// <summary>
    /// 「建立時未進 history」的新元素（文字單擊建立）落地：
    /// 以最終內容替換（若 UI 尚未套用），並補單一步 undo（undo = 整個元素消失）。
    /// </summary>
    public static void CommitNewElement(Document doc, HistoryManager history,
        RasterLayer layer, VectorElement element, string label)
    {
        lock (doc.SyncRoot)
        {
            if (!ReferenceEquals(layer.FindElement(element.Id), element))
                layer.ReplaceElement(element);
        }

        history.Push(new ActionHistoryEntry(label, element.Bounds,
            undo: _ => layer.RemoveElement(element.Id),
            redo: _ => layer.AddElement(element)));
    }

    /// <summary>靜默移除（未進 history 的新元素內容為空或被取消時；不記 undo）。</summary>
    public static void DiscardElement(Document doc, RasterLayer layer, Guid id)
    {
        lock (doc.SyncRoot)
        {
            layer.RemoveElement(id);
        }
    }

    public const string DefaultTextLayerName = "文字";

    /// <summary>
    /// 文字一定自己一層：在作用中圖層上方（或群組末端）靜默建一個空圖層並設為作用中，
    /// 不進 history —— 落地時由 <see cref="CommitNewTextLayer"/> 一步記「新增文字」，
    /// 內容為空則 <see cref="DiscardNewTextLayer"/> 靜默收掉。
    /// </summary>
    public static RasterLayer CreateTextLayerSilently(Document doc)
    {
        var active = doc.ActiveLayer;
        var parent = active as GroupLayer ?? active?.Parent ?? doc.Root;
        var index = active != null && active.Parent != null && active is not GroupLayer
            ? parent.IndexOf(active) + 1
            : parent.Children.Count;
        var layer = new RasterLayer { Name = DefaultTextLayerName };
        lock (doc.SyncRoot)
        {
            parent.Insert(index, layer);
            doc.ActiveLayer = layer;
        }
        return layer;
    }

    /// <summary>新文字落地：圖層 + 元素一步 undo（undo = 整個文字圖層消失）。圖層名跟著內容。</summary>
    public static void CommitNewTextLayer(Document doc, HistoryManager history,
        RasterLayer layer, TextElement element, string label)
    {
        var parent = layer.Parent ?? throw new InvalidOperationException("文字圖層不在文件裡。");
        int index;
        lock (doc.SyncRoot)
        {
            if (!ReferenceEquals(layer.FindElement(element.Id), element))
                layer.ReplaceElement(element);
            if (layer.Name == DefaultTextLayerName) layer.Name = TextLayerNameFor(element.Text);
            index = parent.IndexOf(layer);
        }

        history.Push(new ActionHistoryEntry(label, doc.Bounds,
            undo: d =>
            {
                lock (d.SyncRoot)
                {
                    var below = parent.IndexOf(layer) - 1;
                    parent.Remove(layer);
                    if (ReferenceEquals(d.ActiveLayer, layer))
                        d.ActiveLayer = below >= 0 && below < parent.Children.Count ? parent.Children[below] : parent.Children.LastOrDefault();
                }
            },
            redo: d =>
            {
                lock (d.SyncRoot)
                {
                    parent.Insert(Math.Min(index, parent.Children.Count), layer);
                    d.ActiveLayer = layer;
                }
            }));
    }

    /// <summary>靜默收掉還沒落地的文字圖層（內容為空／取消）。</summary>
    public static void DiscardNewTextLayer(Document doc, RasterLayer layer)
    {
        lock (doc.SyncRoot)
        {
            if (layer.Parent is not { } parent) return;
            var below = parent.IndexOf(layer) - 1;
            parent.Remove(layer);
            if (ReferenceEquals(doc.ActiveLayer, layer))
                doc.ActiveLayer = below >= 0 && below < parent.Children.Count ? parent.Children[below] : parent.Children.LastOrDefault();
        }
        layer.Dispose();
    }

    /// <summary>文字內容 → 圖層名（第一行，最多 24 字）。</summary>
    /// <summary>
    /// 把文字物件依選取範圍拆成獨立的文字圖層（之前／之中／之後，跨行再依行切），
    /// 全部收進一個群組（名字沿用原圖層）。每一段都擺在原本字面的位置（見 <see cref="TextElement.SplitPieces"/>），
    /// 圖層的效果、不透明度、混合模式每一段各帶一份，之後可以分別改樣式。
    /// 一步 undo。回傳新群組與「選取那一段」的圖層；拆不了（範圍為空、只有一段、有彎曲變形）回 null。
    /// </summary>
    public static (GroupLayer Group, RasterLayer Selected)? SplitText(Document doc, HistoryManager history,
        RasterLayer layer, TextElement element, int start, int length)
    {
        var parent = layer.Parent;
        if (parent == null || layer.FindElement(element.Id) is not TextElement current) return null;
        var pieces = current.SplitPieces(start, length, out var selectedIndex);
        if (pieces.Count < 2 || selectedIndex.Count == 0) return null;

        var group = new GroupLayer { Name = layer.Name };
        var pieceLayers = new List<RasterLayer>();
        foreach (var piece in pieces)
        {
            var pieceLayer = new RasterLayer
            {
                Name = TextLayerNameFor(piece.Text),
                Offset = layer.Offset,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                IsVisible = layer.IsVisible,
            };
            pieceLayer.AddElement(piece);
            if (layer.HasEffects)
                pieceLayer.SetEffects([.. layer.Effects.Select(fx => fx with { Id = Guid.NewGuid() })]);
            pieceLayers.Add(pieceLayer);
        }

        // 原圖層上還有別的物件就留在群組最底下（只拿掉被拆的那一個）；只有這一個就整層換掉
        var keepOriginal = layer.Elements.Count > 1;
        int index;
        lock (doc.SyncRoot)
        {
            index = parent.IndexOf(layer);
            if (keepOriginal)
            {
                layer.RemoveElement(current.Id);
                parent.RemoveAt(index);
                group.Add(layer);
            }
            else
            {
                parent.RemoveAt(index);
            }
            foreach (var pieceLayer in pieceLayers) group.Add(pieceLayer);
            parent.Insert(index, group);
            doc.ActiveLayer = pieceLayers[selectedIndex[0]];
        }

        history.Push(new ActionHistoryEntry("分離文字", doc.Bounds,
            undo: d =>
            {
                if (group.Children.Contains(d.ActiveLayer!) || ReferenceEquals(d.ActiveLayer, group)) d.ActiveLayer = layer;
                parent.Remove(group);
                foreach (var pieceLayer in pieceLayers) group.Remove(pieceLayer);
                if (keepOriginal)
                {
                    group.Remove(layer);
                    layer.AddElement(current);
                }
                parent.Insert(Math.Min(index, parent.Children.Count), layer);
            },
            redo: d =>
            {
                parent.Remove(layer);
                if (keepOriginal)
                {
                    layer.RemoveElement(current.Id);
                    group.Add(layer);
                }
                foreach (var pieceLayer in pieceLayers) group.Add(pieceLayer);
                parent.Insert(Math.Min(index, parent.Children.Count), group);
                d.ActiveLayer = pieceLayers[selectedIndex[0]];
            },
            onDispose: () =>
            {
                if (group.Document == null)
                {
                    foreach (var pieceLayer in pieceLayers) if (pieceLayer.Document == null) pieceLayer.Dispose();
                    if (group.Children.Count == 0) group.Dispose();
                }
                else if (!keepOriginal && layer.Document == null) layer.Dispose();
            }));
        doc.NotifyChanged(doc.Bounds);
        return (group, pieceLayers[selectedIndex[0]]);
    }

    public static string TextLayerNameFor(string text)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length == 0) return DefaultTextLayerName;
        return line.Length > 24 ? line[..24] + "…" : line;
    }

    /// <summary>
    /// 物件要放的圖層 = 目前作用中的圖層。
    /// 沒有作用中圖層（例如選到群組）時，才新增一個並設為作用中。
    /// </summary>
    public static RasterLayer EnsureTargetLayer(Document doc, HistoryManager history)
    {
        if (doc.ActiveLayer is RasterLayer existing) return existing;

        var active = doc.ActiveLayer;
        var parent = active as GroupLayer ?? active?.Parent ?? doc.Root;
        var index = active != null && active.Parent != null && active is not GroupLayer
            ? parent.IndexOf(active) + 1
            : parent.Children.Count;

        var layer = new RasterLayer { Name = $"圖層 {CountLayers(doc.Root) + 1}" };
        LayerCommands.InsertLayer(doc, history, parent, index, layer);
        lock (doc.SyncRoot)
        {
            doc.ActiveLayer = layer;
        }
        return layer;
    }

    private static int CountLayers(GroupLayer group)
    {
        var count = 0;
        foreach (var child in group.Children)
        {
            count++;
            if (child is GroupLayer g) count += CountLayers(g);
        }
        return count;
    }
}
