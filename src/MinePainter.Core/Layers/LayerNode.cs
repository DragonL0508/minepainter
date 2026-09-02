using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 圖層樹節點基底。所有樹結構與屬性的變更都在 UI thread、Document.SyncRoot 鎖內進行，
/// 之後透過 Invalidate 通知合成端。
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

    /// <summary>
    /// 內容變了：圖層有效果堆疊時先標髒效果快取（合成器會先重算效果），
    /// 再沿途標髒所有祖先群組的快取並通知合成器。
    /// </summary>
    public void Invalidate(SKRectI docRect)
    {
        if (this is RasterLayer raster && raster.HasActiveEffects)
        {
            raster.FxCache.MarkDirty(new SKRectI(
                docRect.Left - raster.Offset.X, docRect.Top - raster.Offset.Y,
                docRect.Right - raster.Offset.X, docRect.Bottom - raster.Offset.Y));
        }
        InvalidateComposite(docRect);
    }

    /// <summary>只重新合成（效果快取已是最新；效果渲染器寫回後用這個，避免自己把自己標髒）。</summary>
    public void InvalidateComposite(SKRectI docRect)
    {
        for (var g = Parent; g != null; g = g.Parent)
            g.Cache.MarkDirty(docRect);
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
