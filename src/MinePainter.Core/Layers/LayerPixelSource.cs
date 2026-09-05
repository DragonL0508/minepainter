using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Layers;

/// <summary>
/// 圖層的「原始高清來源」：這層目前的像素其實是 <see cref="Pixels"/> 經 <see cref="Matrix"/>
/// 重取樣出來的結果。縮小落地之後再拉大，就從 <see cref="Pixels"/> 重取樣一次 ——
/// 不是從已經縮小的低解析像素再放大，所以不會愈做愈糊。
///
/// 存活到「這層像素被別的編輯改到」為止（<see cref="Revision"/> 與
/// <see cref="Tiles.TileSurface.Revision"/> 不同即作廢）：筆刷、填滿、貼上、平面化效果之後
/// 原始那份已經對不上圖層，繼續用只會把新畫的東西一起放大。
///
/// 專案檔（.mpp）存的是這份原始像素＋矩陣，開檔時再重取樣回目前的樣子。
/// </summary>
public sealed class LayerPixelSource : IDisposable
{
    /// <summary>原始高清像素。</summary>
    public SKImage Pixels { get; }

    /// <summary>Pixels 在文件座標的位置（以 <see cref="BaseOffset"/> 為圖層位移基準）。</summary>
    public SKRectI Bounds { get; }

    /// <summary>原始 → 目前呈現的累積映射（文件座標，同樣以 BaseOffset 為基準）。</summary>
    public SKMatrix Matrix { get; }

    /// <summary>建立當時的圖層 Offset；之後圖層被平移的話，差值疊到 Matrix 上。</summary>
    public SKPointI BaseOffset { get; }

    /// <summary>變形框「重設角度與比例」要回到的尺寸。</summary>
    public SKSize OriginalSize { get; }

    /// <summary>目前呈現框（文件座標，BaseOffset 基準）。</summary>
    public SKRect TargetRect { get; }

    /// <summary>目前累積的旋轉角度（度）。</summary>
    public float RotationDeg { get; }

    /// <summary>建立當時圖層表面的寫入版本；對不上就是被別的編輯改過了。</summary>
    public int Revision { get; internal set; }

    private bool _detached;

    public LayerPixelSource(SKImage pixels, SKRectI bounds, SKMatrix matrix, SKPointI baseOffset,
        SKRect targetRect, float rotationDeg, SKSize originalSize, int revision)
    {
        Pixels = pixels;
        Bounds = bounds;
        Matrix = matrix;
        BaseOffset = baseOffset;
        TargetRect = targetRect;
        RotationDeg = rotationDeg;
        OriginalSize = originalSize;
        Revision = revision;
    }

    /// <summary>像素的擁有權已交給別人（變形 session 接手），本物件不再釋放它。</summary>
    public void Detach() => _detached = true;

    /// <summary>
    /// 整份文件經過一個仿射映射（翻轉、旋轉 90°、裁切平移…）之後的來源：同一張原圖，
    /// 矩陣多串一段、呈現框跟著映射、圖層位移歸零（呼叫端會把 Offset 設成 0）。
    /// 像素的擁有權轉給新來源（本物件之後不再釋放）；本物件若只是借用，新來源也只是借用。
    /// 回傳的 Revision 未對齊，呼叫端要設。
    /// </summary>
    /// <param name="docMap">文件座標的映射（舊 doc 座標 → 新 doc 座標）。</param>
    /// <param name="layerOffset">映射前圖層的 Offset。</param>
    /// <param name="newBaseOffset">映射後圖層的 Offset（翻轉、裁切歸零；整層縮放落地維持原位移）。</param>
    internal LayerPixelSource Rebased(SKMatrix docMap, SKPointI layerOffset, SKPointI newBaseOffset = default)
    {
        var delta = new SKPointI(layerOffset.X - BaseOffset.X, layerOffset.Y - BaseOffset.Y);
        var matrix = SKMatrix.Concat(docMap, SKMatrix.Concat(SKMatrix.CreateTranslation(delta.X, delta.Y), Matrix));
        matrix = SKMatrix.Concat(SKMatrix.CreateTranslation(-newBaseOffset.X, -newBaseOffset.Y), matrix);
        var target = TargetRect;
        target.Offset(delta.X, delta.Y);
        target = docMap.MapRect(target).Standardized;
        target.Offset(-newBaseOffset.X, -newBaseOffset.Y);

        // 鏡射會把框的角度反過來；旋轉直接加上映射本身的角度
        var det = docMap.ScaleX * docMap.ScaleY - docMap.SkewX * docMap.SkewY;
        var turn = MathF.Atan2(docMap.SkewY, docMap.ScaleX) * 180f / MathF.PI;
        var rotation = (det < 0 ? -RotationDeg : RotationDeg) + turn;

        var owner = !_detached;
        Detach();
        var result = new LayerPixelSource(Pixels, Bounds, matrix, newBaseOffset, target, rotation, OriginalSize, 0);
        if (!owner) result.Detach();
        return result;
    }

