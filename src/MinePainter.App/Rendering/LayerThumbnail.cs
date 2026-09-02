using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MinePainter.Core.Compositing;
using MinePainter.Core.Documents;
using MinePainter.Core.Layers;
using SkiaSharp;

namespace MinePainter.App.Rendering;

/// <summary>
/// 圖層縮圖的 Avalonia 包裝：把 <see cref="LayerThumbnailRenderer"/> 的輸出畫進 WriteableBitmap。
/// UI thread 呼叫（繪製本身在 Core，內部自行取 Document.SyncRoot）。
/// </summary>
public static class LayerThumbnail
{
    public static WriteableBitmap Render(Document doc, LayerNode node, int boxWidth, int boxHeight)
    {
        var bitmap = new WriteableBitmap(new PixelSize(boxWidth, boxHeight), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var framebuffer = bitmap.Lock();
        var info = new SKImageInfo(boxWidth, boxHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, framebuffer.Address, framebuffer.RowBytes);
        surface.Canvas.Clear(SKColors.Transparent);
        LayerThumbnailRenderer.Draw(surface.Canvas, doc, node, boxWidth, boxHeight);
        surface.Canvas.Flush();
        return bitmap;
    }
}
