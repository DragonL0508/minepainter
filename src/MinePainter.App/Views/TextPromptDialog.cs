using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MinePainter.App.Views;

/// <summary>單一文字輸入的小對話框（預設集命名等）。</summary>
public sealed class TextPromptDialog : ModalDialog
{
    private readonly TextBox _box = new() { FontSize = 12 };

    public string Text => _box.Text?.Trim() ?? "";

    public TextPromptDialog(string title, string label, string initial = "") : base(title, 320)
    {
        _box.Text = initial;
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12 },
                _box,
            },
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            OnConfirmKey();
            e.Handled = true;
        };
        SetBody(body, ButtonRow(MakeButton("確定", primary: true, confirm: true), MakeButton("取消")));
        Opened += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    protected override bool Validate() => Text.Length > 0;
}
