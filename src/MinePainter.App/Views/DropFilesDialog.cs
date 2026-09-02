using Avalonia.Controls;
using Avalonia.Media;

namespace MinePainter.App.Views;

/// <summary>
/// 影像檔拖進 app 時的選擇：開啟（各自成一份文件）／加入圖層（放進目前文件、插在作用中圖層上方）／取消。
/// 只有一份文件都沒開時只能「開啟」。
/// </summary>
public sealed class DropFilesDialog : ModalDialog
{
    public enum Choice
    {
        Cancel,
        Open,
        AddLayers,
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    public DropFilesDialog(IReadOnlyList<string> names, bool canAddLayers) : base("拖入的檔案", 400)
    {
        Button Make(string text, Choice choice, bool primary = false, bool enabled = true)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Avalonia.Thickness(14, 6),
                FontSize = 12,
                IsEnabled = enabled,
            };
            if (primary) b.Classes.Add("accent");
            b.Click += (_, _) =>
            {
                Result = choice;
                Close();
            };
            return b;
        }

        var shown = names.Count <= 4 ? names : names.Take(3).Append($"…共 {names.Count} 個檔案").ToList();
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = string.Join("\n", shown),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = canAddLayers
                        ? "「開啟」會把每個檔案各自開成一份文件；「加入圖層」會把影像放進目前的文件，插在作用中圖層上方。"
                        : "「開啟」會把每個檔案各自開成一份文件。",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SetBody(body, ButtonRow(
            Make("開啟", Choice.Open, primary: true),
            Make("加入圖層", Choice.AddLayers, enabled: canAddLayers),
            Make("取消", Choice.Cancel)));
    }

    /// <summary>Enter = 開啟。</summary>
    protected override void OnConfirmKey()
    {
        Result = Choice.Open;
        Close();
    }
}
