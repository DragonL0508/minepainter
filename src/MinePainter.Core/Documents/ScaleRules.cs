using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 「整份文件縮放」的規則：像素怎麼縮（有原始高清來源就從它重畫、沒有就重新取樣）、
/// 縮小時留不留原始那份、效果堆疊裡的像素長度參數與遮罩、文字自己的外框／陰影／光暈。
///
/// 少了這一段，放大之後外框還是原本的粗細、陰影還是原本的距離、遮罩只蓋到左上角 ——
/// 看起來就不是同一張圖了。
/// 調整影像大小（<see cref="ImageCommands"/>）與快速模式的輸出／開檔轉模式
/// （<see cref="OutputRender"/>）共用這裡，兩條路才會給出同樣的結果。
/// </summary>
internal static class ScaleRules
{
    /// <summary>
    /// 縮小時保留「原始高清那份」的單層面積上限（像素數）。4K 一層是 830 萬，8K 是 3300 萬；
    /// 超過就不留 —— 那種尺寸的原圖留在記憶體裡代價太高，輸出時只好走放大。
    /// </summary>
    internal const long MaxSourcePixels = 40_000_000;

    /// <summary>
    /// 整份文件一次縮放能新留下的原始來源總量（像素數，約 512 MB）。
    /// 20 層 4K 轉成快速模式若每層都留，記憶體比轉之前還高；超過預算的層就不留，輸出走放大。
    /// </summary>
    internal const long SourceBudgetPixels = 128_000_000;

    /// <summary>從原始來源重畫時，畫布外至少要留多少（效果可能吃到畫布外的內容）。</summary>
    private const int MinOutsideMargin = 256;

    /// <summary>一次縮放共用的狀態：還剩多少「留原始來源」的預算。</summary>
    internal sealed class Budget
    {
        public long Remaining = SourceBudgetPixels;

        public bool TryTake(long pixels)
        {
            if (pixels > MaxSourcePixels || pixels > Remaining) return false;
            Remaining -= pixels;
            return true;
        }
    }

    // ---- 像素 ----

    /// <summary>
    /// 把一層的像素縮放成新表面，並決定新表面的「原始高清來源」：
    /// 　• 這層還留著有效的原始來源 → 直接從原圖以新比例重畫（不會愈縮愈糊、放大也清晰），
    /// 　　新來源＝同一張原圖 × 串接後的矩陣（<paramref name="shareSource"/> 決定共用還是複製像素）
    /// 　• 沒有 → 重新取樣；縮小時把現在的像素拍下來當來源（放大不會有更清楚的來源）
    /// 回傳的來源 Revision 尚未對齊，呼叫端換好表面後要設成新表面的 Revision。
    /// </summary>
    /// <param name="clipDoc">從原圖重畫時只留這個範圍（新文件座標；畫布外整份留著在放大後可能非常大）。</param>
    /// <param name="shareSource">
    /// true＝新來源與舊來源共用同一張 SKImage（新來源不擁有它，舊來源仍是擁有者，給 undo 留著）；
    /// false＝複製一份（跨文件用）。
    /// </param>
    internal static (TileSurface Surface, LayerPixelSource? Source) ScaleLayerPixels(
        RasterLayer layer, float sx, float sy, ResampleMode resample, SKRectI clipDoc,
        Budget budget, bool shareSource)
    {
        if (layer.ValidPixelSource is { } src && src.Pixels != null)
        {
            var chained = ChainSource(layer, src, sx, sy, shareSource);
            var surface = RenderSource(chained, clipDoc, Reach(layer, (Math.Abs(sx) + Math.Abs(sy)) / 2f));
            return (surface, chained);
        }

        var keep = sx < 0.999f || sy < 0.999f ? CaptureSource(layer, sx, sy, budget) : null;
        return (ImageCommands.ScaleSurface(layer, sx, sy, resample), keep);
    }

    /// <summary>原始來源跟著整份縮放：同一張原圖，矩陣多串一段（含圖層後來的平移）。</summary>
    private static LayerPixelSource ChainSource(RasterLayer layer, LayerPixelSource src, float sx, float sy,
        bool shareSource)
    {
        // 原始 → 目前呈現（doc 座標，BaseOffset 基準）→ 圖層後來的平移 → 新比例
        var delta = new SKPointI(layer.Offset.X - src.BaseOffset.X, layer.Offset.Y - src.BaseOffset.Y);
        var matrix = SKMatrix.Concat(
            SKMatrix.CreateScale(sx, sy),
            SKMatrix.Concat(SKMatrix.CreateTranslation(delta.X, delta.Y), src.Matrix));
        var target = src.TargetRect;
        target.Offset(delta.X, delta.Y);
        var targetRect = SKRect.Create(target.Left * sx, target.Top * sy, target.Width * sx, target.Height * sy);

        var pixels = shareSource ? src.Pixels : CopyImage(src.Pixels);
        var chained = new LayerPixelSource(pixels, src.Bounds, matrix, SKPointI.Empty, targetRect,
            src.RotationDeg, src.OriginalSize, 0);
        if (shareSource) chained.Detach(); // 擁有者仍是舊來源（undo 會把它放回去）
        return chained;
    }

