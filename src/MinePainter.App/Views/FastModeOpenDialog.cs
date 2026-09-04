using Avalonia.Controls;
using Avalonia.Media;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>
/// 開啟快速模式專案時問一次：要繼續用代理畫布編輯，還是這次就以完整解析度開啟。
/// 檔案本身兩種都打得開 —— 快速模式只是「編輯時用多大的畫布」。
/// </summary>
public sealed class FastModeOpenDialog : ModalDialog
{
    public enum Choice
    {
        Fast,
        Full,
    }

    public Choice Result { get; private set; } = Choice.Fast;

    public FastModeOpenDialog(int proxyWidth, int proxyHeight, int outputWidth, int outputHeight)
        : base("快速模式專案", 420)
    {
        Button Make(string text, Choice choice, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Avalonia.Thickness(14, 6),
                FontSize = 12,
            };
            if (primary) b.Classes.Add("accent");
            b.Click += (_, _) =>
            {
                Result = choice;
                Close();
            };
            return b;
        }

        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"這份專案以 {proxyWidth} × {proxyHeight} 製作，輸出解析度是 {outputWidth} × {outputHeight}。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = "「快速模式」＝維持小畫布編輯（順很多），輸出時整份重算成完整解析度。\n" +
                           "「完整解析度」＝現在就把整份放大成輸出解析度來編輯；" +
                           "文字、形狀、效果會重畫，筆刷畫上去的像素則是放大取樣。",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SetBody(body, ButtonRow(
            Make("以快速模式開啟", Choice.Fast, primary: true),
            Make("以完整解析度開啟", Choice.Full)));
    }

    protected override void OnConfirmKey()
    {
        Result = Choice.Fast;
        Close();
    }
}
