using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>擁有手勢物件快照，負責擷取、隱藏原件、還原及延後退役。</summary>
internal sealed class TransformElementPreviews(Document doc, List<TransformItem> items)
{
    private readonly Document _doc = doc;
    private readonly List<TransformItem> _items = items;
    internal bool Frozen { get; private set; }

    /// <summary>
    /// 手勢開始時把每一層的「物件＋圖層效果」拍成一張圖，手勢期間只變換這張圖。
    ///
    /// 文字的外框／陰影／光暈是**圖層效果堆疊**（不是文字物件自己的參數），而效果是 CPU 逐像素
    /// 算出來的：4K 文件上一個帶外光暈的字，整串算一次實測 120 ms（外框 37 ms、沒效果 0 ms）。
    /// 手勢中每動一下就 ReplaceElement 一次 ＝ 每幀重算一次整串效果，畫面當然跟不上；而且重算
    /// 是背景逐格寫回的，畫面上還會出現「一部分新角度、一部分舊角度」的撕裂。
    /// 使用者回報「移動工具轉文字會卡、文字工具不會」就是這個 —— 文字工具走的正是快照那條路
    /// （<see cref="EditorSession.BeginElementOverlayLocked"/>），這裡把同一套補給變形手勢。
    ///
    /// 拍完就把原件藏起來、手勢期間不再動它（<see cref="Frozen"/>），放開時由
    /// 蓋章／還原原件時一次落地。
    /// 拍不成（沒有效果快取、範圍太大…）就整份放棄，照舊每幀重算 —— 慢，但不會畫錯。
    /// </summary>
    internal void Capture(SKMatrix matrix, bool isMeshMode)
    {
        // 網格／四角模式的位移是套在網格上的，物件走的是另一條路（TransformedElement），
        // 這裡不接手 —— 照舊每幀重算，慢但不會畫錯。
        if (isMeshMode) return;

        var withElements = _items.Where(i => i.Pixels == null && i.Layer.HasElements).ToList();
        if (withElements.Count == 0) return;

        // 快照拍的是「此刻的樣子」，而此刻已經含了本 session 先前的位移／縮放 ——
        // 畫的時候要把那一段扣掉，否則會被套第二次（見 TransformSession.GestureOverlay.Items）
        if (!matrix.TryInvert(out var inverse)) return;

        foreach (var item in withElements)
        {
            if (!TryCaptureElementPreview(item))
            {
                Release(null, false); // 有一層拍不成就整份放棄（免得半快照半即時）
                return;
            }
        }

        lock (_doc.SyncRoot)
        {
            foreach (var item in withElements)
            {
                item.Layer.ElementsHidden = true;
                item.ElementsWereHidden = true;
                item.PreviewInverse = inverse;
            }
        }
        Frozen = true;
    }

    private bool TryCaptureElementPreview(TransformItem item)
    {
        var layer = item.Layer;
        lock (_doc.SyncRoot)
        {
            // 快照要是最新的：效果還在背景算的話先等它（與 EditorSession.BeginLayerDrag 同一套判斷）
            var withEffects = layer.HasActiveEffects;
            if (withEffects && (layer.FxCache.HasPending || !layer.EffectsRendered))
                Effects.LayerEffectRenderer.RenderLayerNow(_doc, layer);
            withEffects &= layer.EffectsRendered;

            var region = withEffects ? layer.DisplayContentBounds : ElementBounds(layer);
            if (region.Width <= 0 || region.Height <= 0) return false;
            if (region.Width > 16384 || region.Height > 16384) return false;

            var info = new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return false;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-region.Left, -region.Top);
            if (withEffects) DrawDisplayTiles(layer, canvas, region);
            else foreach (var el in layer.Elements) el.Render(canvas);
            canvas.Flush();

            item.ElementPreview = surface.Snapshot();
            item.ElementPreviewBounds = region;
            return true;
        }
    }

    /// <summary>整層物件的 doc 外框（含效果外擴的保守範圍）。</summary>
    private static SKRectI ElementBounds(RasterLayer layer)
    {
        var bounds = SKRectI.Empty;
        foreach (var el in layer.Elements)
        {
            var b = el.Bounds;
            if (b.IsEmpty) continue;
            bounds = bounds.IsEmpty ? b : SKRectI.Union(bounds, b);
        }
        return bounds;
    }

    /// <summary>把圖層的顯示用 tile（有效果堆疊時＝效果快取）畫到 canvas（doc 座標）。</summary>
    private static void DrawDisplayTiles(RasterLayer layer, SKCanvas canvas, SKRectI docRect)
    {
        var surface = layer.DisplaySurface;
        var layerRect = new SKRectI(
            docRect.Left - layer.EffectOffset.X, docRect.Top - layer.EffectOffset.Y,
            docRect.Right - layer.EffectOffset.X, docRect.Bottom - layer.EffectOffset.Y);
        foreach (var idx in Tiles.TileIndex.CoveringRect(layerRect))
        {
            var tile = surface.GetTileForRead(idx);
            if (tile == null) continue;
            using var pixmap = tile.AsPixmap();
            using var img = SKImage.FromPixels(pixmap);
            var tileRect = idx.ToPixelRect();
            canvas.DrawImage(img, tileRect.Left + layer.EffectOffset.X, tileRect.Top + layer.EffectOffset.Y);
        }
    }

    /// <summary>
    /// 把原件放回來（手勢一結束就做，快照本身還留著頂到合成器追上）。
    ///
    /// 順序很重要：交接是「等合成器把那塊畫好才收快照」，而合成器要畫得對，原件就得先解除隱藏
    /// —— 反過來的話，合成器畫出來的是「沒有文字」的那份，收掉快照的瞬間文字會閃不見。
    /// </summary>
    internal void Unfreeze()
    {
        Frozen = false;
        lock (_doc.SyncRoot)
        {
            foreach (var item in _items)
            {
                if (item.ElementsWereHidden) item.Layer.ElementsHidden = false;
                item.ElementsWereHidden = false;
            }
        }
    }

    /// <summary>收掉物件快照（合成器已追上，或 session 結束）。</summary>
    internal void Release(Compositor? compositor, bool overlayEverPublished)
    {
        Unfreeze();
        foreach (var item in _items)
        {
            if (item.ElementPreview is { } image)
            {
                if (overlayEverPublished && compositor != null) compositor.Retire(image);
                else image.Dispose();
                item.ElementPreview = null;
            }
            item.ElementPreviewBounds = SKRectI.Empty;
            item.PreviewInverse = SKMatrix.Identity;
        }
    }

}
