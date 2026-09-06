using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 一個圖層（或整個群組）「畫面上的樣子」算成一塊像素：效果堆疊算完、文字／形狀物件畫上去、群組整組合成。
/// 匯出成沒有這些概念的格式（.pdn、對不上的 .psd 圖層）時用。回傳 doc 座標的範圍與 premul BGRA；空層回 null。
/// 效果重算在鎖外做（RenderLayerNow 自己取鎖），讀像素時短暫持鎖。
/// </summary>
internal static class LayerFlattener
{
    public static (SKRectI Rect, uint[]? Premul) Render(Document doc, LayerNode node)
    {
        if (node.HasActiveEffects) LayerEffectRenderer.RenderLayerNow(doc, node, Compositor.StaticGroupSourceLocked);

        lock (doc.SyncRoot)
        {
            if (node.HasActiveEffects && node.FxCache.Rendered)
            {
                var region = node.FxCache.Surface.ExactContentBounds();
                var rect = Offset(region, node.EffectOffset);
                return (rect, region.Width > 0 && region.Height > 0 ? LayerEffectRenderer.ReadPixels(node.FxCache.Surface, region) : null);
            }
            if (node is RasterLayer raster)
            {
                var region = LayerEffectRenderer.ContentRegion(raster);
                var rect = Offset(region, raster.Offset);
                return (rect, region.Width > 0 && region.Height > 0 ? LayerEffectRenderer.ReadPixelsWithElements(raster, region) : null);
            }
            if (node is GroupLayer group)
            {
                var rect = group.ContentBounds;
                return (rect, rect.Width > 0 && rect.Height > 0 ? Compositor.StaticGroupSourceLocked(group, rect) : null);
            }
            return (SKRectI.Empty, null);
        }
    }

    /// <summary>把一塊 premul 像素貼進整張畫布大小的直通 alpha BGRA 緩衝（畫布外的裁掉）。</summary>
    public static unsafe void BlitStraight(byte[] canvasBgra, int canvasWidth, int canvasHeight, SKRectI rect, uint[] premul)
    {
        var straight = new byte[rect.Width * rect.Height * 4];
        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* src = premul)
        fixed (byte* dst = straight)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)src, rect.Width * 4);
            if (!pixmap.ReadPixels(info.WithAlphaType(SKAlphaType.Unpremul), (IntPtr)dst, rect.Width * 4))
                throw new InvalidOperationException("像素轉換失敗（預乘 → 直通 alpha）。");
        }
        var inter = SKRectI.Intersect(rect, new SKRectI(0, 0, canvasWidth, canvasHeight));
        for (var y = inter.Top; y < inter.Bottom; y++)
        {
            var srcOffset = ((y - rect.Top) * rect.Width + (inter.Left - rect.Left)) * 4;
            var dstOffset = (y * canvasWidth + inter.Left) * 4;
            Buffer.BlockCopy(straight, srcOffset, canvasBgra, dstOffset, inter.Width * 4);
        }
    }

    private static SKRectI Offset(SKRectI rect, SKPointI by) =>
        new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);
}
