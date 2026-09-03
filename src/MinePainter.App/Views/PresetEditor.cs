using Avalonia.Controls;
using Avalonia.Threading;
using MinePainter.App.Services;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 編輯預設集：開一份暫存文件，放一段「Aa」文字圖層並套上預設集的效果堆疊，
/// 用現成的圖層屬性視窗（效果堆疊的新增／排序／開關／調參都在那裡、預覽圖即時更新）編輯；
/// 視窗關掉時把圖層上的堆疊寫回預設集檔（名稱欄改了就一併改名）。
/// </summary>
public static class PresetEditor
{
    private static readonly Dictionary<string, LayerPropertiesWindow> Open = new();

    public static void Edit(Window owner, EffectPreset preset, Action<string>? notify = null)
    {
        if (Open.TryGetValue(preset.Path, out var existing))
        {
            existing.Activate();
            return;
        }

        var doc = new Document(360, 220);
        var session = new EditorSession(doc);
        var layer = VectorCommands.CreateTextLayerSilently(doc);
        var family = FontCatalog.Families.FirstOrDefault(f => f.Contains("JhengHei") || f.Contains("正黑"))
                     ?? EmbeddedFonts.FamilyName;
        var text = new TextElement
        {
            Text = "Aa",
            FontFamily = family,
            FontSize = 120,
            Color = new SKColor(0xFFE8E8EC),
            Position = new SKPoint(0, 0),
        };
        lock (doc.SyncRoot)
        {
            layer.AddElement(text);
            // 置中：先放 (0,0) 量出範圍再平移
            var b = text.Bounds;
            var dx = (doc.Width - b.Width) / 2f - b.Left;
            var dy = (doc.Height - b.Height) / 2f - b.Top;
            var centered = text with { Position = new SKPoint(dx, dy) };
            layer.ReplaceElement(centered);
            layer.Name = preset.Name;
            layer.SetEffects(preset.Effects
                .Select(e => LayerEffect.Create(e.Effect, null, SKColors.Black) with { Enabled = e.Enabled })
                .ToList());
        }
        var win = new LayerPropertiesWindow(session, layer, presetMode: true);
        Open[preset.Path] = win;

        // 效果快取算完（worker 執行緒）→ 預覽圖跟著換。要先訂閱再觸發合成，
        // 不然 worker 可能在訂閱前就算完、之後沒事件，預覽就停在沒效果的「Aa」
        Action<RasterLayer> rendered = l =>
        {
            if (ReferenceEquals(l, layer)) Dispatcher.UIThread.Post(win.RefreshPreview);
        };
        LayerEffectRenderer.LayerRendered += rendered;
        doc.NotifyChanged(doc.Bounds);
        // 保險：開窗後再刷兩次（合成很快就結束的情況）
        win.Opened += (_, _) =>
        {
            DispatcherTimer.RunOnce(win.RefreshPreview, TimeSpan.FromMilliseconds(150));
            DispatcherTimer.RunOnce(win.RefreshPreview, TimeSpan.FromMilliseconds(600));
        };

        win.Closed += (_, _) =>
        {
            LayerEffectRenderer.LayerRendered -= rendered;
            Open.Remove(preset.Path);
            try
            {
                IReadOnlyList<LayerEffect> effects;
                string newName;
                lock (doc.SyncRoot)
                {
                    effects = layer.Effects;
                    newName = layer.Name.Trim();
                }
                var current = EffectPresetStore.LoadFile(preset.Path) ?? preset;
                if (newName.Length > 0 && newName != current.Name)
                {
                    if (!EffectPresetStore.Rename(current, newName)) notify?.Invoke("改名失敗（同名預設集已存在？），內容仍已儲存");
                    else current = EffectPresetStore.LoadFile(EffectPresetStore.LoadAll().First(p => p.Folder == current.Folder && p.Name == newName).Path) ?? current;
                }
                EffectPresetStore.SaveEntries(current.Name, effects.Select(e => (e.Effect, e.Enabled)), current.Folder);
                notify?.Invoke($"已更新預設集「{current.Name}」");
            }
            catch (Exception)
            {
                notify?.Invoke("預設集儲存失敗");
            }
            finally
            {
                session.Dispose();
                doc.Dispose();
            }
        };
        win.Show(owner);
    }
}
