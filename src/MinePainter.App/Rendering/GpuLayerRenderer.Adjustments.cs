using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using SkiaSharp;

namespace MinePainter.App.Rendering;

public sealed unsafe partial class GpuLayerRenderer
{
    // 原本每個半透明調整會遞迴畫下方兩次，n 層使來源重畫 2^n 次。
    // 同一張表面逐階快照，與 CPU 合成器同樣以 Src／SrcOver 套回，來源只畫一次。
    private bool TryDrawAdjustmentPipeline(SKCanvas canvas, EditorSession session,
        GroupLayer group, SKRectI visibleDoc)
    {
        if (!group.Children.Any(c => c is AdjustmentLayer { IsVisible: true, Opacity: > 0 }))
            return false;

        // 工作表面只分配可見裝置像素，放大 8K 文件也不建立整份文件尺寸的貼圖。
        var matrix = canvas.TotalMatrix;
        var mapped = matrix.MapRect(new SKRect(visibleDoc.Left, visibleDoc.Top, visibleDoc.Right, visibleDoc.Bottom));
        var bounds = SKRectI.Intersect(canvas.DeviceClipBounds, new SKRectI(
            (int)Math.Floor(mapped.Left), (int)Math.Floor(mapped.Top),
            (int)Math.Ceiling(mapped.Right), (int)Math.Ceiling(mapped.Bottom)));
        if (bounds.IsEmpty) return true;
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = _gpuContext != null
            ? SKSurface.Create(_gpuContext, true, info)
            : SKSurface.Create(info);
        if (surface == null) return false; // 尚未繪製，配置失敗可安全沿用舊路徑。
        var target = surface.Canvas;
        target.Clear(SKColors.Transparent);
        target.Translate(-bounds.Left, -bounds.Top);
        target.Concat(ref matrix);
        target.ClipRect(new SKRect(visibleDoc.Left, visibleDoc.Top, visibleDoc.Right, visibleDoc.Bottom));
        foreach (var child in group.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0) continue;
            switch (child)
            {
                case AdjustmentLayer adjustment:
                {
                    using var source = surface.Snapshot();
                    using var paint = new SKPaint
                    {
                        ColorFilter = AdjustmentFilter(adjustment),
                        Color = SKColors.White.WithAlpha((byte)(adjustment.Opacity * 255)),
                        BlendMode = adjustment.Opacity >= 1f ? SKBlendMode.Src : SKBlendMode.SrcOver,
                    };
                    target.Save();
                    target.ResetMatrix();
                    target.DrawImage(source, 0, 0, paint);
                    target.Restore();
                    break;
                }
                case RasterLayer raster:
                    DrawRaster(target, session, raster, visibleDoc);
                    break;
                case GroupLayer nested:
                    DrawNestedGroup(target, session, nested, visibleDoc);
                    break;
            }
        }
        using var result = surface.Snapshot();
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(result, bounds.Left, bounds.Top);
        canvas.Restore();
        return true;
    }
}
