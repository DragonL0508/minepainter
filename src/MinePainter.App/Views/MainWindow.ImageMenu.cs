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
    // ---- 影像 ----

    private void OnCropToSelectionClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            DocumentCommands.CropToSelection(s);
            AfterDocumentResized(s);
        });

    private void OnFlipHorizontalClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.FlipHorizontal, "水平翻轉");

    private void OnFlipVerticalClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.FlipVertical, "垂直翻轉");

    private void OnRotateCwClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate90CW, "順時針旋轉 90°");

    private void OnRotateCcwClicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate90CCW, "逆時針旋轉 90°");

    private void OnRotate180Clicked(object? sender, RoutedEventArgs e) =>
        RunGeometry(GeometryOp.Rotate180, "旋轉 180°");

    private void RunGeometry(GeometryOp op, string label) =>
        RunCommand(s =>
        {
            DocumentCommands.ApplyGeometry(s, op, label);
            AfterDocumentResized(s);
            Toasts.Show(label);
        });

    private async void OnFlattenClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;

        // 平面化把合成結果寫進像素，效果一律重算全解析度 —— 大文件要跑一陣子
        var flattened = false;
        await ProgressDialog.RunAsync(this, "平面化影像",
            _ => flattened = LayerCommands.Flatten(session.Document, session.History));
        _layersContent.Refresh();
        RefreshUiState();
        if (flattened) Toasts.Show("已平面化");
    }

    /// <summary>
    /// 快速模式 →一般模式：把整份放大成專案的輸出解析度。
    /// 文字、形狀、效果以新尺寸重畫，筆刷畫上去的像素重新取樣（與輸出時同一套規則）。
    /// </summary>
    private async void OnToFullResolutionClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        if (!doc.IsFastMode)
        {
            Toasts.Show("這份專案已經是完整解析度");
            return;
        }

        var w = doc.OutputWidth;
        var h = doc.OutputHeight;
        try
        {
            await ProgressDialog.RunAsync(this, "轉成完整解析度",
                _ => ImageCommands.ResizeImage(session, w, h, "轉成完整解析度"));
            _layersContent.Refresh();
            RefreshUiState();
            DocSizeLabel.Text = DocSizeText(doc);
            Toasts.Show($"已轉成完整解析度（{w} × {h}）");
        }
        catch (Exception ex)
        {
            Toasts.Show($"轉換失敗：{ex.Message}");
            LogError("轉成完整解析度", ex);
        }
    }

    /// <summary>
    /// 一般模式 → 快速模式：畫布縮到代理級別（預設 1080p，可在設定改），輸出解析度記成現在的尺寸。
    /// 畫過的像素會變成代理解析度（可復原；存檔前建議另存新檔）。
    /// </summary>
    private async void OnToFastModeClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        if (doc.IsFastMode)
        {
            Toasts.Show("這份專案已經是快速模式");
            return;
        }
        if (!Core.Documents.FastMode.ShouldOffer(doc.Width, doc.Height))
        {
            Toasts.Show($"這份專案沒有比 {Core.Documents.FastMode.ProxyWidth} × "
                      + $"{Core.Documents.FastMode.ProxyHeight} 大，不需要快速模式");
            return;
        }

        var outW = doc.Width;
        var outH = doc.Height;
        var (proxyW, proxyH) = Core.Documents.FastMode.ProxySize(outW, outH);
        try
        {
            await ProgressDialog.RunAsync(this, "轉成快速模式",
                _ => ImageCommands.ResizeImage(session, proxyW, proxyH, "轉成快速模式",
                    outputWidth: outW, outputHeight: outH));
            _layersContent.Refresh();
            RefreshUiState();
            DocSizeLabel.Text = DocSizeText(doc);
            Toasts.Show($"快速模式：畫布 {proxyW} × {proxyH}，輸出 {outW} × {outH}（存檔前記得另存新檔）");
        }
        catch (Exception ex)
        {
            Toasts.Show($"轉換失敗：{ex.Message}");
            LogError("轉成快速模式", ex);
        }
    }

    // ---- 影像大小／畫布大小／圖層幾何（paint.net 的 Image / Layers 選單補齊） ----

    private async void OnResizeImageClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        var dialog = new ResizeImageDialog(doc.Width, doc.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        var w = dialog.NewWidth;
        var h = dialog.NewHeight;
        var resample = dialog.Resample; // 交給背景執行緒前先在 UI 執行緒讀完（不得懶讀控制項）
        try
        {
            await ProgressDialog.RunAsync(this, "調整影像大小", _ => ImageCommands.ResizeImage(session, w, h, resample: resample));
        }
        catch (Exception ex)
        {
            Toasts.Show($"調整影像大小失敗：{ex.Message}");
            return;
        }
        AfterDocumentResized(session);
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show($"影像大小：{w} × {h}");
    }

    private async void OnCanvasSizeClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        var dialog = new CanvasSizeDialog(doc.Width, doc.Height);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed) return;
        ImageCommands.ResizeCanvas(session, dialog.NewWidth, dialog.NewHeight, dialog.AnchorX, dialog.AnchorY);
        AfterDocumentResized(session);
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show($"畫布大小：{dialog.NewWidth} × {dialog.NewHeight}");
    }

    private void OnFlipLayerHorizontalClicked(object? sender, RoutedEventArgs e) =>
        FlipActiveLayer(GeometryOp.FlipHorizontal, "水平翻轉圖層");

    private void OnFlipLayerVerticalClicked(object? sender, RoutedEventArgs e) =>
        FlipActiveLayer(GeometryOp.FlipVertical, "垂直翻轉圖層");

    private void FlipActiveLayer(GeometryOp op, string label) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is not RasterLayer layer)
            {
                Toasts.Show("請先選擇一個點陣圖層");
                return;
            }
            ImageCommands.FlipLayer(s, layer, op, label);
            Toasts.Show(label);
        });

    private async void OnImportLayerClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "從檔案匯入圖層",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("影像檔") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }],
        });
        var imported = 0;
        var oversized = false;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path == null) continue;
            try
            {
                using var bitmap = ImageCodec.LoadBitmap(path);
                ImageCommands.ImportImageLayer(session, bitmap, Path.GetFileNameWithoutExtension(path));
                oversized |= bitmap.Width > session.Document.Width || bitmap.Height > session.Document.Height;
                imported++;
            }
            catch (Exception ex)
            {
                Toasts.Show($"匯入失敗：{Path.GetFileName(path)}（{ex.Message}）");
            }
        }
        if (imported == 0) return;
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show(oversized
            ? $"已匯入 {imported} 個圖層（影像比畫布大，超出部分看不到，可用「調整畫布大小」展開）"
            : $"已匯入 {imported} 個圖層");
    }

    /// <summary>
    /// 圖層 → AI 去背：直接用設定頁的參數送 remove.bg，寫進圖層（先平面化；一步 undo）。
    /// 有選取範圍就只處理範圍內、範圍外清掉。沒填 API Key 先帶去設定頁。
    /// </summary>
    private async void OnRemoveBackgroundClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層");
            return;
        }
        var settings = Services.AppSettings.Instance;
        if (settings.RemoveBgApiKeys.All(string.IsNullOrWhiteSpace))
        {
            Toasts.Show("請先填 API Key");
            await OpenSettingsAsync(Settings.SettingsWindow.Page.BackgroundRemoval);
            if (settings.RemoveBgApiKeys.All(string.IsNullOrWhiteSpace)) return;
            session = CommitPending();
            if (session?.Document.ActiveLayer is not RasterLayer stillLayer) return;
            layer = stillLayer;
        }
        var remote = new RemoveBgOptions(settings.RemoveBgApiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
            settings.RemoveBgPreview ? RemoveBgSize.Preview : RemoveBgSize.Auto);
        await RunBackgroundRemovalAsync(session, layer, remote, "AI 去背");
    }

    /// <summary>圖層 → 演算去背：本機 GrabCut，不上網、不用 key。</summary>
    private async void OnRemoveBackgroundLocalClicked(object? sender, RoutedEventArgs e)
    {
        var session = CommitPending();
        if (session == null) return;
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層");
            return;
        }
        await RunBackgroundRemovalAsync(session, layer, null, "演算去背");
    }

    private async Task RunBackgroundRemovalAsync(EditorSession session, RasterLayer layer, RemoveBgOptions? remote, string title)
    {
        var settings = Services.AppSettings.Instance;
        var options = new BackgroundRemovalOptions
        {
            RemoveBg = remote,
            SolidCore = settings.RemoveBgSolidCore,
            Contrast = settings.RemoveBgContrast,
            Shift = settings.RemoveBgShift,
            HardEdge = settings.RemoveBgHardEdgeCut,
            Selection = session.Selection is { IsEmpty: false } sel ? sel : null,
        };
        session.SelectedElement = null; // 平面化後物件不存在，把手框不能還指著它
        var dialog = new BackgroundRemovalWindow(session, layer, options, title);
        await dialog.ShowDialog(this);
        if (dialog.Error != null) Toasts.Show("去背失敗：" + dialog.Error);
        else if (dialog.Applied) Toasts.Show("去背完成");
        _layersContent.Refresh();
        RefreshUiState();
    }

    private void OnLayerPropertiesClicked(object? sender, RoutedEventArgs e)
    {
        var session = Canvas.Session;
        if (session?.Document.ActiveLayer is { } layer) _layersContent.OpenProperties(layer);
    }
}
