using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Selections;

/// <summary>
/// 浮動選取內容：從某圖層「提起」的一塊像素，可自由移動與縮放，
/// 直到提交（切換工具／取消選取／換圖層）才烙回該圖層。
///
/// 提起的瞬間原處像素就被清掉，所以拖曳時看得到底下的圖層 —— paint.net 的行為。
/// 生命週期由 EditorSession 管理；只在 UI thread 操作。
/// </summary>
public sealed class FloatingSelection : IDisposable
{
    /// <summary>提起來源的圖層（物件屬於圖層，浮動內容也是）。</summary>
    public Guid LayerId { get; }

    /// <summary>提起的像素（左上角對齊 SourceBounds）。</summary>
    public SKImage Pixels { get; }

    /// <summary>原始位置（doc 座標）。</summary>
    public SKRectI SourceBounds { get; }

    /// <summary>目前的目標矩形（doc 座標）；移動與縮放都改這個。</summary>
    public SKRect TargetRect { get; set; }

    /// <summary>提起前的圖層快照，供提交時產生單一 undo 步驟。</summary>
    internal TileSnapshot BeforeSnapshot { get; }

    /// <summary>提起時的選取範圍（提交時要還原回去，也是選取框跟著走的基準）。</summary>
    public SelectionMask SourceSelection { get; }

    /// <summary>
    /// 貼上的內容（來自剪貼簿）而非從圖層提起：原處沒有被挖走像素，
    /// 沒移動過也要落地（Lift 的「沒動過＝取消」捷徑不適用），取消＝直接丟棄。
    /// </summary>
    public bool IsPasted { get; private init; }

    /// <summary>
    /// 整個圖層內容的提起（拖圖層內容框的角，GIMP「縮放圖層」的對應）：
    /// 來源不是使用者建立的選取，落地／取消時都不得把矩形遮罩留成 session 的選取。
    /// </summary>
    public bool IsWholeContent { get; private init; }

    /// <summary>提交時的 history 標籤。</summary>
    public string CommitLabel =>
        IsPasted ? "貼上" : IsWholeContent ? "縮放圖層內容" : "移動選取內容";

    private readonly SKPath? _sourceOutline;
    private bool _disposed;
    private bool _pixelsDetached;

    private FloatingSelection(Guid layerId, SKImage pixels, SKRectI sourceBounds,
        TileSnapshot before, SelectionMask sourceSelection)
    {
        LayerId = layerId;
        Pixels = pixels;
        SourceBounds = sourceBounds;
        TargetRect = new SKRect(sourceBounds.Left, sourceBounds.Top, sourceBounds.Right, sourceBounds.Bottom);
        BeforeSnapshot = before;
        SourceSelection = sourceSelection;
        _sourceOutline = sourceSelection.OutlinePath is { } p ? new SKPath(p) : null;
    }

    /// <summary>SourceBounds → TargetRect 的變換（選取框與像素共用同一個矩陣）。</summary>
    public SKMatrix TransformMatrix
    {
        get
        {
            if (SourceBounds.Width <= 0 || SourceBounds.Height <= 0) return SKMatrix.Identity;
            var sx = TargetRect.Width / SourceBounds.Width;
            var sy = TargetRect.Height / SourceBounds.Height;
            return SKMatrix.CreateScaleTranslation(sx, sy,
                TargetRect.Left - SourceBounds.Left * sx,
                TargetRect.Top - SourceBounds.Top * sy);
        }
    }

    /// <summary>
    /// 目前位置的選取輪廓（螞蟻線用）。
    /// 拖曳期間只變換路徑、不重新柵格化 —— 多次縮放才不會越來越糊，也不吃效能。
    /// </summary>
    public SKPath? GetTransformedOutline()
    {
        if (_sourceOutline == null) return null;
        var result = new SKPath();
        _sourceOutline.Transform(TransformMatrix, result);
        return result;
    }

    /// <summary>
    /// 從圖層提起選取範圍內的像素（原處清空）。無內容時回傳 null。
    /// 須在 Document.SyncRoot 內呼叫。
    /// <paramref name="wholeContent"/>：這是「整個圖層內容」的提起而非選取（見 <see cref="IsWholeContent"/>）。
    /// </summary>
    public static FloatingSelection? Lift(RasterLayer layer, SelectionMask selection, bool wholeContent = false)
    {
        var docBounds = selection.Bounds;
        if (docBounds.Width <= 0 || docBounds.Height <= 0) return null;

        var info = new SKImageInfo(docBounds.Width, docBounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface == null) return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 1) 先把圖層像素畫進來（doc 座標 → 影像座標）
        canvas.Save();
        canvas.Translate(-docBounds.Left, -docBounds.Top);
        DrawLayerPixels(layer, canvas, docBounds);
        canvas.Restore();

