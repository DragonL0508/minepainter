using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Selections;
using MinePainter.Core.Tiles;
using SkiaSharp;

namespace MinePainter.Core.Tools;

/// <summary>油漆桶：flood fill 區域以前景色填入作用中圖層（受選取遮罩裁切）。</summary>
public sealed class FillTool : ITool
{
    public string Name => "油漆桶";

    public void OnPointerDown(ToolPointerEvent e, EditorSession session)
    {
        if (session.Document.ActiveLayer is not RasterLayer layer) return;
        if (layer.IsTextLayer)
        {
            session.Notify("文字圖層不能直接繪製；要畫請先「圖層文字平面化」");
            return;
        }
        var doc = session.Document;
        var seed = new SKPointI((int)e.DocPosition.X, (int)e.DocPosition.Y);
        if (seed.X < 0 || seed.Y < 0 || seed.X >= doc.Width || seed.Y >= doc.Height) return;

        // 選取存在且點在選取外 → 不動作
        if (session.Selection != null && session.Selection.CoverageAt(seed.X, seed.Y) == 0) return;

        TileDeltaEntry? entry;
        SKRectI dirty;
        lock (doc.SyncRoot)
        {
            var region = FloodFiller.Fill(layer, seed, session.Tolerance, doc.Bounds);
            if (region.IsEmpty) return;

            // 與選取交集
            var mask = session.Selection != null
                ? SelectionMask.Combine(region, session.Selection, SelectionCombineMode.Intersect)!
                : region;

            using var before = layer.Surface.Snapshot();
            dirty = mask.Bounds;
            FillMasked(layer, mask, session.Foreground);

            var affected = new SKRectI(
                dirty.Left - layer.Offset.X, dirty.Top - layer.Offset.Y,
                dirty.Right - layer.Offset.X, dirty.Bottom - layer.Offset.Y);
            entry = TileDeltaEntry.Capture(Name, layer, before, affected);
        }

        if (entry != null) session.History.Push(entry);
        layer.Invalidate(dirty);
    }

    private static unsafe void FillMasked(RasterLayer layer, SelectionMask mask, SKColor color)
    {
        using var paint = new SKPaint { Color = color, BlendMode = SKBlendMode.SrcOver };
        var maskInfo = new SKImageInfo(MaskTile.Size, MaskTile.Size, SKColorType.Alpha8, SKAlphaType.Premul);

        var docRect = mask.Bounds;
        var layerRect = new SKRectI(
            docRect.Left - layer.Offset.X, docRect.Top - layer.Offset.Y,
            docRect.Right - layer.Offset.X, docRect.Bottom - layer.Offset.Y);

        foreach (var layerIdx in TileIndex.CoveringRect(layerRect))
        {
            var layerTile = layer.Surface.GetTileForWrite(layerIdx);
            using var surface = SKSurface.Create(Tile.Info, layerTile.Pixels, Tile.RowBytes);
            var canvas = surface.Canvas;
            var layerTileRect = layerIdx.ToPixelRect();
            canvas.Translate(-layerTileRect.Left - layer.Offset.X, -layerTileRect.Top - layer.Offset.Y);

            foreach (var (maskIdx, maskTile) in mask.Mask.Tiles)
            {
                var maskRect = maskIdx.ToPixelRect();
                fixed (byte* ptr = maskTile.Alpha)
                {
                    using var img = SKImage.FromPixels(maskInfo, (IntPtr)ptr, MaskTile.Size);
                    canvas.DrawImage(img, maskRect.Left, maskRect.Top, paint);
                }
            }
            canvas.Flush();
        }
    }

    public void OnPointerMove(ToolPointerEvent e, EditorSession session)
    {
    }

    public void OnPointerUp(ToolPointerEvent e, EditorSession session)
    {
    }
}