    private static SKImage CopyImage(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("複製原始像素失敗");
    }

    /// <summary>
    /// 把原始來源依矩陣畫成新表面。畫布外只留 <paramref name="margin"/>（效果吃得到的範圍），
    /// 不然一張旋轉過的 8K 原圖在 4K 輸出上整份留著會非常大。
    /// </summary>
    private static TileSurface RenderSource(LayerPixelSource src, SKRectI clipDoc, int margin)
    {
        var result = new TileSurface();
        var bounds = new SKRect(src.Bounds.Left, src.Bounds.Top, src.Bounds.Right, src.Bounds.Bottom);
        var dest = SKRectI.Round(src.Matrix.MapRect(bounds));
        dest.Inflate(2, 2); // 重取樣的邊緣餘裕（同 LayerPixelSource.RenderInto）
        var limit = clipDoc;
        limit.Inflate(margin, margin);
        dest = SKRectI.Intersect(dest, limit);
        if (dest.Width <= 0 || dest.Height <= 0) return result;

        var info = new SKImageInfo(dest.Width, dest.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return result;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-dest.Left, -dest.Top);
        var matrix = src.Matrix;
        canvas.Concat(ref matrix);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.DrawImage(src.Pixels, src.Bounds.Left, src.Bounds.Top, paint);
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        using var pixmap = bitmap.PeekPixels();
        result.CopyFrom(pixmap, new SKPointI(dest.Left, dest.Top));
        return result;
    }

    /// <summary>
    /// 這層的效果最遠會吃到畫布外多少像素（縮放後）：外框寬度、模糊半徑、陰影距離加總，再留一倍餘裕。
    /// 沒效果也至少留 <see cref="MinOutsideMargin"/>。
    /// </summary>
    private static int Reach(LayerNode layer, float scale)
    {
        if (!layer.HasEffects) return MinOutsideMargin;
        double reach = 0;
        foreach (var entry in layer.Effects)
        {
            if (!entry.Enabled) continue;
            foreach (var def in entry.Effect.Parameters)
                if (def is SliderParam { Geometric: true } slider) reach += Math.Abs(slider.Get(entry.Effect));
        }
        return Math.Max(MinOutsideMargin, (int)Math.Ceiling(reach * scale * 2));
    }

    /// <summary>
    /// 把圖層現在的像素拍成「原始高清來源」，讓之後輸出時能從它重畫。
    /// 只在縮小時有意義：放大不會有更清楚的來源。超過預算就不留（回 null）。
    /// </summary>
    private static LayerPixelSource? CaptureSource(RasterLayer layer, float sx, float sy, Budget budget)
    {
        var content = layer.Surface.ExactContentBounds();
        if (content.Width <= 0 || content.Height <= 0) return null;
        if (!budget.TryTake((long)content.Width * content.Height)) return null;

        var docRect = new SKRectI(
            content.Left + layer.Offset.X, content.Top + layer.Offset.Y,
            content.Right + layer.Offset.X, content.Bottom + layer.Offset.Y);

        using var bitmap = ImageCommands.ReadRegion(layer.Surface, content);
        var image = SKImage.FromBitmap(bitmap);
        if (image == null) return null;

        return new LayerPixelSource(
            image,
            docRect,
            SKMatrix.CreateScale(sx, sy),
            SKPointI.Empty,
            SKRect.Create(docRect.Left * sx, docRect.Top * sy, docRect.Width * sx, docRect.Height * sy),
            0f,
            new SKSize(docRect.Width, docRect.Height),
            0);
    }

    // ---- 效果堆疊 ----

