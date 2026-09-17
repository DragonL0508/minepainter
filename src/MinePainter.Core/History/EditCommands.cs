using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.Core.History;

/// <summary>選取範圍相關的編輯指令（全選／反轉／清除／填滿）。</summary>
public static class EditCommands
{
    public static void SelectAll(EditorSession session)
    {
        var doc = session.Document;
        // 文字圖層沒有可選的像素（要畫得先平面化），選了只會讓「移動選取內容」提起一塊空白
        if (doc.ActiveLayer is RasterLayer { IsTextLayer: true })
        {
            session.Notify("文字圖層不能選取像素；要編輯像素請先「圖層文字平面化」");
            return;
        }
        using var path = new SKPath();
        path.AddRect(SKRect.Create(0, 0, doc.Width, doc.Height));
        SelectionCommands.SetSelection(session, SelectionMask.FromPath(path, doc.Bounds), "全選");
    }

    /// <summary>反轉選取；沒有選取時等同全選。</summary>
    public static void InvertSelection(EditorSession session)
    {
        var doc = session.Document;
        if (session.Selection is not { IsEmpty: false } selection)
        {
            SelectAll(session);
            return;
        }

        using var full = new SKPath();
        full.AddRect(SKRect.Create(0, 0, doc.Width, doc.Height));
        var everything = SelectionMask.FromPath(full, doc.Bounds);
        var inverted = SelectionMask.Combine(everything, selection, SelectionCombineMode.Subtract);
        SelectionCommands.SetSelection(session, inverted is { IsEmpty: true } ? null : inverted, "反轉選取");
    }

    /// <summary>清除選取範圍內的像素（沒有選取時清空整個圖層）。</summary>
    public static void EraseSelection(EditorSession session) =>
        PaintSelection(session, null, "清除選取範圍");

    /// <summary>以前景色填滿選取範圍（沒有選取時填滿整個圖層）。</summary>
    public static void FillSelection(EditorSession session) =>
        PaintSelection(session, session.Foreground, "填滿選取範圍");

    /// <summary>color 為 null = 清除；否則以該色填入。兩者只差一個 blend mode。</summary>
    private static unsafe void PaintSelection(EditorSession session, SKColor? color, string label)
    {
        var doc = session.Document;
        if (doc.ActiveLayer is not RasterLayer layer)
        {
            session.Notify("請先選擇一個圖層");
            return;
        }
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return;
        }

        session.CommitFloating();

        var selection = session.Selection;
        var docRect = selection is { IsEmpty: false } ? selection.Bounds : doc.Bounds;
        docRect = SKRectI.Intersect(docRect, doc.Bounds);
        if (docRect.Width <= 0 || docRect.Height <= 0) return;

        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        IHistoryEntry? entry;
        var touchedRect = layerRect; // 圖層座標：undo 與失效的範圍
        lock (doc.SyncRoot)
        {
            using var before = layer.Surface.Snapshot();

            // 清除要延伸到畫布外（見 CoverageBeyondCanvas）；填色照舊只畫在畫布內
            if (!color.HasValue)
            {
                var content = layer.Surface.ExactContentBounds();
                if (content.Width > 0 && content.Height > 0) touchedRect = SKRectI.Union(layerRect, content);
            }

            // 清除：原始高清來源也把同一塊挖掉（快速模式輸出時才不會拿代理放大）；填色就只能作廢
            var sourceBefore = color.HasValue ? null : layer.ValidPixelSource;
            LayerPixelSource? sourceAfter = null;
            if (sourceBefore != null)
            {
                var keep = new byte[touchedRect.Width * touchedRect.Height];
                for (var y = 0; y < touchedRect.Height; y++)
                for (var x = 0; x < touchedRect.Width; x++)
                {
                    keep[y * touchedRect.Width + x] = (byte)(255 - CoverageBeyondCanvas(selection, doc.Bounds,
                        touchedRect.Left + x + layer.Offset.X, touchedRect.Top + y + layer.Offset.Y));
                }
                sourceAfter = sourceBefore.Masked(touchedRect, keep, outside: 255);
                layer.TakePixelSource(); // 舊的留給 undo
            }

            var paint = new SKPaint
            {
                Color = color ?? SKColors.White,
                BlendMode = color.HasValue ? SKBlendMode.SrcOver : SKBlendMode.DstOut,
            };
            var maskInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);

