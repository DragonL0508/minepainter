using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 一整個「設定」視窗（VS Code 那種）：左邊分類、右邊該分類的內容，
/// 取代原本散在選單裡的快捷鍵／主題／檔案關聯三個獨立小視窗。
/// 選單各分類只是「開這個視窗並選好那一頁」，不再各開各的窗。
/// 所有改動即時生效，關窗時由呼叫端 Save 一次。
/// </summary>
public sealed class SettingsWindow : ModalDialog
{
    /// <summary>分類 id；選單項用 Tag 指定要開哪一頁。</summary>
    public enum Page
    {
        General,
        Appearance,
        Shortcuts,
        BackgroundRemoval,
        FileAssociations,
    }

    private sealed record Entry(Page Id, string Title, MaterialIconKind Icon, Func<SettingsPage> Create);

    /// <summary>使用者按了「立即檢查更新」；主視窗接手（會先關掉設定視窗）。</summary>
    public event Action? CheckUpdatesRequested;

    private readonly List<(Entry Entry, Border Item, Border Bar, TextBlock Label, MaterialIcon Icon)> _nav = [];
    private readonly Dictionary<Page, SettingsPage> _pages = new();
    private readonly ContentControl _host = new();
    private readonly TextBlock _pageTitle = new()
    {
        FontSize = 15,
        FontWeight = FontWeight.Bold,
        Foreground = AppTheme.TextBrush,
    };
    private readonly TextBlock _pageDescription = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private SettingsPage? _current;
    private Page _selected;

    public SettingsWindow(Page initial = Page.General) : base("設定", 800)
    {
        var entries = new[]
        {
            new Entry(Page.General, "一般", MaterialIconKind.TuneVariant, () => new GeneralSettingsPage(() => CheckUpdatesRequested?.Invoke())),
            new Entry(Page.Appearance, "外觀", MaterialIconKind.Palette, () => new AppearanceSettingsPage()),
            new Entry(Page.Shortcuts, "快捷鍵", MaterialIconKind.Keyboard, () => new ShortcutsSettingsPage()),
            new Entry(Page.BackgroundRemoval, "AI 去背", MaterialIconKind.AutoFix, () => new BackgroundRemovalSettingsPage()),
            new Entry(Page.FileAssociations, "檔案關聯", MaterialIconKind.FileLink, () => new FileAssociationsSettingsPage()),
        };

        var navList = new StackPanel { Spacing = 1, Margin = new Thickness(6, 8) };
        foreach (var entry in entries) navList.Children.Add(BuildNavItem(entry));

        var sidebar = new Border
        {
            Width = 176,
            Background = AppTheme.InnerBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            Child = navList,
        };
        DockPanel.SetDock(sidebar, Dock.Left);

        var header = new Border
        {
            Padding = new Thickness(16, 12, 16, 10),
            BorderBrush = AppTheme.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Spacing = 2, Children = { _pageTitle, _pageDescription } },
        };
        DockPanel.SetDock(header, Dock.Top);

        // 內容區是固定大小的一塊；要不要捲由每一頁自己決定（見 SettingsUi.Scroll）
        var pageArea = new Border { Padding = new Thickness(16, 12), Child = _host };

        var body = new Border
        {
            Height = 480,
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new DockPanel { Children = { sidebar, new DockPanel { Children = { header, pageArea } } } },
        };

        SetBody(body, ButtonRow(MakeButton("關閉", primary: true)));
        Select(initial);
    }

    /// <summary>切到某一分類（第一次進去才真的把那頁建出來）。</summary>
    public void Select(Page page)
    {
        if (!_pages.TryGetValue(page, out var content))
        {
            content = _nav.First(n => n.Entry.Id == page).Entry.Create();
            _pages[page] = content;
        }

        _current = content;
        _selected = page;
        _host.Content = content;
        _pageTitle.Text = _nav.First(n => n.Entry.Id == page).Entry.Title;
        _pageDescription.Text = content.Description ?? "";
        _pageDescription.IsVisible = content.Description != null;
        content.OnShown();

        foreach (var (entry, item, bar, label, icon) in _nav)
        {
            var selected = entry.Id == page;
            item.Background = selected ? AppTheme.HeaderBrush : Brushes.Transparent;
            bar.IsVisible = selected;
            label.FontWeight = selected ? FontWeight.Bold : FontWeight.Normal;
            label.Foreground = selected ? AppTheme.TextBrush : AppTheme.TextMutedBrush;
            icon.Foreground = selected ? AppTheme.TextBrush : AppTheme.TextMutedBrush;
        }
    }

    private Control BuildNavItem(Entry entry)
    {
        var icon = new MaterialIcon
        {
            Kind = entry.Icon,
            Width = 16,
            Height = 16,
            Foreground = AppTheme.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 12,
            Foreground = AppTheme.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 選取時左側的一條 accent（同 VS Code 的活動列）
        var bar = new Border
        {
            Width = 2,
            Margin = new Thickness(0, 6),
            Background = AppTheme.AccentBrush,
            CornerRadius = new CornerRadius(1),
            IsVisible = false,
        };
        DockPanel.SetDock(bar, Dock.Left);

        var item = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                Children =
                {
                    bar,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { icon, label },
                    },
                },
            },
        };
        item.PointerPressed += (_, _) => Select(entry.Id);
        item.PointerEntered += (_, _) =>
        {
            if (_selected != entry.Id) item.Background = AppTheme.ChromeBrush;
        };
        item.PointerExited += (_, _) =>
        {
            if (_selected != entry.Id) item.Background = Brushes.Transparent;
        };

        _nav.Add((entry, item, bar, label, icon));
        return item;
    }

    /// <summary>設定是即時生效的，Enter 不該當成「確定」把窗關掉。</summary>
    protected override void OnConfirmKey()
    {
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 快捷鍵頁錄鍵中要先吃掉所有按鍵（含 Esc），不然 Esc 會關掉整個設定視窗
        if (_current?.HandleKeyDown(e) == true)
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
