using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.Core.Compositing;

/// <summary>
/// 圖層縮圖繪製（純 Skia，不依賴 UI 框架）。
/// 把單一節點的內容等比縮進固定大小的框、置中，底下鋪透明棋盤格。
/// 不套用該節點自身的 opacity/blend —— 縮圖顯示的是「這層上有什麼」，
/// 隱藏的圖層也照畫（可見與否由列上的勾選框表示）。
/// </summary>
public static class LayerThumbnailRenderer
{
    /// <summary>棋盤格方格邊長（縮圖尺寸小，用 4px）。</summary>
    private const int CheckerCell = 4;

    /// <summary>文件在縮圖框內的置中等比矩形。</summary>
    public static SKRect FitRect(Document doc, int boxWidth, int boxHeight)
    {
        if (doc.Width <= 0 || doc.Height <= 0) return SKRect.Empty;
        var scale = Math.Min((float)boxWidth / doc.Width, (float)boxHeight / doc.Height);
        var w = doc.Width * scale;
        var h = doc.Height * scale;
        return SKRect.Create((boxWidth - w) / 2, (boxHeight - h) / 2, w, h);
    }

    /// <summary>畫進已備妥的 canvas（原點 = 縮圖框左上）。呼叫端負責清背景。</summary>
    public static void Draw(SKCanvas canvas, Document doc, LayerNode node, int boxWidth, int boxHeight)
    {
        var dest = FitRect(doc, boxWidth, boxHeight);
        if (dest.IsEmpty) return;

        DrawChecker(canvas, dest);

        canvas.Save();
        canvas.ClipRect(dest);
        canvas.Translate(dest.Left, dest.Top);
        canvas.Scale(dest.Width / doc.Width);
        lock (doc.SyncRoot)
        {
            DrawNode(canvas, node, doc);
        }
        canvas.Restore();

        using var border = new SKPaint
        {
            Color = new SKColor(0x55, 0x55, 0x5C),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        canvas.DrawRect(new SKRect(dest.Left + 0.5f, dest.Top + 0.5f, dest.Right - 0.5f, dest.Bottom - 0.5f), border);
    }

    private static void DrawChecker(SKCanvas canvas, SKRect rect)
    {
        using var white = new SKPaint { Color = SKColors.White };
        using var grey = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC) };
        canvas.DrawRect(rect, white);

        canvas.Save();
        canvas.ClipRect(rect);
        for (var y = 0; y * CheckerCell < rect.Height; y++)
        {
            for (var x = 0; x * CheckerCell < rect.Width; x++)
            {
                if (((x + y) & 1) == 0) continue;
                canvas.DrawRect(
                    SKRect.Create(rect.Left + x * CheckerCell, rect.Top + y * CheckerCell, CheckerCell, CheckerCell),
                    grey);
            }
        }
        canvas.Restore();
    }

    /// <summary>畫節點內容（canvas 已縮放至文件座標；持 SyncRoot 呼叫）。</summary>
    private static void DrawNode(SKCanvas canvas, LayerNode node, Document doc)
    {
        switch (node)
        {
            case RasterLayer raster:
            {
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
                foreach (var (idx, tile) in raster.DisplaySurface.Tiles)
                {
                    var rect = idx.ToPixelRect();
                    using var pixmap = tile.AsPixmap();
                    using var img = SKImage.FromPixels(pixmap); // 零拷貝；持 SyncRoot 期間使用
                    canvas.DrawImage(img, rect.Left + raster.Offset.X, rect.Top + raster.Offset.Y, paint);
                }
                foreach (var el in raster.Elements)
                    el.Render(canvas);
                break;
            }

            case GroupLayer group:
            {
                // 群組縮圖 = 子節點由下而上疊合。只求可辨識，不重現群組的隔離合成語意。
                foreach (var child in group.Children)
                {
                    if (child.IsVisible && child.Opacity > 0)
                        DrawNode(canvas, child, doc);
                }
                break;
            }

            case AdjustmentLayer:
            {
                // 調整圖層沒有自己的像素，畫個半亮/半暗圓示意
                var cx = doc.Width / 2f;
                var cy = doc.Height / 2f;
                var r = Math.Min(doc.Width, doc.Height) * 0.3f;
                using var fill = new SKPaint { Color = new SKColor(0x40, 0x40, 0x48), IsAntialias = true };
                using var stroke = new SKPaint
                {
                    Color = new SKColor(0x40, 0x40, 0x48),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1, r * 0.12f),
                    IsAntialias = true,
                };
                canvas.DrawCircle(cx, cy, r, stroke);
                using var path = new SKPath();
                path.AddArc(SKRect.Create(cx - r, cy - r, r * 2, r * 2), 90, 180);
                path.Close();
                canvas.DrawPath(path, fill);
                break;
            }
        }
    }
}
