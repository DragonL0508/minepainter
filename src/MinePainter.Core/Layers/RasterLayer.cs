using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 一般圖層（paint.net 式的單一圖層概念）：同時持有
/// 　• 點陣像素（稀疏 tile 儲存 + 整層平移偏移）
/// 　• 物件（文字等，永遠可再編輯，渲染於該層像素之上）
///
/// 物件屬於它所在的圖層 —— 只有該圖層為作用中圖層時才選得到、編輯得到。
/// 物件修改一律透過 Add/Remove/Replace（在 Document.SyncRoot 內），以配合 undo。
/// </summary>
public sealed class RasterLayer : LayerNode, IDisposable
{
    private readonly List<VectorElement> _elements = new();
    private Guid? _hiddenElementId;
    private IReadOnlyList<LayerEffect> _effects = [];

    public TileSurface Surface { get; private set; }

    /// <summary>整批換掉像素（幾何操作用）；disposeOld=false 表示舊表面要留給 undo。</summary>
    internal void ReplaceSurface(TileSurface surface, bool disposeOld = true)
    {
        var old = Surface;
        Surface = surface;
        if (disposeOld) old.Dispose();
        FxCache.MarkAllDirty();
    }

    // ---- 非破壞性效果堆疊 ----

    /// <summary>套在這層像素上的效果（由先到後）。不可變清單：換整份參考（undo 同構）。</summary>
    public IReadOnlyList<LayerEffect> Effects => _effects;

    public bool HasEffects => _effects.Count > 0;

    public bool HasActiveEffects
    {
        get
        {
            foreach (var e in _effects) if (e.Enabled) return true;
            return false;
        }
    }

    /// <summary>效果堆疊套用後的快取（compositor / LayerEffectRenderer 專用）。</summary>
    public LayerEffectCache FxCache { get; } = new();

    /// <summary>換整份效果清單（在 Document.SyncRoot 內），整層重算。</summary>
    public void SetEffects(IReadOnlyList<LayerEffect> effects)
    {
        _effects = effects;
        InvalidateEffects();
    }

    /// <summary>效果堆疊整層重算並重新合成。</summary>
    public void InvalidateEffects()
    {
        FxCache.MarkAllDirty();
        if (!HasActiveEffects) FxCache.Rendered = false;
        var doc = Document;
        if (doc != null) InvalidateComposite(doc.Bounds);
    }

    /// <summary>合成時該拿哪份像素：有作用中的效果且已算過 → 效果快取；否則基底。</summary>
    public TileSurface DisplaySurface => HasActiveEffects && FxCache.Rendered ? FxCache.Surface : Surface;

    /// <summary>效果快取此刻是否代表這層的畫面（拖曳覆疊可直接拿它的快照）。</summary>
    public bool EffectsRendered => HasActiveEffects && FxCache.Rendered;

    /// <summary>
    /// 畫面上這層佔的範圍（doc 座標，tile 粒度）：內容 ∪ 效果快取（外框／陰影會超出內容）。
    /// 失效與覆疊交接用；使用者看得到的框仍用 ExactContentBounds／FrameBounds。
    /// </summary>
    public SKRectI DisplayContentBounds
    {
        get
        {
            var bounds = ContentBounds;
            if (!EffectsRendered) return bounds;
            var fx = FxCache.Surface.ContentBounds;
            if (fx.IsEmpty) return bounds;
            fx = new SKRectI(fx.Left + Offset.X, fx.Top + Offset.Y, fx.Right + Offset.X, fx.Bottom + Offset.Y);
            return bounds.IsEmpty ? fx : SKRectI.Union(bounds, fx);
        }
    }

    /// <summary>圖層內容相對文件原點的偏移。</summary>
    public SKPointI Offset { get; set; }

    /// <summary>此圖層上的物件（由下而上）。</summary>
    public IReadOnlyList<VectorElement> Elements => _elements;

