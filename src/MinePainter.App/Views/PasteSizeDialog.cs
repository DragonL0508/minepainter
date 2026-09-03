using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 貼上的影像大於畫布時的三選一（paint.net 的同名對話框）：
/// 延展畫布（預設、Enter）／維持畫布大小（超出部分先看不到，可再移動）／取消。
/// </summary>
public sealed class PasteSizeDialog : ModalDialog
{
    public enum Choice
    {
        Cancel,
        KeepCanvas,
        ExpandCanvas,
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    public PasteSizeDialog(SKSizeI image, SKSizeI canvas) : base("貼上的影像大於畫布", 420)
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
                    Text = $"貼上的影像（{image.Width} × {image.Height}）超出目前的畫布（{canvas.Width} × {canvas.Height}）。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SetBody(body, ButtonRow(
            Make("延展畫布大小", Choice.ExpandCanvas, primary: true),
            Make("維持畫布大小", Choice.KeepCanvas),
            Make("取消", Choice.Cancel)));
    }

    /// <summary>Enter = 預設鍵（延展畫布），與 paint.net 一致。</summary>
    protected override void OnConfirmKey()
    {
        Result = Choice.ExpandCanvas;
        Close();
    }
}
