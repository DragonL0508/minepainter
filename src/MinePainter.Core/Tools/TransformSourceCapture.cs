using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>開始變形前擷取原始像素、物件狀態及 Undo 快照，集中處理擷取失敗時的釋放。</summary>
internal static class TransformSourceCapture
{
    private const int MaxContentSide = 16384;

    internal static (List<TransformItem> Items, SKRect Bounds)? Begin(Document doc, LayerNode target, out string? reason)
    {
        reason = null;
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: Collect(g, layers); break;
            default:
                reason = "此圖層類型無法變形";
                return null;
        }

        var items = new List<TransformItem>();
        SKRect? source = null;
        void Accumulate(SKRect r) =>
            source = source is { } a
                ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                    Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                : r;

        lock (doc.SyncRoot)
        {
            foreach (var layer in layers)
            {
                var content = layer.Surface.ExactContentBounds();
                var hasPixels = content.Width > 0 && content.Height > 0;
                if (hasPixels && (content.Width > MaxContentSide || content.Height > MaxContentSide))
                {
                    reason = "圖層內容過大，無法變形";
                    DisposeItems(items);
                    return null;
                }

                SKImage? pixels = null;
                var docRect = SKRectI.Empty;
                if (hasPixels)
                {
                    docRect = new SKRectI(
                        content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                        content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y);
                    var info = new SKImageInfo(docRect.Width, docRect.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info);
                    if (surface == null) continue;
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.Save();
                    surface.Canvas.Translate(-docRect.Left, -docRect.Top);
                    Selections.FloatingSelection.DrawLayerPixels(layer, surface.Canvas, docRect);
                    surface.Canvas.Restore();
                    surface.Canvas.Flush();
                    pixels = surface.Snapshot();
                    Accumulate(new SKRect(docRect.Left, docRect.Top, docRect.Right, docRect.Bottom));
                }

                var elements = layer.HasElements ? layer.Elements.ToArray() : Array.Empty<VectorElement>();
                foreach (var el in elements)
                {
                    // 使用者看到的框：FrameBounds（貼著字），不是 Bounds（失效用的保守外擴，含效果邊、行高餘裕）
                    var b = el.FrameBounds;
                    if (b.IsEmpty)
                    {
                        var pb = el.Bounds;
                        b = new SKRect(pb.Left, pb.Top, pb.Right, pb.Bottom);
                    }
                    Accumulate(b);
                }

                if (pixels == null && elements.Length == 0) continue;
                items.Add(new TransformItem
                {
                    Layer = layer,
                    Pixels = pixels,
                    SrcBounds = docRect,
                    BaseOffset = layer.Offset,
                    Before = layer.Surface.Snapshot(),
                    StartElements = elements,
                    LastStamp = docRect,
                });
            }
        }

        if (items.Count == 0 || source is not { } src || src.Width < 1 || src.Height < 1)
        {
            reason ??= "沒有可變形的內容";
            DisposeItems(items);
            return null;
        }
        return (items, src);
    }

    internal static List<TransformItem>? Resume(Document doc, LayerNode target, TransformResume resume)
    {
        if (!ReferenceEquals(resume.Target, target)) return null;
        var layers = new List<RasterLayer>();
        switch (target)
        {
            case RasterLayer r: layers.Add(r); break;
            case GroupLayer g: Collect(g, layers); break;
            default: return null;
        }
        if (layers.Count != resume.Items.Length) return null;
        for (var i = 0; i < layers.Count; i++)
        {
            if (!ReferenceEquals(layers[i], resume.Items[i].Layer)) return null;
        }

        var items = new List<TransformItem>();
        lock (doc.SyncRoot)
        {
            foreach (var (layer, pixels, srcBounds) in resume.Items)
            {
                if (layer.Document == null) { DisposeItems(items); return null; }
                var content = layer.Surface.ExactContentBounds();
                var current = content.Width > 0 && content.Height > 0
                    ? new SKRectI(
                        content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
                        content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y)
                    : SKRectI.Empty;
                items.Add(new TransformItem
                {
                    Layer = layer,
                    Pixels = pixels,
                    SrcBounds = srcBounds,
                    BaseOffset = layer.Offset,
                    Before = layer.Surface.Snapshot(),
                    StartElements = layer.HasElements ? layer.Elements.ToArray() : Array.Empty<VectorElement>(),
                    LastStamp = current,
                    OwnsPixels = false, // 像素是圖層 LayerPixelSource 那份，session 只是借用
                });
            }
        }

        return items;
    }

    private static void Collect(GroupLayer group, List<RasterLayer> into)
    {
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case RasterLayer r: into.Add(r); break;
                case GroupLayer g: Collect(g, into); break;
            }
        }
    }

    private static void DisposeItems(List<TransformItem> items)
    {
        foreach (var item in items)
        {
            item.Pixels?.Dispose();
            item.Before.Dispose();
        }
        items.Clear();
    }
}
