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
