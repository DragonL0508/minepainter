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
        lock (doc.SyncRoot)
        {
            using var before = layer.Surface.Snapshot();

            // 清除：原始高清來源也把同一塊挖掉（快速模式輸出時才不會拿代理放大）；填色就只能作廢
            var sourceBefore = color.HasValue ? null : layer.ValidPixelSource;
            LayerPixelSource? sourceAfter = null;
            if (sourceBefore != null)
            {
                var coverage = selection is { IsEmpty: false } sel
                    ? BackgroundRemovalCommand.ReadCoverage(sel, layerRect, layer.Offset)
                    : null;
                var keep = new byte[layerRect.Width * layerRect.Height];
                for (var i = 0; i < keep.Length; i++) keep[i] = (byte)(255 - (coverage?[i] ?? 255));
                sourceAfter = sourceBefore.Masked(layerRect, keep, outside: 255);
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
            entry = TileDeltaEntry.Capture(label, layer, before, layerRect);
            if (sourceAfter != null)
            {
                sourceAfter.Revision = layer.Surface.Revision;
                layer.SetPixelSource(sourceAfter);
                if (entry != null) entry = new PixelSourceSwapEntry(entry, layer, sourceBefore, sourceAfter);
            }
        }

        if (entry != null) session.History.Push(entry);
        layer.Invalidate(docRect);
    }
}
