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

    private int _outputWidth;
    private int _outputHeight;

    /// <summary>
    /// 這份專案「真正的」輸出解析度。0 = 與畫布相同（一般模式）。
    ///
    /// 快速模式（實驗）：畫布是代理（預設 1080p 級），所有編輯、合成、效果都在代理解析度上做，
    /// 輸出時才整份放大重算成這個尺寸 —— 文字、形狀、效果都會以新尺寸重新算，
    /// 筆刷畫上去的像素則是重新取樣（見 OutputRender）。
    /// 專案本身沒有因此壞掉：以一般模式開啟就是把整份放大成這個尺寸再編輯。
    /// </summary>
    public int OutputWidth
    {
        get => _outputWidth > 0 ? _outputWidth : Width;
        private set => _outputWidth = value;
    }

    public int OutputHeight
    {
        get => _outputHeight > 0 ? _outputHeight : Height;
        private set => _outputHeight = value;
    }

    /// <summary>畫布比輸出小＝快速模式。</summary>
    public bool IsFastMode => _outputWidth > Width || _outputHeight > Height;

    private float _dpi = PhysicalUnits.ScreenDpi;

    /// <summary>
    /// 解析度（每英寸幾個像素）。只影響「印出來多大」的換算與對話框顯示的公分／英寸，
    /// 不影響任何像素運算。新檔預設 96；印刷預設集 300；.psd 帶進來的用它自己的。
    /// </summary>
    public float Dpi
    {
        get => _dpi;
        set => _dpi = float.IsFinite(value) && value > 0 ? Math.Clamp(value, 1f, 10000f) : PhysicalUnits.ScreenDpi;
    }

    /// <summary>輸出比畫布大幾倍（一般模式為 1）。</summary>
    public float OutputScale => IsFastMode ? OutputWidth / (float)Width : 1f;

    /// <summary>
    /// 設定輸出解析度。與畫布相同（或更小）就是一般模式。
    /// 傳 0 代表「跟著畫布」。
    /// </summary>
    public void SetOutputSize(int width, int height)
    {
        _outputWidth = width <= Width ? 0 : width;
        _outputHeight = height <= Height ? 0 : height;
        if (_outputWidth == 0 || _outputHeight == 0)
        {
            _outputWidth = 0;
            _outputHeight = 0;
        }
    }

    /// <summary>改變畫布尺寸；只由幾何操作與其 undo 呼叫。</summary>
    internal void SetSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException($"文件尺寸無效：{width}×{height}");
        if (width == Width && height == Height) return;

        // 快速模式：畫布改了（裁切／調整大小），輸出解析度按同樣的比例跟著走，
        // 不然裁一半之後輸出還是原來那麼大
        if (IsFastMode && Width > 0 && Height > 0)
        {
            var sx = width / (float)Width;
            var sy = height / (float)Height;
            _outputWidth = Math.Max(1, (int)MathF.Round(OutputWidth * sx));
            _outputHeight = Math.Max(1, (int)MathF.Round(OutputHeight * sy));
        }

        Width = width;
        Height = height;
        if (_outputWidth <= Width || _outputHeight <= Height) SetOutputSize(_outputWidth, _outputHeight);
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
