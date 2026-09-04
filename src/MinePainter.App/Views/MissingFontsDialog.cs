using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.Core.Vectors;

namespace MinePainter.App.Views;

/// <summary>
/// 開專案檔時，檔案裡用到的字型這台機器沒有裝就跳這個。
///
/// 專案檔只記字型的家族名，換一台機器沒裝那支字型，Skia 會安靜地換一支畫出來 ——
/// 字還在、位置與寬度卻全變了，而且沒有任何提示。這個對話框就是那個提示：
/// 列出缺哪幾支、哪段文字在用，讓使用者自己決定要裝字型還是換字型。
/// </summary>
public sealed class MissingFontsDialog : ModalDialog
{
    public MissingFontsDialog(string fileName, IReadOnlyList<MissingFont> missing)
        : base("缺少字型", 460)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = $"「{fileName}」用到 {missing.Count} 種這台電腦沒有的字型，" +
                   "那些文字會先用系統挑的替代字型顯示（排版與寬度可能跑掉）。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var font in missing)
        {
            var row = new DockPanel();
            var count = new TextBlock
            {
                Text = font.TextCount == 1 ? "1 段文字" : $"{font.TextCount} 段文字",
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);

            var name = new StackPanel { Spacing = 1 };
            name.Children.Add(new TextBlock
            {
                Text = font.Family,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(font.Sample))
            {
                name.Children.Add(new TextBlock
                {
                    Text = $"例如：{font.Sample}",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            row.Children.Add(name);

            list.Children.Add(new Border
            {
                Background = AppTheme.InnerBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7),
                Child = row,
            });
        }

        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 260,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = list,
        });

        body.Children.Add(new TextBlock
        {
            Text = "裝好字型後重新開啟這個檔案就會恢復原本的排版；" +
                   "也可以直接選中文字、在工具列換一支有裝的字型。",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        SetBody(body, ButtonRow(MakeButton("知道了", primary: true, confirm: true)));
    }
}
