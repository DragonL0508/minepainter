using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Selections;

/// <summary>
/// 物件選取：使用者用矩形／橢圓／套索圈個大概，這裡在作用中圖層上找出圈內的主體，
/// 回傳貼著主體邊緣的選取。圈外一律當背景（<see cref="GrabCut"/> 需要背景樣本，
/// 所以會多讀圈外一圈像素當參考），圈內是「可能前景」；套索圈得越緊越準。
/// 結果邊緣用原圖引導濾波貼回真實像素邊，內部填實。
/// </summary>
public static class ObjectSelector
{
    /// <summary>
    /// 在 layer 上、以 shape（doc 座標）為初始範圍找主體。找不到（圈內沒有內容或全被判成背景）回 null。
    /// 呼叫端要持 doc.SyncRoot。
    /// </summary>
    public static SelectionMask? Select(RasterLayer layer, SelectionMask shape, SKRectI docBounds,
        CancellationToken ct = default)
    {
        if (shape.IsEmpty) return null;
        var bounds = SKRectI.Intersect(shape.Bounds, docBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        // 多讀一圈當背景參考：至少 16px、或範圍的四分之一
        var margin = Math.Max(16, Math.Max(bounds.Width, bounds.Height) / 4);
        var region = SKRectI.Intersect(
            new SKRectI(bounds.Left - margin, bounds.Top - margin, bounds.Right + margin, bounds.Bottom + margin),
            docBounds);
        var w = region.Width; var h = region.Height;

        // doc 座標 → 圖層座標
        var layerRect = new SKRectI(region.Left - layer.Offset.X, region.Top - layer.Offset.Y,
            region.Right - layer.Offset.X, region.Bottom - layer.Offset.Y);
        var pixels = BackgroundRemovalCommand.ReadRegion(layer.Surface, layerRect);

        var coverage = new byte[w * h];
        var trimap = new byte[w * h];
        var anyContent = false;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var c = shape.CoverageAt(region.Left + x, region.Top + y);
            coverage[i] = c;
            var opaque = pixels[i] >> 24 != 0;
            trimap[i] = c >= 128 && opaque ? GrabCut.ProbableForeground : GrabCut.Background;
            if (c >= 128 && opaque) anyContent = true;
        }
        if (!anyContent) return null;

        var binary = GrabCut.Run(pixels, w, h, trimap, ct: ct);
        var found = false;
        foreach (var b in binary) if (b != 0) { found = true; break; }
        if (!found) return null;

        // 邊緣貼回原圖、內部填實，再限制在使用者圈的範圍內
        var scale = Math.Max(1, (int)MathF.Ceiling(Math.Max(w, h) / (float)GrabCut.MaxSide));
        var radius = Math.Max(4, 3 * scale);
        var soft = GuidedFilter.Refine(binary, pixels, w, h, radius, ct: ct);
        var mask = BackgroundRemover.SolidifyCore(soft, binary, w, h, radius);
        for (var i = 0; i < mask.Length; i++)
            if (coverage[i] != 255) mask[i] = (byte)(mask[i] * coverage[i] / 255);

        var surface = new MaskSurface();
        var any = false;
        foreach (var idx in TileIndex.CoveringRect(region))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, region);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            MaskTile? tile = null;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = (y - region.Top) * w;
                for (var x = inter.Left; x < inter.Right; x++)
                {
                    var m = mask[row + (x - region.Left)];
                    if (m == 0) continue;
                    tile ??= surface.GetForWrite(idx);
                    tile.Alpha[(y - tileRect.Top) * MaskTile.Size + (x - tileRect.Left)] = m;
                    any = true;
                }
            }
        }
        if (!any) return null;
        surface.ExtendBounds(region);
        return SelectionMask.FromMaskSurface(surface);
    }
}
