using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.Adjustments;
using MinePainter.Core.AI;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;
using MinePainter.Core.History;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Tiles;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;

public partial class MainWindow
{
    // ---- 檢視 ----

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.25);

    private void OnActualSizeClicked(object? sender, RoutedEventArgs e) => Canvas.SetZoomPercent(100);

    private void OnBestFitClicked(object? sender, RoutedEventArgs e) => Canvas.ZoomToFit();

    private void OnTogglePixelGridClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.ShowPixelGrid = PixelGridMenuItem.IsChecked;
        Toasts.Show(Canvas.ShowPixelGrid ? "像素格線：開（放大 300% 以上顯示）" : "像素格線：關");
    }

    private void OnToggleSmoothZoomClicked(object? sender, RoutedEventArgs e)
    {
        Canvas.SmoothZoom = SmoothZoomMenuItem.IsChecked;
        Services.AppSettings.Instance.SmoothZoom = Canvas.SmoothZoom;
        Services.AppSettings.Instance.Save();
        Toasts.Show(Canvas.SmoothZoom ? "放大時平滑取樣：開（只影響顯示）" : "放大時平滑取樣：關（顯示真實像素）");
    }

    private void OnToggleCanvasLodClicked(object? sender, RoutedEventArgs e)
    {
        Rendering.GpuLayerRenderer.LodEnabled = CanvasLodMenuItem.IsChecked;
        Services.AppSettings.Instance.CanvasLod = Rendering.GpuLayerRenderer.LodEnabled;
        Services.AppSettings.Instance.Save();
        Toasts.Show(Rendering.GpuLayerRenderer.LodEnabled
            ? "縮小時用降取樣貼圖：開（只影響顯示）"
            : "縮小時用降取樣貼圖：關（一律逐格畫全解析度）");
    }
}
