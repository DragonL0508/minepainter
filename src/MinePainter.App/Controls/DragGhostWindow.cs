using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace MinePainter.App.Controls;

/// <summary>
/// 拖曳中的「殘影」：一個跟著游標走的小透明視窗（縮圖＋名稱），滑鼠事件穿透，不搶焦點。
/// OS 拖放本身沒有拖曳影像，殘影用計時器讀游標位置、以指數插值追過去（有一點點延遲感的平滑），
/// 放開時淡出縮小。Windows 專用（讀游標與穿透都靠 user32）。
/// </summary>
public sealed class DragGhostWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly Border _root;
    private double _x, _y;
    private bool _placed;
    private bool _finishing;

    private const int OffsetX = 14;
    private const int OffsetY = 12;

    private DragGhostWindow(IImage? image, string name)
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.WidthAndHeight;

        var stack = new StackPanel { Spacing = 3 };
        if (image != null)
        {
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                Child = new Image { Source = image, Width = image.Size.Width, Height = image.Size.Height, Stretch = Stretch.None },
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = AppTheme.TextBrush,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        _root = new Border
        {
            Background = AppTheme.PanelBrush,
            BorderBrush = AppTheme.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6),
            Opacity = 0,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("scale(0.85)"),
            Child = stack,
        };
        Content = _root;
        _timer.Tick += (_, _) => Follow();
    }

    /// <summary>開始顯示殘影（跟著游標）。回傳的物件在拖放結束時呼叫 <see cref="Finish"/>。</summary>
    public static DragGhostWindow? Start(IImage? image, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var w = new DragGhostWindow(image, name);
            if (GetCursorPos(out var p))
            {
                w._x = p.X + OffsetX;
                w._y = p.Y + OffsetY;
                w.Position = new PixelPoint((int)w._x, (int)w._y);
                w._placed = true;
            }
            w.Show();
            w.MakeClickThrough();
            w._timer.Start();
            // 進場：淡入＋放大到 1
            Motion.EnsureFadeSlide(w._root, Motion.Quick, Motion.Enter);
            Dispatcher.UIThread.Post(() =>
            {
                w._root.Opacity = 0.92;
                w._root.RenderTransform = TransformOperations.Parse("scale(1)");
            }, DispatcherPriority.Render);
            return w;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>拖放結束：淡出縮小後關閉。</summary>
    public void Finish()
    {
        if (_finishing) return;
        _finishing = true;
        _timer.Stop();
        try
        {
            _root.Opacity = 0;
            _root.RenderTransform = TransformOperations.Parse("scale(0.7)");
        }
        catch (Exception)
        {
        }
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                Close();
            }
            catch (Exception)
            {
            }
        }, Motion.Quick + TimeSpan.FromMilliseconds(30));
    }

    private void Follow()
    {
        if (_finishing || !GetCursorPos(out var p)) return;
        var tx = p.X + OffsetX;
        var ty = p.Y + OffsetY;
        if (!_placed)
        {
            _x = tx;
            _y = ty;
            _placed = true;
        }
        else
        {
            // 每 8ms 追 35%：時間常數約 20ms，看得出一點跟隨感但不拖泥帶水
            _x += (tx - _x) * 0.35;
            _y += (ty - _y) * 0.35;
        }
        var next = new PixelPoint((int)Math.Round(_x), (int)Math.Round(_y));
        if (next != Position) Position = next;
    }

    // ---- Win32：游標位置與滑鼠穿透 ----

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;

    private void MakeClickThrough()
    {
        try
        {
            if (TryGetPlatformHandle() is not { } handle) return;
            var style = GetWindowLongPtr(handle.Handle, GwlExStyle).ToInt64();
            // 殘影固定偏在游標右下 14px，游標不會壓到它，不需要 WS_EX_LAYERED 那套穿透（會讓 DWM 合成的透明視窗畫不出來）
            style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            _ = WsExLayered;
            SetWindowLongPtr(handle.Handle, GwlExStyle, new IntPtr(style));
        }
        catch (Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
