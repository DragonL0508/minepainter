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
    // ---- 圖層 ----

    private void OnAddLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            var doc = s.Document;
            var active = doc.ActiveLayer;
            var parent = active?.Parent ?? doc.Root;
            var index = active?.Parent != null ? parent.IndexOf(active) + 1 : parent.Children.Count;
            var layer = new RasterLayer { Name = $"圖層 {parent.Children.Count + 1}" };
            LayerCommands.InsertLayer(doc, s.History, parent, index, layer);
            lock (doc.SyncRoot) doc.ActiveLayer = layer;
        });

    private void OnDuplicateLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is RasterLayer layer)
            {
                LayerCommands.DuplicateLayer(s.Document, s.History, layer);
                Toasts.Show("已複製圖層");
            }
        });

    private void OnDeleteLayerClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is { Parent: not null } layer)
            {
                LayerCommands.RemoveLayer(s.Document, s.History, layer);
                lock (s.Document.SyncRoot) s.Document.ActiveLayer = s.Document.Root.Children.LastOrDefault();
            }
        });

    private void OnMergeLayerDownClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is RasterLayer layer &&
                LayerCommands.MergeLayerDown(s.Document, s.History, layer))
                Toasts.Show("已向下合併");
            else
                Toasts.Show("下方沒有可合併的圖層");
        });

    /// <summary>圖層文字平面化：本層文字物件烙成像素（單一步 undo）。</summary>
    private void OnFlattenTextClicked(object? sender, RoutedEventArgs e) =>
        RunCommand(s =>
        {
            if (s.Document.ActiveLayer is not RasterLayer layer)
            {
                Toasts.Show("請先選一個圖層（群組不能平面化文字）");
                return;
            }
            if (!layer.HasElements)
            {
                Toasts.Show("這個圖層沒有文字物件");
                return;
            }
            s.SelectedElement = null; // 物件已不存在，把手框不能還指著它
            if (LayerCommands.FlattenText(s.Document, s.History, layer))
                Toasts.Show("已將文字平面化為像素");
        });

    /// <summary>
    /// 分離選取的文字：正在畫布上編輯文字、且框選了一段 → 先落地這次編輯，再把那一段拆成獨立圖層
    /// （前／中／後收進一個群組，像素位置不變）。
    /// 這是我們對「一段文字多種樣式」的作法：不做混合樣式，改用分開的圖層各自調（使用者 2026-09-06 明示）。
    /// 只從選取框旁的小按鈕叫（按鈕不可聚焦）—— 放在選單上一點，焦點就離開編輯框、編輯先落地、
    /// 狀態被清掉，什麼都不會發生（使用者回報「點了沒有用」）。
    /// </summary>
    private void SplitSelectedText()
    {
        var box = _canvasEditBox;
        var layer = _canvasEditLayer;
        var elementId = _canvasEditElement?.Id;
        if (box == null || layer == null || elementId == null) return;
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var length = Math.Abs(box.SelectionEnd - box.SelectionStart);
        if (length <= 0)
        {
            Toasts.Show("先選取要分離的那幾個字");
            return;
        }

        CommitCanvasTextEdit();   // 這次編輯先進 history，分離另算一步
        var session = Canvas.Session;
        if (session == null || layer.FindElement(elementId.Value) is not TextElement element) return;
        if (element.Deform is { IsIdentity: false })
        {
            Toasts.Show("有彎曲／透視變形的文字不能分離，先重設變形");
            return;
        }

        session.SelectedElement = null;
        var result = VectorCommands.SplitText(session.Document, session.History, layer, element, start, length);
        if (result == null)
        {
            Toasts.Show("這段選取拆不出新的圖層（整段都選了？）");
            return;
        }
        _layersContent.Refresh();
        RefreshUiState();
        Toasts.Show($"已分離成 {result.Value.Group.Children.Count} 個文字圖層，收在群組「{result.Value.Group.Name}」");
    }
}