    // ---- 在來源解析度上做事（快速模式的去背） ----

    /// <summary>
    /// 原始像素座標 → 圖層座標的矩陣：原始像素先擺到 <see cref="Bounds"/>，走 <see cref="Matrix"/>，
    /// 再減掉 <see cref="BaseOffset"/>（圖層座標 = 文件座標 − 圖層位移）。
    /// </summary>
    internal SKMatrix SourceToLayer =>
        SKMatrix.CreateTranslation(-BaseOffset.X, -BaseOffset.Y)
            .PreConcat(Matrix)
            .PreConcat(SKMatrix.CreateTranslation(Bounds.Left, Bounds.Top));

    /// <summary>一個圖層像素對應幾個原始像素（1 = 沒縮；4 = 原圖是畫布的四倍）。</summary>
    internal float SourcePixelsPerLayerPixel
    {
        get
        {
            var m = SourceToLayer;
            var det = Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY);
            return det <= 1e-12f ? 1f : 1f / MathF.Sqrt(det);
        }
    }

    /// <summary>圖層座標的範圍在原始像素上蓋到哪（原始像素座標，已裁到圖內）；空＝沒交集。</summary>
    internal SKRectI SourceRegionFor(SKRectI layerRect)
    {
        if (!SourceToLayer.TryInvert(out var inverse)) return SKRectI.Empty;
        var corners = inverse.MapPoints(
        [
            new SKPoint(layerRect.Left, layerRect.Top), new SKPoint(layerRect.Right, layerRect.Top),
            new SKPoint(layerRect.Left, layerRect.Bottom), new SKPoint(layerRect.Right, layerRect.Bottom),
        ]);
        var left = (int)MathF.Floor(corners.Min(c => c.X));
        var top = (int)MathF.Floor(corners.Min(c => c.Y));
        var right = (int)MathF.Ceiling(corners.Max(c => c.X));
        var bottom = (int)MathF.Ceiling(corners.Max(c => c.Y));
        var region = SKRectI.Intersect(new SKRectI(left, top, right, bottom), new SKRectI(0, 0, Pixels.Width, Pixels.Height));
        return region.Width <= 0 || region.Height <= 0 ? SKRectI.Empty : region;
    }

    /// <summary>讀原始像素某一塊（premul BGRA）。</summary>
    internal unsafe uint[] ReadPixels(SKRectI region)
    {
        var pixels = new uint[region.Width * region.Height];
        var info = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (uint* p = pixels)
        {
            if (!Pixels.ReadPixels(info, (IntPtr)p, region.Width * 4, region.Left, region.Top))
                throw new InvalidOperationException("讀取原始像素失敗");
        }
        return pixels;
    }

    /// <summary>
    /// 把「原始像素座標」的遮罩直接乘到原圖上（區域外一律透明）。快速模式的去背在來源解析度算出遮罩，
    /// 用這個套回去，邊緣就是來源解析度的邊緣，不是代理畫布放大來的。Revision 未對齊，呼叫端要設。
    /// </summary>
    internal unsafe LayerPixelSource MaskedInSourceSpace(SKRectI region, byte[] mask, CancellationToken ct = default)
    {
        using var bitmap = SKBitmap.FromImage(Pixels);
        var premul = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul
            ? bitmap
            : Convert(bitmap);
        try
        {
            var w = premul.Width;
            var px = (uint*)premul.GetPixels();
            for (var y = 0; y < premul.Height; y++)
            {
                if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                var row = px + y * w;
                var inRow = y >= region.Top && y < region.Bottom;
                for (var x = 0; x < w; x++)
                {
                    ref var p = ref row[x];
                    if (p == 0) continue;
                    if (!inRow || x < region.Left || x >= region.Right)
                    {
                        p = 0;
                        continue;
                    }
                    var m = mask[(y - region.Top) * region.Width + (x - region.Left)];
                    if (m != 255) p = ScalePremul(p, m);
                }
            }
            var image = SKImage.FromBitmap(premul) ?? throw new InvalidOperationException("複製原始像素失敗");
            return new LayerPixelSource(image, Bounds, Matrix, BaseOffset, TargetRect, RotationDeg, OriginalSize, 0);
        }
        finally
        {
            if (!ReferenceEquals(premul, bitmap)) premul.Dispose();
        }

        static SKBitmap Convert(SKBitmap raw)
        {
            var bmp = new SKBitmap(new SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            canvas.DrawBitmap(raw, 0, 0);
            return bmp;
        }
    }

    /// <summary>
    /// 把原圖某一塊的像素整塊換掉（premul），區域外一律透明 —— 硬邊去背在來源解析度算好顏色與 alpha 之後用這個寫回。
    /// Revision 未對齊，呼叫端要設。
    /// </summary>
    internal unsafe LayerPixelSource WithRegionPixels(SKRectI region, uint[] pixels, CancellationToken ct = default)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Pixels.Width, Pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            var w = bitmap.Width;
            var dst = (uint*)bitmap.GetPixels();
            for (var y = region.Top; y < region.Bottom; y++)
            {
                if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                pixels.AsSpan((y - region.Top) * region.Width, region.Width).CopyTo(new Span<uint>(dst + y * w + region.Left, region.Width));
            }
            var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("複製原始像素失敗");
            return new LayerPixelSource(image, Bounds, Matrix, BaseOffset, TargetRect, RotationDeg, OriginalSize, 0);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>原始像素座標的遮罩 → 圖層座標（縮小時取樣平均，不會有鋸齒）。</summary>
    internal byte[] ResampleMaskToLayer(byte[] sourceMask, SKRectI region, SKRectI layerRect) =>
        ResampleMask(sourceMask, region, SourceToLayer, layerRect);

    /// <summary>圖層座標的遮罩 → 原始像素座標（放大時雙線性）。</summary>
    internal byte[] ResampleMaskToSource(byte[] layerMask, SKRectI layerRect, SKRectI region)
    {
        if (!SourceToLayer.TryInvert(out var inverse)) return new byte[region.Width * region.Height];
        return ResampleMask(layerMask, layerRect, inverse, region);
    }

    /// <summary>
    /// 用 Skia 把一張 8 位元遮罩從一個座標系畫到另一個：<paramref name="fromRect"/> 是遮罩在來源座標系的位置，
    /// <paramref name="fromToTo"/> 把來源座標映到目標座標，<paramref name="toRect"/> 是要輸出的目標範圍。
    /// 縮小走 mipmap（High），放大是雙線性。範圍外＝0。
    /// </summary>
    internal static unsafe byte[] ResampleMask(byte[] mask, SKRectI fromRect, SKMatrix fromToTo, SKRectI toRect)
    {
        var result = new byte[toRect.Width * toRect.Height];
        if (fromRect.Width <= 0 || fromRect.Height <= 0 || toRect.Width <= 0 || toRect.Height <= 0) return result;

        var info = new SKImageInfo(toRect.Width, toRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("建立遮罩重取樣表面失敗");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-toRect.Left, -toRect.Top);
        canvas.Concat(ref fromToTo);

        var maskInfo = new SKImageInfo(fromRect.Width, fromRect.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        fixed (byte* p = mask)
        {
            using var image = SKImage.FromPixels(maskInfo, (IntPtr)p, fromRect.Width);
            using var paint = new SKPaint { Color = SKColors.White, FilterQuality = SKFilterQuality.High, IsAntialias = false };
            canvas.DrawImage(image, fromRect.Left, fromRect.Top, paint);
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var pixels = new SKBitmap(info);
        if (!snapshot.ReadPixels(info, pixels.GetPixels(), info.RowBytes, 0, 0))
            throw new InvalidOperationException("讀取遮罩重取樣結果失敗");
        var src = (byte*)pixels.GetPixels();
        for (var i = 0; i < result.Length; i++) result[i] = src[i * 4 + 3];
        return result;
    }

    /// <summary>複製一份（獨立擁有像素）；Revision 未對齊。</summary>
    internal LayerPixelSource Copy()
    {
        using var bitmap = SKBitmap.FromImage(Pixels);
        var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("複製原始像素失敗");
        return new LayerPixelSource(image, Bounds, Matrix, BaseOffset, TargetRect, RotationDeg, OriginalSize, 0);
    }

    /// <summary>
    /// 把圖層座標的遮罩套到原圖上，產生新的來源（原圖像素 × 遮罩；獨立擁有像素）。
    /// 原始像素每一點依矩陣算出它在圖層上的位置，雙線性取遮罩值；
    /// 落在 <paramref name="crop"/> 外的用 <paramref name="outside"/>。
    /// 去背、清除選取、裁切到選取都靠這個讓原圖跟著變。回傳的 Revision 未對齊，呼叫端要設。
    /// </summary>
    internal unsafe LayerPixelSource Masked(SKRectI crop, byte[] mask, byte outside = 0, CancellationToken ct = default)
    {
        using var raw = SKBitmap.FromImage(Pixels);
        SKBitmap bmp;
        if (raw.ColorType != SKColorType.Bgra8888 || raw.AlphaType != SKAlphaType.Premul)
        {
            bmp = new SKBitmap(new SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            canvas.DrawBitmap(raw, 0, 0);
        }
        else bmp = raw;

        try
        {
            var w = bmp.Width;
            var h = bmp.Height;
            var m = Matrix;
            // 原始像素中心（BaseOffset 基準的 doc 座標）→ 矩陣 → 現在的 doc 座標（同基準）
            // → 圖層座標（− BaseOffset）→ 遮罩座標（− crop 左上）；再 −0.5 對齊遮罩像素中心
            var ox = Bounds.Left + 0.5f;
            var oy = Bounds.Top + 0.5f;
            var tx = m.TransX - BaseOffset.X - crop.Left - 0.5f;
            var ty = m.TransY - BaseOffset.Y - crop.Top - 0.5f;
            var px = (uint*)bmp.GetPixels();
            for (var y = 0; y < h; y++)
            {
                if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
                var row = px + y * w;
                var sy = oy + y;
                for (var x = 0; x < w; x++)
                {
                    ref var p = ref row[x];
                    if (p == 0) continue;
                    var sx = ox + x;
                    var mx = m.ScaleX * sx + m.SkewX * sy + tx;
                    var my = m.SkewY * sx + m.ScaleY * sy + ty;
                    p = ScalePremul(p, SampleMask(mask, crop.Width, crop.Height, mx, my, outside));
                }
            }
            var image = SKImage.FromBitmap(bmp) ?? throw new InvalidOperationException("複製原始像素失敗");
            return new LayerPixelSource(image, Bounds, Matrix, BaseOffset, TargetRect, RotationDeg, OriginalSize, 0);
        }
        finally
        {
            if (!ReferenceEquals(bmp, raw)) bmp.Dispose();
        }
    }

    /// <summary>雙線性取遮罩；範圍外視為 outside。</summary>
    private static byte SampleMask(byte[] mask, int w, int h, float x, float y, byte outside)
    {
        if (x <= -1 || y <= -1 || x >= w || y >= h) return outside;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        float At(int xi, int yi) => xi < 0 || yi < 0 || xi >= w || yi >= h ? outside : mask[yi * w + xi];
        var top = At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx;
        var bottom = At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx;
        return (byte)Math.Clamp(MathF.Round(top * (1 - fy) + bottom * fy), 0, 255);
    }

    /// <summary>premul 像素四通道乘上 m/255。</summary>
    internal static uint ScalePremul(uint p, byte m)
    {
        if (m == 255) return p;
        if (m == 0) return 0;
        var mul = m + (m >> 7); // 0..256
        var b = (int)(p & 0xFF) * mul >> 8;
        var g = (int)((p >> 8) & 0xFF) * mul >> 8;
        var r = (int)((p >> 16) & 0xFF) * mul >> 8;
        var a = (int)(p >> 24) * mul >> 8;
        return (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
    }

    public void Dispose()
    {
        if (_detached) return;
        _detached = true;
        Pixels.Dispose();
    }

    /// <summary>
    /// 把原始高清像素依 <see cref="Matrix"/> 重取樣進圖層 —— 開檔時用它重建「縮放後的樣子」
    /// （專案檔存的是原始那份，畫面上的縮放結果在這裡算回來）。
    /// 取樣品質與變形落地時同一份（High），結果才對得上存檔前。須在 Document.SyncRoot 內呼叫。
    /// </summary>
    public void RenderInto(RasterLayer layer)
    {
        var mapped = Matrix.MapRect(new SKRect(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom));
        var docStamp = SKRectI.Ceiling(mapped);
        docStamp.Inflate(2, 2); // 重取樣的邊緣餘裕（同 TransformSession.Apply）
        var layerRect = new SKRectI(
            docStamp.Left - BaseOffset.X, docStamp.Top - BaseOffset.Y,
            docStamp.Right - BaseOffset.X, docStamp.Bottom - BaseOffset.Y);

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForWrite(idx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = idx.ToPixelRect();
            canvas.Translate(-tileRect.Left - BaseOffset.X, -tileRect.Top - BaseOffset.Y);
            var m = Matrix;
            canvas.Concat(ref m);
            canvas.DrawImage(Pixels, Bounds.Left, Bounds.Top, paint);
            canvas.Flush();
            if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
        }
    }
}