    public bool HasElements => _elements.Count > 0;

    /// <summary>
    /// 文字圖層：持有文字物件的圖層。規則（使用者 2026-09-02 明示）：文字一定自己一層，
    /// 不能在上面直接繪製（要畫請先「圖層文字平面化」）；外框／陰影／光暈用圖層效果堆疊做。
    /// </summary>
    public bool IsTextLayer => _elements.Count > 0;

    /// <summary>物件層的 tile 快取（compositor 專用）。</summary>
    internal GroupCache ElementCache { get; } = new();

    /// <summary>幾何操作後需要整層重畫。</summary>
    internal void InvalidateElementCache() => ElementCache.MarkAllDirty();

    /// <summary>
    /// 暫時不渲染的物件（畫布內編輯 overlay 顯示期間避免重影）。
    /// 在 Document.SyncRoot 內設定。
    /// </summary>
    public Guid? HiddenElementId
    {
        get => _hiddenElementId;
        set
        {
            if (_hiddenElementId == value) return;
            var affected = SKRectI.Empty;
            foreach (var id in new[] { _hiddenElementId, value })
            {
                if (id is { } g && FindElement(g) is { } el)
                    affected = affected.IsEmpty ? el.Bounds : SKRectI.Union(affected, el.Bounds);
            }
            _hiddenElementId = value;
            if (!affected.IsEmpty) InvalidateElement(affected);
        }
    }

    public RasterLayer(TilePool? pool = null) => Surface = new TileSurface(pool);

    public override SKRectI ContentBounds
    {
        get
        {
            var bounds = Surface.ContentBounds;
            if (!bounds.IsEmpty)
            {
                bounds = new SKRectI(
                    bounds.Left + Offset.X, bounds.Top + Offset.Y,
                    bounds.Right + Offset.X, bounds.Bottom + Offset.Y);
            }

            foreach (var el in _elements)
            {
                var b = el.Bounds;
                if (b.IsEmpty) continue;
                bounds = bounds.IsEmpty ? b : SKRectI.Union(bounds, b);
            }
            return bounds;
        }
    }

    // ---- 物件 ----

    public VectorElement? FindElement(Guid id) => _elements.FirstOrDefault(e => e.Id == id);

    /// <summary>由上而下命中測試（回傳最上層命中的物件）。</summary>
    public VectorElement? HitTest(SKPoint p)
    {
        for (var i = _elements.Count - 1; i >= 0; i--)
            if (_elements[i].HitTest(p)) return _elements[i];
        return null;
    }

    public void AddElement(VectorElement element)
    {
        _elements.Add(element);
        InvalidateElement(element.Bounds);
    }

    public void RemoveElement(Guid id)
    {
        var index = _elements.FindIndex(e => e.Id == id);
        if (index < 0) return;
        var bounds = _elements[index].Bounds;
        _elements.RemoveAt(index);
        InvalidateElement(bounds);
    }

    /// <summary>以同 Id 的新實例替換（不可變編輯模型的核心操作）。</summary>
    public void ReplaceElement(VectorElement replacement)
    {
        var index = _elements.FindIndex(e => e.Id == replacement.Id);
        if (index < 0) throw new InvalidOperationException("找不到要替換的物件。");
        var oldBounds = _elements[index].Bounds;
        _elements[index] = replacement;
        InvalidateElement(oldBounds.IsEmpty ? replacement.Bounds : SKRectI.Union(oldBounds, replacement.Bounds));
    }

    private void InvalidateElement(SKRectI bounds)
    {
        if (bounds.IsEmpty) return;
        ElementCache.MarkDirty(bounds);
        Invalidate(bounds);
    }

    internal override void AttachToDocument(Document? doc)
    {
        base.AttachToDocument(doc);
        ElementCache.MarkAllDirty();
    }

    public void Dispose()
    {
        Surface.Dispose();
        ElementCache.Dispose();
        FxCache.Dispose();
    }
}
