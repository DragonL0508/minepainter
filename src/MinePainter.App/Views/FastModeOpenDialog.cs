using Avalonia.Controls;
using Avalonia.Media;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>
/// 開檔時問一次要用哪種解析度模式。兩個方向共用：
/// 　• 已經是快速模式的專案 → 繼續用代理畫布，還是這次以完整解析度開啟
/// 　• 一般的大專案／大圖 → 照常開，還是改用快速模式（畫布縮到 1080p、輸出仍是原尺寸）
/// 檔案本身兩種都打得開，模式只影響「編輯時用多大的畫布」。
/// </summary>
public sealed class FastModeOpenDialog : ModalDialog
{
    public enum Choice
    {
        Fast,
        Full,
    }

    public Choice Result { get; private set; }

    private FastModeOpenDialog(string title, Choice defaultChoice, string headline, string detail,
        (string Text, Choice Choice, bool Primary)[] buttons) : base(title, 430)
    {
        Result = defaultChoice;

        Button Make(string text, Choice choice, bool primary)
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
                new TextBlock { Text = headline, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = detail,
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SetBody(body, ButtonRow([.. buttons.Select(b => Make(b.Text, b.Choice, b.Primary))]));
        _default = defaultChoice;
    }

    private readonly Choice _default;

    /// <summary>已經是快速模式的專案。</summary>
    public static FastModeOpenDialog ForFastProject(int proxyWidth, int proxyHeight, int outWidth, int outHeight) =>
        new("快速模式專案", Choice.Fast,
            $"這份專案以 {proxyWidth} × {proxyHeight} 製作，輸出解析度是 {outWidth} × {outHeight}。",
            "「快速模式」＝維持小畫布編輯（順很多），輸出時整份重算成完整解析度。\n" +
            "「完整解析度」＝現在就把整份放大成輸出解析度來編輯；文字、形狀、效果會重畫，" +
            "筆刷畫上去的像素則是放大取樣。",
            [("以快速模式開啟", Choice.Fast, true), ("以完整解析度開啟", Choice.Full, false)]);

    /// <summary>一般的大專案／大圖：問要不要改用快速模式。</summary>
    public static FastModeOpenDialog ForLargeDocument(string what, int width, int height,
        int proxyWidth, int proxyHeight) =>
        new("要用快速模式嗎？", Choice.Full,
            $"{what}是 {width} × {height}。可以改用快速模式：畫布縮到 {proxyWidth} × {proxyHeight} 編輯，" +
            $"輸出時整份重算成 {width} × {height}。",
            "編輯會順很多（像素少了大半），文字、形狀、效果輸出時都以完整解析度重畫。\n" +
            "代價：這份工作檔裡的像素會變成代理解析度，輸出時只能放大取樣 —— " +
            "原始檔案不會被動到，但存檔時記得另存新檔，才不會蓋掉原本的高解析度像素。",
            [("一般開啟", Choice.Full, true), ("以快速模式開啟", Choice.Fast, false)]);

    protected override void OnConfirmKey()
    {
        Result = _default;
        Close();
    }
}