    /// <summary>
    /// 效果跟著縮：像素長度的參數乘上比例，套用時的選取遮罩（doc 座標）也重新取樣到新尺寸。
    /// </summary>
    /// <param name="clampToSlider">
    /// true＝結果夾在滑桿範圍內（留在文件裡、之後還要在 UI 上調的）；
    /// false＝不夾（輸出用的暫時複本：4K 上外框該多粗就多粗，效果內部自己有上限）。
    /// </param>
    internal static LayerEffect ScaleEffect(LayerEffect entry, float sx, float sy, bool clampToSlider = true)
    {
        var k = (Math.Abs(sx) + Math.Abs(sy)) / 2f;
        var mask = entry.Mask;
        if (Math.Abs(sx - 1f) < 0.001f && Math.Abs(sy - 1f) < 0.001f) return entry;

        object current = entry.Effect;
        foreach (var def in entry.Effect.Parameters)
        {
            if (def is not SliderParam { Geometric: true } slider) continue;
            var value = slider.Get(current) * k;
            value = clampToSlider ? Math.Clamp(value, slider.Min, slider.Max) : Math.Max(value, slider.Min);
            current = slider.With(current, value);
        }
        return entry with { Effect = (IEffect)current, Mask = mask == null ? null : ScaleMask(mask, sx, sy) };
    }

    /// <summary>doc 座標的 8-bit 遮罩重新取樣到新尺寸（高品質；邊緣會有一格內的過渡）。</summary>
    internal static unsafe MaskSurface ScaleMask(MaskSurface mask, float sx, float sy)
    {
        var result = new MaskSurface();
        var bounds = mask.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return result;

        var dstRect = SKRectI.Round(new SKRect(bounds.Left * sx, bounds.Top * sy, bounds.Right * sx, bounds.Bottom * sy));
        if (dstRect.Width < 1) dstRect.Right = dstRect.Left + 1;
        if (dstRect.Height < 1) dstRect.Bottom = dstRect.Top + 1;

        using var src = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Alpha8, SKAlphaType.Unpremul));
        var srcSpan = new Span<byte>((void*)src.GetPixels(), bounds.Width * bounds.Height);
        srcSpan.Clear();
        foreach (var (idx, tile) in mask.Tiles)
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, bounds);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                tile.Alpha.AsSpan((y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left), inter.Width)
                    .CopyTo(srcSpan.Slice((y - bounds.Top) * bounds.Width + (inter.Left - bounds.Left), inter.Width));
            }
        }

        using var dst = new SKBitmap(new SKImageInfo(dstRect.Width, dstRect.Height, SKColorType.Alpha8, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(src, SKRect.Create(0, 0, dstRect.Width, dstRect.Height), paint);
            canvas.Flush();
        }

        var dstSpan = dst.GetPixelSpan();
        foreach (var idx in TileIndex.CoveringRect(dstRect))
        {
            var tileRect = idx.ToPixelRect();
            var inter = SKRectI.Intersect(tileRect, dstRect);
            if (inter.Width <= 0 || inter.Height <= 0) continue;
            MaskTile? tile = null;
            for (var y = inter.Top; y < inter.Bottom; y++)
            {
                var row = dstSpan.Slice((y - dstRect.Top) * dstRect.Width + (inter.Left - dstRect.Left), inter.Width);
                if (tile == null)
                {
                    var any = false;
                    foreach (var a in row)
                    {
                        if (a == 0) continue;
                        any = true;
                        break;
                    }
                    if (!any) continue;
                    tile = result.GetForWrite(idx);
                }
                row.CopyTo(tile.Alpha.AsSpan((y - tileRect.Top) * MaskTile.Size + (inter.Left - tileRect.Left), inter.Width));
            }
        }
        result.ExtendBounds(dstRect);
        result.RemoveEmptyTiles();
        return result;
    }

    // ---- 物件 ----

    /// <summary>物件（文字／形狀）縮放：外觀上的像素長度也要跟著縮。</summary>
    internal static VectorElement ScaleElement(VectorElement element, SKMatrix matrix, float sx, float sy)
    {
        var scaled = element.TransformedBy(matrix, sx, sy, 0f);
        if (scaled is not TextElement text) return scaled;

        var k = (Math.Abs(sx) + Math.Abs(sy)) / 2f;
        return text with
        {
            Stroke = ScaleStroke(text.Stroke, k),
            Shadow = text.Shadow is { } shadow
                ? shadow with
                {
                    Distance = shadow.Distance * k,
                    Blur = shadow.Blur * k,
                    Spread = shadow.Spread * k,
                }
                : null,
            Glow = text.Glow is { } glow
                ? glow with { Size = glow.Size * k, Spread = glow.Spread * k }
                : null,
        };
    }

    private static TextStroke? ScaleStroke(TextStroke? stroke, float k)
    {
        if (stroke == null) return null;
        var layers = stroke.Layers().Select(s => s with { Width = s.Width * k }).ToList();
        return TextStroke.FromLayers(layers);
    }
}
