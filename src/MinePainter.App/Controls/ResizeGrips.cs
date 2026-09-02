using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MinePainter.App.Controls;

/// <summary>
/// 無邊框浮動視窗的「拉大小」把手：四邊＋四角各一條透明的熱區，拖曳直接改視窗的
/// Width／Height（左／上邊同時搬 Position，讓對邊固定不動）。
///
/// 不走 OS 的 BeginResizeDrag —— 無邊框 + 透明底的視窗在 Windows 上沒有可靠的
/// 系統 resize 迴圈可用，自己算反而確定。把手疊在內容最外層（Grid 同格），
/// 熱區只有 6px、不擋內容的點擊（標題列的拖曳只會被最上面那 6px 讓給「拉高」）。
/// </summary>
public static class ResizeGrips
{
    private const double Thickness = 6;
    private const double Corner = 12;

    /// <summary>
    /// 把 <paramref name="content"/> 包成「內容 + 八個把手」的 Grid，供 Window.Content 使用。
    /// 視窗必須是 SizeToContent.Manual 且有明確的 Width／Height。
    /// </summary>
    public static Control Wrap(Window window, Control content)
    {
        var host = new Grid();
        host.Children.Add(content);

        Add(host, window, new Thickness(0, 0, 0, 0), HorizontalAlignment.Left, VerticalAlignment.Stretch,
            Thickness, double.NaN, StandardCursorType.LeftSide, left: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Right, VerticalAlignment.Stretch,
            Thickness, double.NaN, StandardCursorType.RightSide, right: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Stretch, VerticalAlignment.Top,
            double.NaN, Thickness, StandardCursorType.TopSide, top: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
            double.NaN, Thickness, StandardCursorType.BottomSide, bottom: true);

        Add(host, window, new Thickness(0), HorizontalAlignment.Left, VerticalAlignment.Top,
            Corner, Corner, StandardCursorType.TopLeftCorner, left: true, top: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Right, VerticalAlignment.Top,
            Corner, Corner, StandardCursorType.TopRightCorner, right: true, top: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Left, VerticalAlignment.Bottom,
            Corner, Corner, StandardCursorType.BottomLeftCorner, left: true, bottom: true);
        Add(host, window, new Thickness(0), HorizontalAlignment.Right, VerticalAlignment.Bottom,
            Corner, Corner, StandardCursorType.BottomRightCorner, right: true, bottom: true);
        return host;
    }

    private static void Add(Grid host, Window window, Thickness margin,
        HorizontalAlignment h, VerticalAlignment v, double width, double height,
        StandardCursorType cursor, bool left = false, bool right = false, bool top = false, bool bottom = false)
    {
        var grip = new Border
        {
            Background = Brushes.Transparent, // Transparent（非 null）才吃得到點擊
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Width = width,
            Height = height,
            Margin = margin,
            Cursor = new Cursor(cursor),
            ZIndex = 1000,
        };

        PixelPoint startPointer = default;
        PixelPoint startPos = default;
        Size startSize = default;
        var dragging = false;

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragging = true;
            startPointer = window.PointToScreen(e.GetPosition(window));
            startPos = window.Position;
            startSize = window.ClientSize;
            window.SizeToContent = SizeToContent.Manual;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var now = window.PointToScreen(e.GetPosition(window));
            var scale = window.RenderScaling;
            var dx = (now.X - startPointer.X) / scale;
            var dy = (now.Y - startPointer.Y) / scale;

            var w = startSize.Width;
            var hgt = startSize.Height;
            if (right) w = startSize.Width + dx;
            if (left) w = startSize.Width - dx;
            if (bottom) hgt = startSize.Height + dy;
            if (top) hgt = startSize.Height - dy;

            w = Math.Clamp(w, window.MinWidth, double.IsFinite(window.MaxWidth) ? window.MaxWidth : 10000);
            hgt = Math.Clamp(hgt, window.MinHeight, double.IsFinite(window.MaxHeight) ? window.MaxHeight : 10000);

            // 左／上邊：對邊固定，位置隨尺寸差搬移
            var x = left ? startPos.X + (int)Math.Round((startSize.Width - w) * scale) : startPos.X;
            var y = top ? startPos.Y + (int)Math.Round((startSize.Height - hgt) * scale) : startPos.Y;

            window.Width = w;
            window.Height = hgt;
            if (left || top) window.Position = new PixelPoint(x, y);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;

        host.Children.Add(grip);
    }
}