        // 2) 以選取遮罩裁切（DstIn：只留選取內）
        ApplySelectionMask(selection, canvas, docBounds);
        canvas.Flush();

        var lifted = surface.Snapshot();
        if (lifted == null) return null;

        // 3) 從原圖層挖掉這塊（DstOut）
        var before = layer.Surface.Snapshot();
        EraseFromLayer(layer, selection, docBounds);

        return new FloatingSelection(layer.Id, lifted, docBounds, before, selection)
        {
            IsWholeContent = wholeContent,
        };
    }

    /// <summary>
    /// 把外部影像（剪貼簿）包成浮動內容：不動圖層像素，快照只是提交時算 undo 的基準。
    /// 接手 <paramref name="pixels"/> 的擁有權。須在 Document.SyncRoot 內呼叫。
    /// </summary>
    public static FloatingSelection CreatePasted(RasterLayer layer, SKImage pixels,
        SKRectI bounds, SelectionMask selection) =>
        new(layer.Id, pixels, bounds, layer.Surface.Snapshot(), selection) { IsPasted = true };

    /// <summary>
    /// 把浮動內容以目前 TargetRect 畫到 canvas。須在 SyncRoot 內呼叫。
    ///
    /// <paramref name="preview"/>＝畫面預覽（會一直重畫）：縮放取樣降一級換速度，
    /// 落地時再用高品質重取樣一次 —— 實測 256×256 一格的重取樣成本
    /// High+AA 約 1.5ms、Low 約 0.15ms，純平移（None）約 0.02ms。
    /// 純平移時兩者都用 None：沒有縮放就沒有重取樣的必要，還能避免子像素糊化。
    /// </summary>
    public void DrawInto(SKCanvas canvas, bool preview = false)
    {
        var rect = TargetRect; // 只讀一次：render thread 也會走這裡，UI thread 可能同時在改
        var scaled = rect.Width != SourceBounds.Width || rect.Height != SourceBounds.Height;
        using var paint = new SKPaint
        {
            FilterQuality = !scaled ? SKFilterQuality.None
                : preview ? SKFilterQuality.Low
                : SKFilterQuality.High,
            IsAntialias = scaled,
        };
        canvas.DrawImage(Pixels, rect, paint);
    }

    /// <summary>
    /// 浮動內容能不能改由 render thread 直接覆疊上去，而不必讓合成器逐格重畫。
    ///
    /// 判準是「兩條路徑的結果完全相同」：SrcOver 有結合律，只要浮動內容所在的圖層
    /// 之後不再有任何東西被合成上去（它是最上面那個看得見的東西），
    /// 而且它與所有祖先群組都是 100% 不透明的正常混合，那麼
    /// 　　完整合成 ＝（不含浮動內容的合成）⊕ 浮動內容
    /// 覆疊路徑因此是精確的，不是近似。
    ///
    /// 不成立時（上面還有別的圖層、或該層有不透明度/混合模式）就退回合成器路徑 ——
    /// 浮動內容必須夾在正確的層序裡，畫面正確優先於流暢。
    /// 須在 Document.SyncRoot 內呼叫。
    /// </summary>
    internal static bool CanOverlay(LayerNode layer)
    {
        for (var node = layer; node.Parent != null; node = node.Parent)
        {
            if (!node.IsVisible || node.Opacity < 1f || node.BlendMode != BlendMode.Normal)
                return false;

            // 同層中排在它之後的（＝畫在它上面的）都必須看不見
            var siblings = node.Parent.Children;
            for (var i = node.Parent.IndexOf(node) + 1; i < siblings.Count; i++)
            {
                if (siblings[i].IsVisible && siblings[i].Opacity > 0) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 交出像素的擁有權（<see cref="Dispose"/> 之後不再釋放它）。
    /// 用於落地/取消後的殘影 —— 見 EditorSession 的 OverlayGhost。
    /// </summary>
    internal SKImage DetachPixels()
    {
        _pixelsDetached = true;
        return Pixels;
    }

    /// <summary>目前位置佔用的 doc 範圍（含縮放取樣的邊緣餘裕）。</summary>
    public SKRectI TargetBounds
    {
        get
        {
            var target = SKRectI.Round(TargetRect);
            target.Inflate(2, 2); // 縮放取樣的邊緣餘裕
            return target;
        }
    }

    /// <summary>受影響的 doc 範圍（原位置 ∪ 目前位置）。</summary>
    public SKRectI AffectedBounds => SKRectI.Union(SourceBounds, TargetBounds);

    internal static void DrawLayerPixels(RasterLayer layer, SKCanvas canvas, SKRectI docRect)
    {
        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        foreach (var idx in TileIndex.CoveringRect(layerRect))
        {
            var tile = layer.Surface.GetTileForRead(idx);
            if (tile == null) continue;
            using var pixmap = tile.AsPixmap();
            using var img = SKImage.FromPixels(pixmap);
            var tileRect = idx.ToPixelRect();
            canvas.DrawImage(img, tileRect.Left + layer.Offset.X, tileRect.Top + layer.Offset.Y);
        }
    }

    /// <summary>
    /// 以選取遮罩裁切 canvas 現有內容（只保留選取範圍內的像素）。
    ///
    /// 兩個關鍵：
    /// 1. 必須先把稀疏遮罩組成一張與 docBounds 等大的完整遮罩 ——
    ///    逐 tile 套用會漏掉「沒有 tile 的區域」，那些正是該被裁掉的部分。
    /// 2. 中繼影像必須是帶 alpha 通道的 BGRA，不能直接用 Alpha8。
    ///    Skia 把 A8 當「覆蓋度」而非 alpha：DstIn 時覆蓋度 0 的地方會維持原狀
    ///    而不是被清成透明，多區域選取的空隙與不規則選取的外圍就會被一起提起。
    /// </summary>
    internal static unsafe void ApplySelectionMask(SelectionMask selection, SKCanvas canvas, SKRectI docBounds)
    {
        var bgraInfo = new SKImageInfo(docBounds.Width, docBounds.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var maskSurface = SKSurface.Create(bgraInfo);
        if (maskSurface == null) return;

        var mc = maskSurface.Canvas;
        mc.Clear(SKColors.Transparent);

        // 在透明底上以白色畫 A8：覆蓋度就轉成了 premul alpha
        var tileInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);
        using (var white = new SKPaint { Color = SKColors.White })
        {
            foreach (var (idx, tile) in selection.Mask.Tiles)
            {
                var rect = idx.ToPixelRect();
                if (!rect.IntersectsWith(docBounds)) continue;
                fixed (byte* ptr = tile.Alpha)
                {
                    using var img = SKImage.FromPixels(tileInfo, (IntPtr)ptr, MaskTile.Size);
                    mc.DrawImage(img, rect.Left - docBounds.Left, rect.Top - docBounds.Top, white);
                }
            }
        }
        mc.Flush();

        using var maskImage = maskSurface.Snapshot();
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.DrawImage(maskImage, 0, 0, paint);
    }

    private static unsafe void EraseFromLayer(RasterLayer layer, SelectionMask selection, SKRectI docBounds)
    {
        var maskInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstOut, Color = SKColors.White };

        var layerRect = new SKRectI(
            docBounds.Left - layer.Offset.X, docBounds.Top - layer.Offset.Y,
            docBounds.Right - layer.Offset.X, docBounds.Bottom - layer.Offset.Y);

        foreach (var layerIdx in TileIndex.CoveringRect(layerRect))
        {
            if (layer.Surface.GetTileForRead(layerIdx) == null) continue;
            var tile = layer.Surface.GetTileForWrite(layerIdx);
            using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            var tileRect = layerIdx.ToPixelRect();
            canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);

            foreach (var (maskIdx, maskTile) in selection.Mask.Tiles)
            {
                var maskRect = maskIdx.ToPixelRect();
                fixed (byte* ptr = maskTile.Alpha)
                {
                    using var img = SKImage.FromPixels(maskInfo, (IntPtr)ptr, MaskTile.Size);
                    canvas.DrawImage(img, maskRect.Left, maskRect.Top, paint);
                }
            }
            canvas.Flush();

            if (tile.IsBlank()) layer.Surface.RemoveTile(layerIdx);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_pixelsDetached) Pixels.Dispose();
        BeforeSnapshot.Dispose();
        _sourceOutline?.Dispose();
    }
}
