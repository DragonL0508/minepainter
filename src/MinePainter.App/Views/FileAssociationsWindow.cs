using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Services;

namespace MinePainter.App.Views;

/// <summary>
/// 設定 → 檔案關聯：勾選要交給 MinePainter 開的格式，按下去就寫進登錄檔（只寫 HKCU）。
/// Windows 不讓程式自己搶預設程式，所以主按鈕在登記完之後直接把使用者送到
/// 「設定 → 預設應用程式 → MinePainter」，剩下的兩下由他自己按。
/// </summary>
public sealed class FileAssociationsWindow : ModalDialog
{
    private readonly List<(FileAssociations.Kind Kind, CheckBox Box)> _rows = [];

    public FileAssociationsWindow() : base("檔案關聯", 460)
    {
        var anyRegistered = FileAssociations.All.Any(k => FileAssociations.IsRegistered(k.Extension));
        var stale = FileAssociations.All.Any(k => FileAssociations.IsStale(k.Extension));

        var list = new StackPanel { Spacing = 2 };
        foreach (var kind in FileAssociations.All)
        {
            var box = new CheckBox
            {
                // 沒登記過的第一次進來：全部預設勾起來（使用者多半就是要全部）
                IsChecked = anyRegistered ? FileAssociations.IsRegistered(kind.Extension) : true,
                Content = new TextBlock
                {
                    Text = $"{kind.Extension}　{kind.Description}",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                FontSize = 12,
                MinHeight = 0,
                Padding = new Thickness(6, 2, 0, 2),
            };
            _rows.Add((kind, box));
            list.Children.Add(box);
        }

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "勾選要用 MinePainter 開啟的檔案格式。登記之後，這些檔案在檔案總管按右鍵的「開啟方式」就會出現 MinePainter。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                list,
                new TextBlock
                {
                    Text = "Windows 不允許程式自行指定預設開啟程式，要在系統設定裡按一下。"
                         + "按「登記並前往設定」會直接開到 MinePainter 那一頁。",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (stale)
        {
            body.Children.Insert(1, new TextBlock
            {
                Text = "先前登記的路徑指向舊的 MinePainter.exe（程式被搬過位置）。重新登記一次就會更新。",
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var apply = new Button { Content = "登記並前往設定", Padding = new Thickness(14, 6), FontSize = 12 };
        apply.Classes.Add("accent");
        apply.Click += (_, _) =>
        {
            ApplySelection();
            FileAssociations.OpenWindowsDefaultAppsSettings();
            Confirmed = true;
            Close();
        };

        var applyOnly = new Button { Content = "只登記", Padding = new Thickness(14, 6), FontSize = 12 };
        applyOnly.Click += (_, _) =>
        {
            ApplySelection();
            Confirmed = true;
            Close();
        };

        var remove = new Button { Content = "全部移除", Padding = new Thickness(14, 6), FontSize = 12 };
        remove.Click += (_, _) =>
        {
            FileAssociations.RemoveAll();
            AppSettings.Instance.FileAssociationsOptOut = true;
            AppSettings.Instance.FileAssociationsRegistered = true;
            Confirmed = true;
            Close();
        };

        SetBody(body, ButtonRow(apply, applyOnly, remove, MakeButton("取消")));
    }

    private void ApplySelection()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Kind.Extension).ToList();
        FileAssociations.Apply(chosen);
        // 手動勾成空的＝跟按「全部移除」同一件事，啟動時不要再自動塞回去
        var settings = AppSettings.Instance;
        settings.FileAssociationsOptOut = chosen.Count == 0;
        settings.FileAssociationsRegistered = true;
    }

    /// <summary>Enter＝主按鈕（登記並前往設定）。</summary>
    protected override void OnConfirmKey()
    {
        ApplySelection();
        FileAssociations.OpenWindowsDefaultAppsSettings();
        Confirmed = true;
        Close();
    }
}
