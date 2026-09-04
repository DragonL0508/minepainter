using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.Core.Vectors;

namespace MinePainter.App.Views;

/// <summary>
/// 開專案檔時，檔案裡用到的字型這台機器沒有裝就跳這個。
///
/// 專案檔只記字型的家族名，換一台機器沒裝那支，Skia 會安靜地換一支畫出來 ——
/// 字還在、排版與寬度卻全變了。這裡直接讓使用者當場挑替代字型（一步 undo 就換完），
/// 不挑就關掉、維持系統自己挑的替代字面。
/// </summary>
public sealed class MissingFontsDialog : ModalDialog
{
    private const string Keep = "（不替換）";

    private readonly Dictionary<string, ComboBox> _pickers = new();

    /// <summary>使用者選好的替換：原字型 → 新字型（沒選的不在裡面）。</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; private set; } =
        new Dictionary<string, string>();

    public MissingFontsDialog(string projectName, IReadOnlyList<MissingFont> missing)
        : base($"{projectName} 缺少以下字型：", 520)
    {
        var families = Services.FontCatalog.Families;
        var list = new StackPanel { Spacing = 6 };

        foreach (var font in missing)
        {
            var picker = new ComboBox
            {
                Width = 210,
                FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MaxDropDownHeight = 360,
                ItemTemplate = Services.FontCatalog.FamilyItemTemplate(170),
                SelectionBoxItemTemplate = Services.FontCatalog.SelectionBoxTemplate(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            picker.Items.Add(Keep);
            foreach (var f in families) picker.Items.Add(f);
            picker.SelectedIndex = 0;
            _pickers[font.Family] = picker;

            var name = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock
            {
                Text = font.Family,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            name.Children.Add(new TextBlock
            {
                Text = font.TextCount == 1 ? $"1 段文字・{font.Sample}" : $"{font.TextCount} 段文字・{font.Sample}",
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var row = new DockPanel();
            DockPanel.SetDock(picker, Dock.Right);
            row.Children.Add(picker);
            row.Children.Add(name);

            list.Children.Add(new Border
            {
                Background = AppTheme.InnerBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7),
                Child = row,
            });
        }

        var body = new ScrollViewer
        {
            MaxHeight = 320,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = list,
        };

        SetBody(body, ButtonRow(
            MakeButton("替換", primary: true, confirm: true),
            MakeButton("略過")));
    }

    /// <summary>按下「替換」時把選好的對應收起來（沒選的不算）。</summary>
    protected override bool Validate()
    {
        var picked = new Dictionary<string, string>();
        foreach (var (family, picker) in _pickers)
        {
            if (picker.SelectedItem is string chosen && chosen != Keep) picked[family] = chosen;
        }
        Replacements = picked;
        return true;
    }
}
