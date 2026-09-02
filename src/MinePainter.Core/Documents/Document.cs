using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 一份文件：固定畫布大小 + 圖層樹（root 本身是群組）。
///
/// 執行緒模型：
/// - 所有「結構與像素的變更」在 UI thread 進行，且必須持有 SyncRoot。
/// - 合成執行緒讀取時同樣持 SyncRoot（短暫），或透過 COW 快照。
/// - Changed 事件可能在任意持鎖執行緒上發出，訂閱者只該做輕量轉發。
/// </summary>
public sealed class Document : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public GroupLayer Root { get; }

    /// <summary>文件尺寸改變（裁切／旋轉／調整大小後）。UI 需重算 viewport 與捲動範圍。</summary>
    public event Action? SizeChanged;

    /// <summary>改變畫布尺寸；只由幾何操作與其 undo 呼叫。</summary>
    internal void SetSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException($"文件尺寸無效：{width}×{height}");
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;
        SizeChanged?.Invoke();
        NotifyChanged(Bounds);
    }

    /// <summary>目前作用中的圖層（工具的寫入目標）。</summary>
    public LayerNode? ActiveLayer { get; set; }

    public object SyncRoot { get; } = new();

    /// <summary>文件某範圍已變更、需要重新合成。</summary>
    public event Action<SKRectI>? Changed;

    public Document(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException($"文件尺寸無效：{width}×{height}");
        Width = width;
        Height = height;
        Root = new GroupLayer { Name = "root" };
        Root.AttachToDocument(this);
    }

    public SKRectI Bounds => new(0, 0, Width, Height);

    /// <summary>以 Id 在圖層樹中尋找節點（undo 與序列化用）。</summary>
    public LayerNode? FindLayer(Guid id) => FindRecursive(Root, id);

    /// <summary>深度優先枚舉所有節點（不含 root）。</summary>
    public IEnumerable<LayerNode> Descendants() => DescendantsOf(Root);

    private static IEnumerable<LayerNode> DescendantsOf(GroupLayer group)
    {
        foreach (var child in group.Children)
        {
            yield return child;
            if (child is GroupLayer g)
            {
                foreach (var nested in DescendantsOf(g)) yield return nested;
            }
        }
    }

    private static LayerNode? FindRecursive(LayerNode node, Guid id)
    {
        if (node.Id == id) return node;
        if (node is GroupLayer group)
        {
            foreach (var child in group.Children)
            {
                var found = FindRecursive(child, id);
                if (found != null) return found;
            }
        }
        return null;
    }

    public void NotifyChanged(SKRectI rect)
    {
        var clipped = SKRectI.Intersect(rect, Bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return;
        Changed?.Invoke(clipped);
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            DisposeRecursive(Root);
        }
    }

    private static void DisposeRecursive(LayerNode node)
    {
        if (node is GroupLayer group)
        {
            foreach (var child in group.Children) DisposeRecursive(child);
            group.Dispose();
        }
        else if (node is IDisposable d)
        {
            d.Dispose();
        }
    }
}
