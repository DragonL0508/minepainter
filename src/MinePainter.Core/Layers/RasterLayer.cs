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

    public TileSurface Surface { get; private set; }

    /// <summary>整批換掉像素（幾何操作用）；disposeOld=false 表示舊表面要留給 undo。</summary>
    internal void ReplaceSurface(TileSurface surface, bool disposeOld = true)
    {
        var old = Surface;
        Surface = surface;
        if (disposeOld) old.Dispose();
        FxCache.MarkAllDirty();
        SetPixelSource(null); // 換掉整片像素＝原始那份對不上了
    }

    private LayerPixelSource? _pixelSource;

    /// <summary>
    /// 這層像素的「原始高清來源」；null＝沒有（就是一般圖層）。見 <see cref="LayerPixelSource"/>。
    /// 讀的人要用 <see cref="ValidPixelSource"/>，它會順便檢查有沒有被別的編輯改過。
    /// </summary>
    public LayerPixelSource? PixelSource => _pixelSource;

    /// <summary>
    /// 還有效的原始高清來源：像素從那次變形之後沒被別的編輯改過才算數。
    /// 失效的那份會就地釋放掉（記憶體不留著）。
    /// </summary>
    public LayerPixelSource? ValidPixelSource
    {
        get
        {
            if (_pixelSource is not { } src) return null;
            if (src.Revision == Surface.Revision) return src;
            SetPixelSource(null);
            return null;
        }
    }

    /// <summary>換掉（或清掉）原始高清來源；舊的就地釋放。</summary>
    public void SetPixelSource(LayerPixelSource? source)
    {
        if (ReferenceEquals(_pixelSource, source)) return;
        _pixelSource?.Dispose();
        _pixelSource = source;
    }

    // ---- 非破壞性效果堆疊（清單／快取在 LayerNode，群組也有一份）----

    /// <summary>效果快取跟著圖層座標走，平移整層不必重算（見 LayerNode.EffectOffset）。</summary>
    public override SKPointI EffectOffset => Offset;

    /// <summary>合成時該拿哪份像素：有作用中的效果且已算過 → 效果快取；否則基底。</summary>
    public TileSurface DisplaySurface => EffectsRendered ? FxCache.Surface : Surface;

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

    /// <summary>
    /// 文字圖層不變式：有物件的圖層不能同時有像素（反之亦然）。
    /// 所有會把像素寫進圖層或把物件放進圖層的入口都得維持這條；
    /// 測試以此驗證整份文件（<see cref="Documents.Document.FindMixedLayer"/>）。
    /// </summary>
    public bool ViolatesTextLayerInvariant => _elements.Count > 0 && !Surface.ExactContentBounds().IsEmpty;

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

    private bool _elementsHidden;

    /// <summary>
    /// 整層的物件暫時都不渲染（含效果堆疊的來源）——手勢期間物件由一張快照代表時用。
    ///
    /// 與 <see cref="HiddenElementId"/> 同一個用途，差別只在「一個」與「整層」：
    /// 變形手勢一次動的是整層的物件，逐個藏沒有意義。在 Document.SyncRoot 內設定。
    /// </summary>
    public bool ElementsHidden
    {
        get => _elementsHidden;
        set
        {
            if (_elementsHidden == value) return;
            _elementsHidden = value;
            var affected = SKRectI.Empty;
            foreach (var el in _elements)
            {
                var b = el.Bounds;
                if (b.IsEmpty) continue;
                affected = affected.IsEmpty ? b : SKRectI.Union(affected, b);
            }
            if (!affected.IsEmpty) InvalidateElement(affected);
        }
    }

    /// <summary>這個物件現在該不該畫（整層藏起來、或單獨被藏起來就不畫）。</summary>
    public bool IsElementHidden(Guid id) => _elementsHidden || _hiddenElementId == id;

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
    /// <summary>
    /// 換掉一個物件。<paramref name="effectsUnchanged"/>＝這次改動不會讓效果堆疊算出不同的東西
    /// （目前只有「物件與圖層 Offset 一起平移同一個整數向量」符合），效果快取因此不必重算 ——
    /// 4K 上一個帶外光暈的文字圖層重算一次要 56 ms，拖曳時每動一步一次就是一路卡到底。
    /// </summary>
    public void ReplaceElement(VectorElement replacement, bool effectsUnchanged = false)
    {
        var index = _elements.FindIndex(e => e.Id == replacement.Id);
        if (index < 0) throw new InvalidOperationException("找不到要替換的物件。");
        var oldBounds = _elements[index].Bounds;
        _elements[index] = replacement;
        InvalidateElement(
            oldBounds.IsEmpty ? replacement.Bounds : SKRectI.Union(oldBounds, replacement.Bounds),
            effectsUnchanged);
    }

    private void InvalidateElement(SKRectI bounds, bool effectsUnchanged = false)
    {
        if (bounds.IsEmpty) return;
        ElementCache.MarkDirty(bounds);
        // effectsUnchanged＝物件與圖層 Offset 走了同一個整數位移：物件在「圖層座標」裡沒動，
        // 效果快取算出來會一模一樣，只是要重新合成到新位置而已（見 TransformSession.TranslateTo）
        if (effectsUnchanged) InvalidateComposite(bounds);
        else Invalidate(bounds);
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
        _pixelSource?.Dispose();
        _pixelSource = null;
    }
}
