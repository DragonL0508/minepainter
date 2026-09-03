using Avalonia.Controls;
using Avalonia.Media;

namespace MinePainter.App.Views;

/// <summary>
/// 預設集丟到「已經有效果堆疊」的圖層上：覆蓋（換掉整個堆疊）／疊加（接在現有堆疊之後）／取消。
/// 沒有堆疊的圖層不會問，直接套。
/// </summary>
public sealed class PresetApplyDialog : ModalDialog
{
    public enum Choice
    {
        Cancel,
        Replace,
        Append,
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    public PresetApplyDialog(string presetName, string layerName, IReadOnlyList<string> existingEffects) : base("套用預設集", 400)
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

        var shown = existingEffects.Count <= 5
            ? string.Join("、", existingEffects)
            : string.Join("、", existingEffects.Take(4)) + $"…共 {existingEffects.Count} 道";
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"圖層「{layerName}」已經有效果堆疊：{shown}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = $"要怎麼套用預設集「{presetName}」？「覆蓋」會換掉整個堆疊；「疊加」接在現有堆疊之後。兩種都可以復原。",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SetBody(body, ButtonRow(
            Make("覆蓋", Choice.Replace, primary: true),
            Make("疊加", Choice.Append),
            Make("取消", Choice.Cancel)));
    }

    /// <summary>Enter = 覆蓋。</summary>
    protected override void OnConfirmKey()
    {
        Result = Choice.Replace;
        Close();
    }
}
