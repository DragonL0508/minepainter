using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MinePainter.App.Controls;

/// <summary>
/// 所有下拉清單（ComboBox）收起時都能用滾輪切換選項：滾輪往上＝前一項、往下＝後一項
/// （與拉條「滾輪往上＝數值變小」同一個方向）。打開時交給清單自己捲。
/// 用類別層級的處理器一次掛給全部 ComboBox，不必每個下拉各自接（之前只有部分接了，使用者 2026-09-07 回報不一致）。
/// </summary>
public static class ComboBoxWheel
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.PointerWheelChangedEvent.AddClassHandler<ComboBox>(OnWheel, RoutingStrategies.Tunnel);
    }

    private static void OnWheel(ComboBox combo, PointerWheelEventArgs e)
    {
        if (e.Handled || combo.IsDropDownOpen || !combo.IsEnabled || combo.ItemCount == 0) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var step = delta > 0 ? -1 : 1;
        var index = combo.SelectedIndex;
        for (var next = index + step; next >= 0 && next < combo.ItemCount; next += step)
        {
            if (combo.ContainerFromIndex(next) is ComboBoxItem { IsEnabled: false }) continue;
            combo.SelectedIndex = next;
            break;
        }
        e.Handled = true;   // 外層的捲動區不要跟著捲
    }
}
