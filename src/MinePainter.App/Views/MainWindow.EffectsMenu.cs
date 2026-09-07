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
    // ---- 調整／效果（paint.net 的 Adjustments / Effects 選單） ----

    /// <summary>「調整」選單順序照 paint.net；我們自己加的（曝光度、色溫）接在後面。</summary>
    private static readonly string[] AdjustmentMenuOrder =
        ["blackWhite", "brightnessContrast", "curves", "hueSaturation", "invert", "levels", "posterize", "sepia",
         "exposure", "temperatureTint", "colorBalance", "photoFilter", "channelMixer", "threshold", "lut"];

    internal static IReadOnlyList<string> AdjustmentMenuOrderForTests => AdjustmentMenuOrder;

    private IEffect? _lastEffect;
    private MenuItem? _repeatEffectItem;

    /// <summary>選單項目左邊的小圖示（單色、跟著文字顏色）—— 使用者 2026-09-06：「只有文字略顯單調」。</summary>
    private static MaterialIcon MenuIcon(MaterialIconKind kind) => new() { Kind = kind, Width = 16, Height = 16 };

    private static MaterialIconKind AdjustmentIcon(string typeId) => typeId switch
    {
        "brightnessContrast" => MaterialIconKind.Brightness6,
        "curves" => MaterialIconKind.ChartBellCurveCumulative,
        "hueSaturation" => MaterialIconKind.Palette,
        "levels" => MaterialIconKind.Tune,
        "posterize" => MaterialIconKind.Stairs,
        "blackWhite" => MaterialIconKind.ContrastBox,
        "invert" => MaterialIconKind.InvertColors,
        "sepia" => MaterialIconKind.ImageFilterVintage,
        "exposure" => MaterialIconKind.WhiteBalanceSunny,
        "temperatureTint" => MaterialIconKind.Thermometer,
        "colorBalance" => MaterialIconKind.ScaleBalance,
        "photoFilter" => MaterialIconKind.CameraIris,
        "channelMixer" => MaterialIconKind.TuneVertical,
        "threshold" => MaterialIconKind.ContrastCircle,
        "lut" => MaterialIconKind.GradientHorizontal,
        _ => MaterialIconKind.TuneVariant,
    };

    private static MaterialIconKind EffectCategoryIcon(string category) => category switch
    {
        "藝術" => MaterialIconKind.Brush,
        "模糊" => MaterialIconKind.Blur,
        "色彩" => MaterialIconKind.Palette,
        "扭曲" => MaterialIconKind.Waves,
        "雜訊" => MaterialIconKind.Grain,
        "物件" => MaterialIconKind.ShapeOutline,
        "相片" => MaterialIconKind.Camera,
        "演算" => MaterialIconKind.FunctionVariant,
        "風格化" => MaterialIconKind.TextureBox,
        _ => MaterialIconKind.AutoFix,
    };

    private void BuildEffectMenus()
    {
        var auto = new MenuItem { Header = "自動色階", Tag = "adjust.autoLevel", Icon = MenuIcon(MaterialIconKind.AutoFix) };
        auto.Click += (_, _) => ApplyAutoLevel();
        AdjustmentsMenu.Items.Add(auto);

        // 順序表沒列到的登錄項也要出現（2026-09-06 新增的調整就是因為漏列而在選單上看不到）
        var ordered = AdjustmentMenuOrder
            .Concat(AdjustmentRegistry.All.Select(e => e.TypeId).Where(id => !AdjustmentMenuOrder.Contains(id)));
        foreach (var typeId in ordered)
        {
            var entry = AdjustmentRegistry.Find(typeId);
            if (entry == null) continue;
            var item = new MenuItem
            {
                Header = entry.HasDialog ? entry.DisplayName + "…" : entry.DisplayName,
                Tag = "adjust." + entry.TypeId,
                Icon = MenuIcon(AdjustmentIcon(entry.TypeId)),
            };
            item.Click += (_, _) => _ = ApplyAdjustmentAsync(entry);
            AdjustmentsMenu.Items.Add(item);
        }

        _repeatEffectItem = new MenuItem { Header = "重複上次效果", Tag = "effect.repeat", IsEnabled = false, Icon = MenuIcon(MaterialIconKind.Repeat) };
        _repeatEffectItem.Click += (_, _) => OnRepeatEffect();
        EffectsMenu.Items.Add(_repeatEffectItem);
        EffectsMenu.Items.Add(new Separator());

        // 非破壞性：效果／調整記錄在圖層的效果堆疊（圖層屬性可回頭改、排序、存預設集）

        foreach (var category in EffectRegistry.Categories)
        {
            var sub = new MenuItem { Header = category, Icon = MenuIcon(EffectCategoryIcon(category)) };
            foreach (var entry in EffectRegistry.InCategory(category))
            {
                var e = entry;
                var item = new MenuItem { Header = e.Name + "…" };
                item.Click += (_, _) => _ = ApplyEffectAsync(Services.EffectParamMemory.Recall(e.Create(), Canvas.Session?.Foreground ?? SKColors.Black), e.Name, showDialog: true);
                sub.Items.Add(item);
            }
            EffectsMenu.Items.Add(sub);
        }
    }

    private Task ApplyAdjustmentAsync(AdjustmentRegistry.Entry entry) =>
        ApplyEffectAsync(Services.EffectParamMemory.Recall(new AdjustmentEffect(entry.CreateDefault()), Canvas.Session?.Foreground ?? SKColors.Black), entry.DisplayName, entry.HasDialog);

    private void ApplyAutoLevel()
    {
        var session = CommitPending();
        if (session == null) return;
        // 群組：自動色階也走群組的效果堆疊（直方圖取整組合成後的樣子）
        if (session.Document.ActiveLayer is GroupLayer group)
        {
            var groupEntry = LayerEffect.Create(new AdjustmentEffect(new LevelsAdjustment()),
                session.Selection?.Clone().Mask, session.Foreground);
            using var groupPreview = new LayerEffectPreview(session, group, groupEntry, isNew: true);
            var groupLevels = LevelsAdjustment.FromHistogram(groupPreview.Histogram());
            groupPreview.Commit(new AdjustmentEffect(groupLevels));
            _lastEffect = new AdjustmentEffect(groupLevels);
            Toasts.Show("自動色階（已記錄在群組）");
            AfterEffect();
            return;
        }
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個圖層");
            return;
        }
        using var fx = new EffectSession(session, layer);
        if (fx.IsEmpty) return;
        var levels = LevelsAdjustment.FromHistogram(fx.Histogram());
        // 效果一律記錄在圖層效果堆疊（非破壞性；使用者 2026-09-06 明示不再提供直接寫入像素的選項）
        var effect = new AdjustmentEffect(levels);
        LayerEffectCommands.Add(session.Document, session.History, layer,
            LayerEffect.Create(effect, session.Selection?.Clone().Mask, session.Foreground));
        _lastEffect = effect;
        Toasts.Show("自動色階（已記錄在圖層）");
        AfterEffect();
    }

    /// <summary>
    /// 套用效果到作用中圖層（受選取範圍限制）。有對話框時即時預覽、確定才進 history；
    /// 沒有對話框（負片／黑白／懷舊／重複上次）直接套用。
    /// </summary>
    private async Task ApplyEffectAsync(IEffect effect, string name, bool showDialog)
    {
        var session = CommitPending();
        if (session == null) return;

        // 群組：效果一律進群組的效果堆疊（作用在整組合成後的樣子，組內每一層都吃得到）
        if (session.Document.ActiveLayer is GroupLayer group)
        {
            await ApplyToLayerStackAsync(session, group, effect, name, showDialog);
            return;
        }
        if (session.Document.ActiveLayer is not RasterLayer layer)
        {
            Toasts.Show("請先選擇一個點陣圖層或群組");
            return;
        }
        // 效果一律記錄在圖層效果堆疊（非破壞性），可在圖層屬性重新調整；不再提供直接寫入像素的模式
        await ApplyToLayerStackAsync(session, layer, effect, name, showDialog);
    }

    /// <summary>非破壞性：效果進圖層效果堆疊（有選取就帶遮罩），對話框即時預覽由合成器背景重算。</summary>
    private async Task ApplyToLayerStackAsync(EditorSession session, LayerNode layer, IEffect effect, string name, bool showDialog)
    {
        var entry = LayerEffect.Create(effect, session.Selection?.Clone().Mask, session.Foreground);
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: true);
        if (!showDialog)
        {
            preview.Commit(effect);
            _lastEffect = effect;
            Toasts.Show($"{name}（已記錄在{(layer is GroupLayer ? "群組" : "圖層")}）");
            AfterEffect();
            return;
        }

        var dialog = new EffectDialog(preview, effect, name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            preview.Commit(dialog.Result);
            _lastEffect = dialog.Result;
            Services.EffectParamMemory.Remember(dialog.Result);
            Toasts.Show($"{name}（已記錄在{(layer is GroupLayer ? "群組" : "圖層")}）");
        }
        else
        {
            preview.Cancel();
        }
        AfterEffect();
    }

    /// <summary>圖層屬性視窗要求重新編輯堆疊裡的某一筆。</summary>
    public async Task EditLayerEffectAsync(LayerNode layer, LayerEffect entry)
    {
        var session = CommitPending();
        if (session == null) return;
        using var preview = new LayerEffectPreview(session, layer, entry, isNew: false);
        var dialog = new EffectDialog(preview, entry.Effect, entry.Name);
        await dialog.ShowDialog(this);
        await dialog.WaitIdleAsync();
        if (dialog.Confirmed)
        {
            preview.Commit(dialog.Result);
            Services.EffectParamMemory.Remember(dialog.Result);
        }
        else preview.Cancel();
        AfterEffect();
    }

    private void AfterEffect()
    {
        _layersContent.Refresh();
        _layersContent.SyncPropertiesWindow();
        RefreshUiState();
        if (_repeatEffectItem != null)
        {
            // paint.net 式：選單直接寫出上次是哪個效果（「重複 高斯模糊」）
            _repeatEffectItem.IsEnabled = _lastEffect != null;
            _repeatEffectItem.Header = _lastEffect is { } last ? $"重複「{last.Name}」" : "重複上次效果";
        }
    }

    private void OnRepeatEffect()
    {
        if (_lastEffect is { } effect) _ = ApplyEffectAsync(effect, effect.Name, showDialog: false);
    }

    private void AfterDocumentResized(EditorSession session)
    {
        // 標籤由 Document.SizeChanged 統一更新（undo/redo 也才會同步）；這裡只處理縮放
        Canvas.ZoomToFit();
    }
}
