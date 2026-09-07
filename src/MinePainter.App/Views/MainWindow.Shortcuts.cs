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
    // ---- 快捷鍵 ----

    /// <summary>指令 id → 動作（快捷鍵表在 <see cref="Services.ShortcutMap"/>，可在設定裡改綁）。</summary>
    private Dictionary<string, Action> _shortcutActions = null!;

    private void BuildShortcutActions()
    {
        _shortcutActions = new Dictionary<string, Action>
        {
            ["file.new"] = () => OnNewClicked(null, new RoutedEventArgs()),
            ["file.open"] = () => OnOpenClicked(null, new RoutedEventArgs()),
            ["file.save"] = () => _ = SaveAsync(saveAs: false),
            ["file.saveAs"] = () => _ = SaveAsync(saveAs: true),
            ["file.export"] = () => OnExportClicked(null, new RoutedEventArgs()),
            ["file.exportProject"] = () => OnExportProjectClicked(null, new RoutedEventArgs()),
            ["file.copyImage"] = () => OnCopyFlattenedClicked(null, new RoutedEventArgs()),
            ["file.closeTab"] = () => OnCloseTabClicked(null, new RoutedEventArgs()),

            ["edit.undo"] = () => OnUndoClicked(null, new RoutedEventArgs()),
            ["edit.redo"] = () => OnRedoClicked(null, new RoutedEventArgs()),
            ["edit.cut"] = () => OnCutClicked(null, new RoutedEventArgs()),
            ["edit.copy"] = () => OnCopyClicked(null, new RoutedEventArgs()),
            ["edit.paste"] = () => OnPasteClicked(null, new RoutedEventArgs()),
            ["edit.selectAll"] = () => RunCommand(EditCommands.SelectAll),
            ["edit.deselect"] = () => OnDeselectClicked(null, new RoutedEventArgs()),
            ["edit.invertSelection"] = () => RunCommand(EditCommands.InvertSelection),
            ["edit.erase"] = EraseSelectionWithToast,
            ["edit.fill"] = () => OnFillSelectionClicked(null, new RoutedEventArgs()),

            ["image.crop"] = () => OnCropToSelectionClicked(null, new RoutedEventArgs()),
            ["image.rotateCw"] = () => OnRotateCwClicked(null, new RoutedEventArgs()),
            ["image.rotateCcw"] = () => OnRotateCcwClicked(null, new RoutedEventArgs()),
            ["image.rotate180"] = () => OnRotate180Clicked(null, new RoutedEventArgs()),
            ["image.flatten"] = () => OnFlattenClicked(null, new RoutedEventArgs()),

            ["layer.add"] = () => OnAddLayerClicked(null, new RoutedEventArgs()),
            ["layer.duplicate"] = () => OnDuplicateLayerClicked(null, new RoutedEventArgs()),
            ["layer.mergeDown"] = () => OnMergeLayerDownClicked(null, new RoutedEventArgs()),
            ["layer.flattenText"] = () => OnFlattenTextClicked(null, new RoutedEventArgs()),

            ["view.zoomIn"] = () => Canvas.ZoomBy(1.25),
            ["view.zoomOut"] = () => Canvas.ZoomBy(1 / 1.25),
            ["view.actualSize"] = () => Canvas.SetZoomPercent(100),
            ["view.bestFit"] = () => Canvas.ZoomToFit(),
        };
        foreach (var key in new[] { "brush", "pencil", "eraser", "bgeraser", "eyedropper", "move", "rectselect", "ellipseselect", "lasso", "wand", "fill", "text", "shape", "line", "pen" })
        {
            var toolKey = key;
            _shortcutActions[$"tool.{key}"] = () => SelectTool(toolKey);
        }
        _shortcutActions["layer.transformFree"] = () => BeginTransformFromMenu(TransformMode.Free);
        _shortcutActions["layer.transformPerspective"] = () => BeginTransformFromMenu(TransformMode.Perspective);
        _shortcutActions["layer.transformDistort"] = () => BeginTransformFromMenu(TransformMode.Warp);

        _shortcutActions["image.resize"] = () => OnResizeImageClicked(null, new RoutedEventArgs());
        _shortcutActions["image.canvasSize"] = () => OnCanvasSizeClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.import"] = () => OnImportLayerClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipH"] = () => OnFlipLayerHorizontalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.flipV"] = () => OnFlipLayerVerticalClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.properties"] = () => OnLayerPropertiesClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.removeBackground"] = () => OnRemoveBackgroundClicked(null, new RoutedEventArgs());
        _shortcutActions["layer.removeBackgroundLocal"] = () => OnRemoveBackgroundLocalClicked(null, new RoutedEventArgs());
        _shortcutActions["gadget.youtubePreview"] = () => OnYouTubePreviewClicked(null, new RoutedEventArgs());

        _shortcutActions["adjust.autoLevel"] = () => ApplyAutoLevel();
        foreach (var entry in AdjustmentRegistry.All)
        {
            var e = entry;
            _shortcutActions[$"adjust.{e.TypeId}"] = () => _ = ApplyAdjustmentAsync(e);
        }
        _shortcutActions["effect.repeat"] = OnRepeatEffect;
    }

    private void EraseSelectionWithToast()
    {
        var hadSelection = Canvas.Session?.Selection is { IsEmpty: false };
        RunCommand(EditCommands.EraseSelection);
        // 沒有選取時會清空整層（paint.net 的行為），講清楚免得嚇到人
        Toasts.Show(hadSelection == true ? "已清除選取範圍" : "已清空整個圖層");
    }

    /// <summary>把選單的快捷鍵顯示文字同步到目前的綁定（InputGesture 只是顯示，不註冊按鍵）。</summary>
    private void SyncMenuGestures()
    {
        // ShortcutMap.Changed 在改表的那條執行緒上同步發出（見 ShortcutsSettingsPage.RefreshAll）
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SyncMenuGestures);
            return;
        }
        Walk(MainMenu);

        static void Walk(ItemsControl items)
        {
            foreach (var child in items.Items)
            {
                if (child is not MenuItem mi) continue;
                if (mi.Tag is string id) mi.InputGesture = Services.ShortcutMap.GetGesture(id);
                Walk(mi);
            }
        }
    }

    // ---- 對齊模式（按住 Tab，移動框時吸附畫布四邊與中線；按鍵可自訂） ----

    private void OnGlobalKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (Services.ShortcutMap.Matches("tool.alignHold", e.Key, e.KeyModifiers))
        {
            SetAlignMode(true);
            e.Handled = true;
            return;
        }
        // Tab 無效化：就算對齊模式改綁別的鍵，Tab 也不做焦點跳轉（使用者明示）
        if (e.Key == Avalonia.Input.Key.Tab) e.Handled = true;
    }

    private void OnGlobalKeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // 放開只看鍵本身：按住期間修飾鍵可能已變，比對完整手勢會漏掉 KeyUp
        if (Services.ShortcutMap.MatchesKey("tool.alignHold", e.Key))
        {
            SetAlignMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Avalonia.Input.Key.Tab) e.Handled = true;
    }

    private void SetAlignMode(bool on)
    {
        var session = Canvas.Session;
        if (session == null || session.SnapToCanvas == on) return;
        session.SnapToCanvas = on;
        if (!on) session.SnapGuides = null; // 導線跟著模式收掉
    }

    /// <summary>
    /// 浮動面板（獨立 OS 視窗）沒處理掉的按鍵：當成主視窗的按鍵處理一次。
    /// 面板上的焦點不該讓整套快捷鍵失效 —— 使用者按了「新增圖層」之後就想直接 Ctrl+Z。
    /// </summary>
    private void HandlePanelKey(Avalonia.Input.KeyEventArgs e)
    {
        OnGlobalKeyDown(this, e);
        if (!e.Handled) OnKeyDown(e);
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var session = Canvas.Session;
        if (session == null) return;

        // 打字時不攔截（畫布內編輯框、任何取得焦點的輸入框；
        // 下拉選單聚焦/展開時打字是在搜尋項目，不能被單鍵快捷鍵搶走）
        if (_canvasEditBox?.IsFocused == true) return;
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox or ComboBoxItem) return;

        // 情境鍵（套用／取消）：有東西進行中時才輪到它們，所以擺在一般查表之前。
        // 它們也在快捷鍵表裡，設定 → 快捷鍵 改得到。
        var commit = Services.ShortcutMap.Matches("edit.commitEdit", e.Key, e.KeyModifiers);
        var cancel = Services.ShortcutMap.Matches("edit.cancelEdit", e.Key, e.KeyModifiers);

        // 變形框：套用＝落地、取消＝無損還原
        if (session.Transform != null && (commit || cancel))
        {
            if (commit)
            {
                session.CommitTransform();
                Toasts.Show("已套用變形");
            }
            else
            {
                session.CancelTransform();
                Toasts.Show("已還原變形");
            }
            RefreshUiState();
            e.Handled = true;
            return;
        }

        // 浮動選取內容：套用＝提交、取消＝還原
        if (session.Floating != null && (commit || cancel))
        {
            if (commit)
            {
                session.CommitFloating();
                Toasts.Show("已套用移動的選取內容");
            }
            else
            {
                session.CancelFloating();
                Toasts.Show("已取消移動");
            }
            RefreshUiState();
            e.Handled = true;
            return;
        }

        // 鋼筆路徑：轉為選取／退一個錨點／清除
        if (session.ActiveTool == session.Pen && session.PenPath != null)
        {
            if (commit)
            {
                PenMakeSelection(); // 「套用」在鋼筆的意思就是把路徑定案成選取
                e.Handled = true;
                return;
            }
            if (Services.ShortcutMap.Matches("pen.removeLastPoint", e.Key, e.KeyModifiers))
            {
                PenCommands.RemoveLast(session);
                e.Handled = true;
                return;
            }
            if (cancel)
            {
                PenCommands.Clear(session);
                Toasts.Show("已清除路徑");
                e.Handled = true;
                return;
            }
        }

        // 其餘一律查快捷鍵表（設定 → 快捷鍵 可改綁；CanvasView 也查同一張表）。
        // （選中向量物件時的 Delete 由 CanvasView 先攔下來刪除物件，不會走到這裡）
        var id = Services.ShortcutMap.Match(e.Key, e.KeyModifiers);
        if (id != null && _shortcutActions.TryGetValue(id, out var action))
        {
            action();
            e.Handled = true;
        }
    }
}
