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
    // ---- 拖放檔案 ----

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private static readonly string[] OpenableExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".mpp", ".pdn", ".psd", ".psb"];

    private static bool HasExtension(string path, string[] list) =>
        list.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static List<string> DroppedPaths(DragEventArgs e)
    {
        var result = new List<string>();
        if (e.DataTransfer.TryGetFiles() is not { } items) return result;
        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (path != null && File.Exists(path) && HasExtension(path, OpenableExtensions)) result.Add(path);
        }
        return result;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (PresetsPanelContent.PresetFrom(e.DataTransfer) != null)
        {
            // 預設集只能丟在畫布上（有文件時）
            e.DragEffects = Canvas.Session != null && IsOverCanvas(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.DragEffects = DroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private bool IsOverCanvas(DragEventArgs e)
    {
        var p = e.GetPosition(Canvas);
        return p.X >= 0 && p.Y >= 0 && p.X < Canvas.Bounds.Width && p.Y < Canvas.Bounds.Height;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (PresetsPanelContent.PresetFrom(e.DataTransfer) is { } preset)
        {
            e.Handled = true;
            if (!IsOverCanvas(e)) return;
            DropPresetOnCanvas(preset, Canvas.ViewToDoc(e.GetPosition(Canvas)));
            return;
        }

        var paths = DroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;

        var session = Canvas.Session;
        var canAddLayers = session != null && paths.Any(p => HasExtension(p, ImageExtensions));
        var dialog = new DropFilesDialog(paths.Select(Path.GetFileName).ToList()!, canAddLayers);
        await dialog.ShowDialog(this);

        switch (dialog.Result)
        {
            case DropFilesDialog.Choice.Open:
                foreach (var path in paths) OpenFile(path);
                break;

            case DropFilesDialog.Choice.AddLayers:
                session = CommitPending();
                if (session == null) return;
                var skipped = 0;
                foreach (var path in paths)
                {
                    if (!HasExtension(path, ImageExtensions))
                    {
                        skipped++; // .mpp/.pdn/.psd 是整份文件，不能當一層
                        continue;
                    }
                    ImportLayerFromFile(session, path);
                }
                if (skipped > 0) Toasts.Show($"{skipped} 個檔案是文件格式（.mpp/.pdn/.psd），只能用「開啟」");
                _layersContent.Refresh();
                RefreshUiState();
                break;
        }
    }

    // ---- 效果預設集拖進畫布 ----

    /// <summary>
    /// 預設集丟在畫布上：落點底下最上面「看得到且有像素」的點陣圖層就是目標
    /// （像 Premiere 把預設集丟到剪輯上）；落在空白處就套到目前圖層。
    /// </summary>
    private void DropPresetOnCanvas(EffectPreset preset, SKPoint docPoint)
    {
        var session = CommitPending();
        if (session == null) return;
        var doc = session.Document;
        LayerNode? target;
        bool hit;
        lock (doc.SyncRoot)
        {
            target = LayerAtLocked(doc, docPoint);
            hit = target != null;
            // 落在空白處：套到目前選的東西（群組也可以 —— 整組一起吃）
            target ??= doc.ActiveLayer is { CanHaveEffects: true } active
                ? active
                : doc.Descendants().OfType<RasterLayer>().LastOrDefault();
        }
        if (target == null)
        {
            Toasts.Show("文件裡沒有可以套效果的圖層或群組");
            return;
        }
        if (hit)
        {
            lock (doc.SyncRoot) doc.ActiveLayer = target; // 套到哪層就選哪層，圖層面板看得到
        }
        _ = ApplyPresetAskingAsync(session, target, preset);
    }

    /// <summary>圖層還沒有效果堆疊就直接套；已經有了就問要覆蓋還是疊加。</summary>
    private async Task ApplyPresetAskingAsync(EditorSession session, LayerNode layer, EffectPreset preset)
    {
        IReadOnlyList<LayerEffect> existing;
        lock (session.Document.SyncRoot) existing = layer.Effects;
        if (existing.Count == 0)
        {
            ApplyPresetToLayer(session, layer, preset, replace: false);
            return;
        }
        var dialog = new PresetApplyDialog(preset.Name, layer.Name, existing.Select(e => e.Name).ToList());
        await dialog.ShowDialog(this);
        if (dialog.Result == PresetApplyDialog.Choice.Cancel) return;
        ApplyPresetToLayer(session, layer, preset, replace: dialog.Result == PresetApplyDialog.Choice.Replace);
    }

    /// <summary>落點（doc 座標）附近 5×5 內有不透明像素的最上層點陣圖層（含效果輸出）；沒有就 null。</summary>
    private static RasterLayer? LayerAtLocked(MinePainter.Core.Documents.Document doc, SKPoint p)
    {
        var x = (int)MathF.Floor(p.X);
        var y = (int)MathF.Floor(p.Y);
        if (x < 0 || y < 0 || x >= doc.Width || y >= doc.Height) return null;

        foreach (var node in doc.Descendants().Reverse())
        {
            if (node is not RasterLayer layer || !IsShown(layer)) continue;
            var lx = x - layer.Offset.X;
            var ly = y - layer.Offset.Y;
            var rect = new SKRectI(lx - 2, ly - 2, lx + 3, ly + 3);
            var pixels = layer.EffectsRendered
                ? LayerEffectRenderer.ReadPixels(layer.DisplaySurface, rect)
                : LayerEffectRenderer.ReadPixelsWithElements(layer, rect);
            if (pixels.Any(px => (px >> 24) != 0)) return layer;
        }
        return null;

        static bool IsShown(LayerNode node)
        {
            for (LayerNode? n = node; n != null; n = n.Parent)
            {
                if (!n.IsVisible || n.Opacity <= 0f) return false;
            }
            return true;
        }
    }

    /// <summary>套用預設集到某層（一步 undo），同步圖層面板／屬性視窗並回報。</summary>
    private void ApplyPresetToLayer(EditorSession session, LayerNode layer, EffectPreset preset, bool replace)
    {
        if (preset.Effects.Count == 0)
        {
            Toasts.Show($"預設集「{preset.Name}」是空的");
            return;
        }
        EffectPresetStore.Apply(session, layer, preset, replace);
        _layersContent.Refresh();
        _layersContent.SyncPropertiesWindow();
        RefreshUiState();
        Toasts.Show(replace
            ? $"已用預設集「{preset.Name}」取代「{layer.Name}」的效果堆疊"
            : $"已把預設集「{preset.Name}」套到「{layer.Name}」");
    }
}