            foreach (var idx in TileIndex.CoveringRect(layerRect))
            {
                var tile = layer.Surface.GetTileForWrite(idx);
                using var surface = SKSurface.Create(Tile.Info, tile.Pixels, Tile.RowBytes);
                var canvas = surface.Canvas;
                var tileRect = idx.ToPixelRect();
                canvas.Translate(-tileRect.Left - layer.Offset.X, -tileRect.Top - layer.Offset.Y);
                canvas.ClipRect(SKRect.Create(docRect.Left, docRect.Top, docRect.Width, docRect.Height));

                if (selection is { IsEmpty: false } mask)
                {
                    // 用遮罩的覆蓋度當筆刷，軟邊界自然保留
                    foreach (var (maskIdx, maskTile) in mask.Mask.Tiles)
                    {
                        var maskRect = maskIdx.ToPixelRect();
                        if (!maskRect.IntersectsWith(docRect)) continue;
                        fixed (byte* ptr = maskTile.Alpha)
                        {
                            using var img = SKImage.FromPixels(maskInfo, (IntPtr)ptr, MaskTile.Size);
                            canvas.DrawImage(img, maskRect.Left, maskRect.Top, paint);
                        }
                    }
                }
                else
                {
                    canvas.DrawRect(
                        SKRect.Create(docRect.Left, docRect.Top, docRect.Width, docRect.Height), paint);
                }
                canvas.Flush();

                if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
            }

            paint.Dispose();
            if (!color.HasValue) EraseBeyondCanvas(layer, selection, doc.Bounds, touchedRect);
            entry = TileDeltaEntry.Capture(label, layer, before, touchedRect);
            if (sourceAfter != null)
            {
                sourceAfter.Revision = layer.Surface.Revision;
                layer.SetPixelSource(sourceAfter);
                if (entry != null) entry = new PixelSourceSwapEntry(entry, layer, sourceBefore, sourceAfter);
            }
        }

        if (entry != null) session.History.Push(entry);
        layer.Invalidate(new SKRectI(
            touchedRect.Left + layer.Offset.X, touchedRect.Top + layer.Offset.Y,
            touchedRect.Right + layer.Offset.X, touchedRect.Bottom + layer.Offset.Y));
    }

    /// <summary>
    /// 清除用的選取覆蓋度，定義域延伸到畫布外：畫布外的點取「夾回畫布後最近那一格」的覆蓋度
    /// （沒有選取＝整層，一律 255）。選取範圍本身永遠夾在畫布內，但圖層可以持有畫布外的像素
    /// （放大、平移出去的部分）；不延伸的話那些像素永遠選不到也清不掉，卻照樣被外框／光暈算進去
    /// （2026-09-17 使用者回報「放大後去背，畫布外沒清掉的會留著影響外框，用 delete 還清不掉」）。
    /// 語意＝選取貼到畫布哪一邊，那一邊外面的東西就一起清。
    /// </summary>
    internal static byte CoverageBeyondCanvas(SelectionMask? selection, SKRectI canvas, int docX, int docY)
    {
        if (selection is not { IsEmpty: false }) return 255;
        return selection.CoverageAt(
            Math.Clamp(docX, canvas.Left, canvas.Right - 1),
            Math.Clamp(docY, canvas.Top, canvas.Bottom - 1));
    }

    /// <summary>把 <paramref name="rect"/>（圖層座標）裡落在畫布外的像素依延伸覆蓋度清掉。須在 SyncRoot 內呼叫。</summary>
    private static unsafe void EraseBeyondCanvas(RasterLayer layer, SelectionMask? selection, SKRectI canvas, SKRectI rect)
    {
        // 選取沒貼到畫布任何一邊：畫布外的延伸覆蓋度全是 0，不必掃
        if (selection is { IsEmpty: false } s)
        {
            var b = s.Bounds;
            if (b.Left > canvas.Left && b.Top > canvas.Top && b.Right < canvas.Right && b.Bottom < canvas.Bottom) return;
        }

        var offset = layer.Offset;
        foreach (var idx in TileIndex.CoveringRect(rect))
        {
            if (layer.Surface.GetTileForRead(idx) == null) continue;
            var tileRect = idx.ToPixelRect();
            var tileDoc = new SKRectI(tileRect.Left + offset.X, tileRect.Top + offset.Y,
                tileRect.Right + offset.X, tileRect.Bottom + offset.Y);
            if (canvas.Contains(tileDoc)) continue; // 整格都在畫布內，上面那一趟處理過了

            var tile = layer.Surface.GetTileForWrite(idx);
            var px = (uint*)tile.Pixels;
            for (var y = 0; y < Tile.Size; y++)
            {
                var docY = tileDoc.Top + y;
                var rowInside = docY >= canvas.Top && docY < canvas.Bottom;
                var row = px + y * Tile.Size;
                for (var x = 0; x < Tile.Size; x++)
                {
                    var docX = tileDoc.Left + x;
                    if (rowInside && docX >= canvas.Left && docX < canvas.Right) continue;
                    if (row[x] == 0) continue;
                    var cov = CoverageBeyondCanvas(selection, canvas, docX, docY);
                    if (cov == 0) continue;
                    row[x] = LayerPixelSource.ScalePremul(row[x], (byte)(255 - cov));
                }
            }
            if (tile.IsBlank()) layer.Surface.RemoveTile(idx);
        }
    }
}
