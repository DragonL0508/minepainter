using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 圖層樹節點基底。所有樹結構與屬性的變更都在 UI thread、Document.SyncRoot 鎖內進行，
/// 之後透過 Invalidate 通知合成端。
///
/// 非破壞性效果堆疊掛在這一層（不是只有點陣圖層）：群組也能套效果，
/// 作用對象是「群組合成起來的樣子」，等於整組一起吃 —— 外框、陰影會包住整組而不是每層各一份。
/// </summary>
public abstract class LayerNode
{
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    public GroupLayer? Parent { get; internal set; }
    public Document? Document { get; internal set; }

    /// <summary>此節點的內容邊界（文件座標，tile 粒度即可）。</summary>
    public abstract SKRectI ContentBounds { get; }

    // ---- 非破壞性效果堆疊 ----

    private IReadOnlyList<LayerEffect> _effects = [];

    /// <summary>這個節點能不能套效果堆疊（調整圖層沒有自己的像素，不行）。</summary>
    public virtual bool CanHaveEffects => true;

    /// <summary>套在這個節點上的效果（由先到後）。不可變清單：換整份參考（undo 同構）。</summary>
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

    /// <summary>
    /// 效果快取的座標系相對文件原點的偏移：點陣圖層是它的 Offset（快取跟著圖層走，
    /// 平移不必重算），群組則直接用文件座標。
    /// </summary>
    public virtual SKPointI EffectOffset => SKPointI.Empty;

    /// <summary>
    /// 效果快取此刻是否代表這個節點的畫面。
    /// 手勢進行中一律當作「還沒算好」：合成器改畫原始內容（見 Document.InteractiveGesture）。
    /// </summary>
    public bool EffectsRendered =>
        HasActiveEffects && FxCache.Rendered && Document is not { InteractiveGesture: true };

    /// <summary>換整份效果清單（在 Document.SyncRoot 內），整個節點重算。</summary>
    public void SetEffects(IReadOnlyList<LayerEffect> effects)
    {
        _effects = effects;
        InvalidateEffects();
    }

    /// <summary>效果堆疊整份重算並重新合成。</summary>
    public void InvalidateEffects()
    {
        FxCache.MarkAllDirty();
        if (!HasActiveEffects) FxCache.Rendered = false;
        var doc = Document;
        if (doc != null) InvalidateComposite(doc.Bounds);
    }

    /// <summary>
    /// 內容變了：本節點有效果堆疊時先標髒它的效果快取（合成器會先重算效果），
    /// 再沿途標髒所有祖先群組的快取並通知合成器。
    /// </summary>
    public void Invalidate(SKRectI docRect)
    {
        if (HasActiveEffects)
        {
            var off = EffectOffset;
            FxCache.MarkDirty(new SKRectI(
                docRect.Left - off.X, docRect.Top - off.Y,
                docRect.Right - off.X, docRect.Bottom - off.Y));
        }
        InvalidateComposite(docRect);
    }

    /// <summary>
    /// 只重新合成（本節點的效果快取已是最新；效果渲染器寫回後用這個，避免自己把自己標髒）。
    /// 祖先群組若有效果堆疊，它的來源就是「這一組合成起來的樣子」—— 內容變了它也得重算。
    /// </summary>
    public void InvalidateComposite(SKRectI docRect)
    {
        for (var g = Parent; g != null; g = g.Parent)
        {
            g.Cache.MarkDirty(docRect);
            if (g.HasActiveEffects) g.FxCache.MarkDirty(docRect); // 群組效果快取＝文件座標
        }
        Document?.NotifyChanged(docRect);
    }

    /// <summary>通知整個節點範圍失效（例如可見性/混合模式改變）。</summary>
    public void InvalidateAll()
    {
        var doc = Document;
        if (doc != null) Invalidate(new SKRectI(0, 0, doc.Width, doc.Height));
    }

    internal virtual void AttachToDocument(Document? doc) => Document = doc;
}
