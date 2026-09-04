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

    private LayerNode? _activeLayer;

    /// <summary>目前作用中的圖層（工具的寫入目標）。</summary>
    public LayerNode? ActiveLayer
    {
        get => _activeLayer;
        set
        {
            if (ReferenceEquals(_activeLayer, value)) return;
            _activeLayer = value;
            ActiveLayerChanged?.Invoke();
        }
    }

    /// <summary>
    /// 換了作用中圖層。設定 <see cref="ActiveLayer"/> 的地方散在圖層面板、工具、貼上、
    /// 各種 undo entry 裡，要在「換層」時做的事只能掛在這裡才不會漏。
    /// </summary>
    public event Action? ActiveLayerChanged;

    public object SyncRoot { get; } = new();

    private volatile bool _interactiveGesture;

    /// <summary>
    /// 進行中的移動／旋轉／縮放手勢。手勢期間**不算效果堆疊**：
    /// 一段帶外框＋陰影的大文字，效果算一次要上百毫秒，每動一步就排一次的話合成器永遠追不上，
    /// 畫面上看起來就是「手勢期間完全沒有渲染」（使用者 2026-09-04 回報）。
    /// 手勢中改畫沒有效果的原始內容（看得到、只是暫時沒有外框陰影），放開再算回來 ——
    /// 使用者明示「寧可過程中品質很低，也不要完全看不到」。
    /// </summary>
    public bool InteractiveGesture
    {
        get => _interactiveGesture;
        set => _interactiveGesture = value;
    }

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
    /// <summary>第一個違反「文字圖層不能有像素」不變式的圖層（null = 文件乾淨）。</summary>
    public Layers.RasterLayer? FindMixedLayer()
    {
        foreach (var node in Descendants())
            if (node is Layers.RasterLayer { ViolatesTextLayerInvariant: true } r) return r;
        return null;
    }

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
