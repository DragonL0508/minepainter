using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MinePainter.App.Controls;

/// <summary>
/// 主選單：子選單「必須點擊才展開」（使用者明示），不像 Avalonia 預設滑過就延遲彈出。
/// 頂層項目維持慣例：選單已打開時滑過即切換。
/// </summary>
public sealed class ClickSubmenuMenu : Menu
{
    public ClickSubmenuMenu() : base(new ClickSubmenuInteractionHandler())
    {
    }

    /// <summary>沿用 Menu 的控制項樣板（Avalonia 依 StyleKey 套樣式，子類別沒有這行會整條畫不出來）。</summary>
    protected override Type StyleKeyOverride => typeof(Menu);

    private sealed class ClickSubmenuInteractionHandler : DefaultMenuInteractionHandler
    {
        public ClickSubmenuInteractionHandler() : base(isContextMenu: false)
        {
        }

        private static MenuItem? FindItem(object? source)
        {
            var current = source as StyledElement;
            while (current != null && current is not MenuItem) current = current.Parent;
            return current as MenuItem;
        }

        protected override void PointerEntered(object? sender, RoutedEventArgs e)
        {
            var item = FindItem(e.Source);
            if (item is { IsTopLevel: false, HasSubMenu: true })
            {
                // 只高亮、關掉兄弟已開的子選單，不自動展開
                if (item.Parent is ItemsControl parent)
                {
                    foreach (var sibling in parent.Items.OfType<MenuItem>())
                    {
                        if (ReferenceEquals(sibling, item)) continue;
                        if (sibling.IsSubMenuOpen) sibling.Close();
                        sibling.IsSelected = false;
                    }
                }
                item.IsSelected = true;
                e.Handled = true;
                return;
            }
            base.PointerEntered(sender, e);
        }

        protected override void PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var item = FindItem(e.Source);
            if (item is { IsTopLevel: false, HasSubMenu: true, IsSubMenuOpen: false } &&
                e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
            {
                item.Open();
                e.Handled = true;
                return;
            }
            base.PointerPressed(sender, e);
        }
    }
}
