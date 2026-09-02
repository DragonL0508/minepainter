using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 圖層群組：一律 isolated composite（先合成到透明底，再以群組 opacity/blend 疊到下方）。
/// 調整圖層的作用範圍以群組為界。持有 tile 級合成快取。
/// </summary>
public sealed class GroupLayer : LayerNode, IDisposable
{
    private readonly List<LayerNode> _children = new();

    /// <summary>由下而上排序（index 0 = 最底層）。</summary>
    public IReadOnlyList<LayerNode> Children => _children;

    /// <summary>此群組內容（未套群組 opacity/blend）的合成快取；compositor 專用。</summary>
    internal GroupCache Cache { get; } = new();

    public override SKRectI ContentBounds
    {
        get
        {
            var bounds = SKRectI.Empty;
            foreach (var child in _children)
            {
                var b = child.ContentBounds;
                if (b.IsEmpty) continue;
                bounds = bounds.IsEmpty ? b : SKRectI.Union(bounds, b);
            }
            return bounds;
        }
    }

    public void Insert(int index, LayerNode child)
    {
        if (child.Parent != null)
            throw new InvalidOperationException("節點已有父節點，先 Remove。");
        _children.Insert(index, child);
        child.Parent = this;
        child.AttachToDocument(Document);
        Cache.MarkAllDirty();
        child.InvalidateAll();
    }

    public void Add(LayerNode child) => Insert(_children.Count, child);

    public void Remove(LayerNode child)
    {
        var index = _children.IndexOf(child);
        if (index < 0) throw new InvalidOperationException("不是此群組的子節點。");
        RemoveAt(index);
    }

    public void RemoveAt(int index)
    {
        var child = _children[index];
        Cache.MarkAllDirty();
        child.InvalidateAll();   // 先失效（此時還在樹上，祖先快取會被標髒）
        _children.RemoveAt(index);
        child.Parent = null;
        child.AttachToDocument(null);
    }

    public int IndexOf(LayerNode child) => _children.IndexOf(child);

    internal override void AttachToDocument(Document? doc)
    {
        base.AttachToDocument(doc);
        Cache.MarkAllDirty();
        foreach (var child in _children) child.AttachToDocument(doc);
    }

    public void Dispose() => Cache.Dispose();
}
