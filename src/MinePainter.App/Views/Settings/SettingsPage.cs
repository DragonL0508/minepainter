using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定視窗裡的一頁。頁面自己負責「即時套用」——改了就寫進 AppSettings，
/// 由 <see cref="SettingsWindow"/> 關窗時統一 Save（同舊的各別設定視窗行為）。
/// </summary>
public abstract class SettingsPage : UserControl
{
    /// <summary>頁面在右側大標題列顯示的一行說明（null＝不顯示）。</summary>
    public virtual string? Description => null;

    /// <summary>
    /// 這一頁要先吃掉按鍵嗎（快捷鍵頁在錄鍵時要攔下所有鍵）。
    /// 回傳 true＝已處理，視窗不再走 Esc 關窗等預設行為。
    /// </summary>
    public virtual bool HandleKeyDown(KeyEventArgs e) => false;

    /// <summary>每次切到這一頁時呼叫（重新讀當下狀態用）。</summary>
    public virtual void OnShown() { }
}

/// <summary>設定頁共用的排版零件，讓四頁的字級／間距長得一樣。</summary>
internal static class SettingsUi
{
    /// <summary>
    /// 把一頁的內容包成可捲動的。設定視窗給每頁的是「一塊固定大小的地方」（不是無限高），
    /// 所以要不要捲、捲哪一段由頁面自己決定——快捷鍵頁就是搜尋框釘住、只捲清單。
    /// </summary>
    public static Control Scroll(Control content) => new ScrollViewer
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };

    /// <summary>區塊小標題（如「更新」「畫布背景圖」）。</summary>
    public static Control Section(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Bold,
        Foreground = AppTheme.TextBrush,
        Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>灰色說明文字（會自動換行）。</summary>
    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>一列開關：標題 + 選填的灰色說明，勾選立即回呼。</summary>
    public static Control Toggle(string text, string? hint, bool value, Action<bool> changed)
    {
        var box = new CheckBox
        {
            IsChecked = value,
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
        };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);

        if (hint == null) return box;

        var hintText = Hint(hint);
        hintText.Margin = new Thickness(26, 1, 0, 0);
        return new StackPanel { Spacing = 0, Children = { box, hintText } };
    }
}
