using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>
/// 模態對話框的共用外框：與 <see cref="LayerPropertiesWindow"/> 一致的自繪標題列與
/// 深色圓角邊框，進退場走 WindowAnimator。Esc＝取消、Enter＝確定（子類可覆寫）。
/// 用法：子類建構時組好內容後呼叫 <see cref="SetBody"/>，按鈕用 <see cref="MakeButton"/> 建。
/// </summary>
public abstract class ModalDialog : Window
{
    private Border _root = null!;
    private bool _closing;

    /// <summary>使用者是否按了確定（Esc／✕／取消都維持 false）。</summary>
    public bool Confirmed { get; protected set; }

    protected ModalDialog(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>由子類在內容組好後呼叫一次；footer 通常是 ButtonRow。</summary>
    protected void SetBody(Control body, Control footer, bool showClose = true)
    {
        var titleText = new TextBlock
        {
            Text = Title,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var closeButton = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 24,
            Height = 20,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            // 標題列是「可拖曳」的 SizeAll 游標，會被子元素繼承；✕ 上面要蓋回一般游標，
            // 不然滑上去看起來還是在拖視窗，不像按得下去的鈕
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        closeButton.Click += (_, _) => Close();
        closeButton.IsVisible = showClose;

        DockPanel.SetDock(closeButton, Dock.Right);
        var header = new Border
        {
            Background = AppTheme.HeaderBrush,
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Height = 26,
            Child = new DockPanel { Children = { closeButton, titleText } },
        };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        header.Cursor = new Cursor(StandardCursorType.SizeAll);
        DockPanel.SetDock(header, Dock.Top);

        var stack = new StackPanel
        {
            Spacing = 12,
            Children = { body, footer },
        };

        _root = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new DockPanel
            {
                Children =
                {
                    header,
                    new Border { Padding = new Thickness(14, 12), Child = stack },
                },
            },
        };
        WindowAnimator.Prepare(_root);
        Content = _root;
    }

    protected Button MakeButton(string text, bool primary = false, bool confirm = false)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6),
            FontSize = 12,
        };
        if (primary) b.Classes.Add("accent");
        b.Click += (_, _) =>
        {
            if (confirm && !Validate()) return;
            Confirmed = confirm;
            Close();
        };
        return b;
    }

    protected static Control ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>按下確定前的檢查點；回傳 false 會留在對話框。</summary>
    protected virtual bool Validate() => true;

    /// <summary>Enter 的預設行為：等同按下確定。子類的輸入框若自己吃 Enter 不會走到這。</summary>
    protected virtual void OnConfirmKey()
    {
        if (!Validate()) return;
        Confirmed = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnConfirmKey();
            e.Handled = true;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowAnimator.PlayIn(_root);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // 同 LayerPropertiesWindow：只在使用者關窗時播退場動畫；
        // 主視窗/應用程式關閉時直接放行，否則會中止整個關閉流程。
        if (_closing || WindowAnimator.IsShuttingDown ||
            e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        WindowAnimator.PlayOut(_root, Close);
    }
}
