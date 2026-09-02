using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MinePainter.App.Controls;

/// <summary>
/// 主選單：子選單「必須點擊才展開」（使用者明示），不像 Avalonia 預設滑過就延遲彈出；
/// 點開之後滑鼠移出去也不會自動關（使用者明示），只有滑到兄弟項目、點別處或 Esc 才收。
/// 頂層項目維持慣例：選單已打開時滑過即切換。
/// </summary>
public sealed class ClickSubmenuMenu : Menu
{
    public ClickSubmenuMenu()
        : base(new ClickSubmenuInteractionHandler(isContextMenu: false, clickOnlyAtTopLevel: false))
    {
    }

    /// <summary>沿用 Menu 的控制項樣板（Avalonia 依 StyleKey 套樣式，子類別沒有這行會整條畫不出來）。</summary>
    protected override Type StyleKeyOverride => typeof(Menu);
}

/// <summary>
/// 比照主選單行為的 MenuFlyout（按鈕下拉）：有子清單的項目一律點擊才展開、滑鼠移出不自動關。
/// 在 flyout 裡第一層項目對 Avalonia 來說就是「頂層」，預設滑過會直接切換展開，所以這裡連頂層一起管。
/// 會播與 <see cref="AnimatedMenuFlyout"/> 相同的開啟動畫。
/// </summary>
public sealed class ClickSubmenuMenuFlyout : MenuFlyout
{
    protected override Control CreatePresenter()
    {
        return new MenuFlyoutPresenter(
            new ClickSubmenuInteractionHandler(isContextMenu: true, clickOnlyAtTopLevel: true))
        {
            ItemsSource = Items,
            [!ItemsControl.ItemTemplateProperty] = this[!ItemTemplateProperty],
            [!ItemsControl.ItemContainerThemeProperty] = this[!ItemContainerThemeProperty],
        };
    }

    protected override void OnOpened()
    {
        base.OnOpened();
        if (Popup.Child is Control presenter) PopupAnimator.Play(presenter);
    }
}

/// <summary>
/// 「點擊才展開子選單、移出不自動關」的互動處理器。
/// clickOnlyAtTopLevel：頂層項目也套用（flyout 用）；否則頂層維持 Avalonia 慣例。
/// </summary>
internal sealed class ClickSubmenuInteractionHandler(bool isContextMenu, bool clickOnlyAtTopLevel)
    : DefaultMenuInteractionHandler(isContextMenu)
{
    private static MenuItem? FindItem(object? source)
    {
        var current = source as StyledElement;
        while (current != null && current is not MenuItem) current = current.Parent;
        return current as MenuItem;
    }

    private bool Managed(MenuItem item) => item.HasSubMenu && (clickOnlyAtTopLevel || !item.IsTopLevel);

    protected override void PointerEntered(object? sender, RoutedEventArgs e)
    {
        var item = FindItem(e.Source);
        if (item != null && Managed(item))
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

    protected override void PointerExited(object? sender, RoutedEventArgs e)
    {
        // 預設行為：滑出有子選單的項目（且不在子選單上）就延遲關掉它 —— 使用者不要這個。
        // 已點開的子清單留著，直到滑到兄弟項目、點別處或 Esc。
        var item = FindItem(e.Source);
        if (item is { HasSubMenu: true, IsSubMenuOpen: true })
        {
            e.Handled = true;
            return;
        }
        base.PointerExited(sender, e);
    }

    protected override void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var item = FindItem(e.Source);
        if (item != null && Managed(item) && !item.IsSubMenuOpen &&
            e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
        {
            item.Open();
            e.Handled = true;
            return;
        }
        base.PointerPressed(sender, e);
    }
}
