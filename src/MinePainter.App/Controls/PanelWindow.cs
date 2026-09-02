using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MinePainter.App.Controls;

/// <summary>
/// 面板相對主視窗工作區的錨點（螢幕像素）：貼近左/右、上/下哪一組邊，以及與那條邊的距離。
/// 靠右的面板記的是「主視窗右緣到面板左緣」的距離，所以主視窗變寬變窄時面板整個跟著平移。
/// </summary>
public sealed record PanelAnchor(bool Right, bool Bottom, int OffsetX, int OffsetY);

/// <summary>
/// paint.net 式浮動面板：真正的 OS 子視窗（owned window），可拖出主視窗外。
/// ✕ 或 OS 關閉手勢一律轉為隱藏（由右上角開關重新顯示）。
/// </summary>
public sealed class PanelWindow : Window
{
    /// <summary>相對主視窗的錨點；主視窗移動／改變大小時照這個相對位置跟著走。</summary>
    public PanelAnchor? Anchor { get; set; }

    private bool _allowClose;
    private bool _closing;
    private bool _cancelHide;
    private readonly Border _root;

    /// <summary>使用者關閉（隱藏）面板時發出，供開關按鈕同步。</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// <paramref name="resizableHeight"/> 給了＝可拉大小的面板（圖層／歷史記錄這類「清單會長」的）：
    /// 視窗以固定尺寸起手、內容填滿，四邊四角可拖；沒給＝高度隨內容、不可拉（工具／調色盤）。
    /// </summary>
    public PanelWindow(string title, Control content, double width, double? resizableHeight = null)
    {
        Title = title;
        Width = width;
        var resizable = resizableHeight is { } h0;
        if (resizable)
        {
            Height = resizableHeight!.Value;
            SizeToContent = SizeToContent.Manual;
            MinWidth = Math.Min(width, 180);
            MinHeight = 160;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
        }
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var titleText = new TextBlock
        {
            Text = title,
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
        };
        closeButton.Click += (_, _) => HidePanel();

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
                    new Border { Padding = new Thickness(6), Child = content },
                },
            },
        };
        WindowAnimator.Prepare(_root);
        Content = resizable ? ResizeGrips.Wrap(this, _root) : _root;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _closing = false;
        WindowAnimator.PlayIn(_root);
    }

    /// <summary>播完退場動畫再隱藏（開關按鈕與 ✕ 都走這條）。</summary>
    public void HideAnimated() => HidePanel();

    /// <summary>
    /// 「這個面板應該要看得到」的唯一入口（開關打開、啟動、以及主視窗的定期自我修復都走這裡）。
    /// 退場動畫還在播 → 取消那次隱藏、重播進場；已隱藏 → Show；看得到但內容還是全透明
    /// （進場的 post 沒跑到、或某次動畫被打斷）→ 重播進場。冪等，可以放心重複呼叫。
    /// </summary>
    public void EnsureShown(Window owner)
    {
        if (IsClosed) return;
        if (_closing)
        {
            _cancelHide = true;
            return;
        }
        if (!IsVisible)
        {
            Show(owner);
            return;
        }
        if (_root.Opacity < 0.01) WindowAnimator.PlayIn(_root);
    }

    private void HidePanel()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        _cancelHide = false;
        LastPosition = Position;
        WindowAnimator.PlayOut(_root, () =>
        {
            _closing = false;
            if (_cancelHide)
            {
                // 動畫播到一半又被要求顯示（開關快速連點）：不能真的 Hide 掉，
                // 否則開關是亮的、面板卻不見了
                _cancelHide = false;
                WindowAnimator.PlayIn(_root);
                return;
            }
            Hide();
        });
        CloseRequested?.Invoke(); // 開關按鈕立刻同步，不等動畫
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        LastPosition = Position; // 主視窗若取消關閉，面板要能原位重建

        // 只有「使用者關掉這個面板」才轉成隱藏。
        // 主視窗/應用程式關閉時 Avalonia 會先來關子視窗，此時再 Cancel 會連帶中止
        // 整個關閉流程 —— 症狀就是第一次只收掉浮窗、要按第二次才關得掉 app。
        if (_allowClose || WindowAnimator.IsShuttingDown ||
            e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        e.Cancel = true;
        HidePanel();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        IsClosed = true;
    }

    /// <summary>已經真的關掉（不可再 Show，需重建）。</summary>
    public bool IsClosed { get; private set; }

    /// <summary>關閉前的位置，供重建時沿用。</summary>
    public PixelPoint LastPosition { get; private set; }

    /// <summary>應用程式關閉時呼叫，允許真正關閉。</summary>
    public void AllowClose()
    {
        if (IsClosed) return;
        _allowClose = true;
        Close();
    }
}
