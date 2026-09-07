using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>游標選擇與畫布繪製共用的游標幾何。</summary>
internal static class CanvasCursor
{
    /// <summary>圈小於這個螢幕半徑就看不出是圈了，改回十字游標。</summary>
    private const double MinBrushCursorRadius = 3.5;

    /// <summary>
    /// 把手的命中半徑（螢幕像素）。把手畫出來是 8px 見方，命中範圍給得比它大一圈才好抓 ——
    /// 拖角是高頻操作，抓不到的代價（不小心平移整層、或重畫一個選取範圍）比誤抓大。
    /// </summary>
    internal const double HandleHitRadius = 13;

    // 黑白交錯的虛線：任何底色上都看得見（純白圈在亮圖上、純黑圈在暗圖上都會消失）
    private static readonly Pen BrushCursorPenDark =
        new(Brushes.Black, 1, new DashStyle([4, 4], 0));
    private static readonly Pen BrushCursorPenLight =
        new(Brushes.White, 1, new DashStyle([4, 4], 4));

    // 中心點細十字：圈大的時候光看圈抓不準筆刷會落在哪。白線＋深色外框，任何底色上都看得見
    private static readonly Pen BrushCenterHaloPen =
        new(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 2.5);
    private static readonly Pen BrushCenterPen = new(Brushes.White, 1);

    /// <summary>十字臂長（螢幕像素）；圈太小時再縮短，不要戳出圈外。</summary>
    private const double BrushCenterArm = 4;

    internal static StandardCursorType ForHandle(int handle) => handle switch
    {
        0 => StandardCursorType.TopLeftCorner,
        1 => StandardCursorType.TopRightCorner,
        2 => StandardCursorType.BottomRightCorner,
        3 => StandardCursorType.BottomLeftCorner,
        4 or 6 => StandardCursorType.SizeNorthSouth,
        5 or 7 => StandardCursorType.SizeWestEast,
        _ => StandardCursorType.SizeAll,
    };

    /// <summary>目前該畫的圈的螢幕半徑；不該畫時回 null。</summary>
    internal static double? BrushRadius(EditorSession? session, double scale)
    {
        if (session?.ActiveTool is not IBrushCursorTool tool) return null;
        var radius = tool.CursorRadius * scale;
        return radius >= MinBrushCursorRadius ? radius : null;
    }

    internal static void DrawBrush(DrawingContext context, Point position, double radius)
    {
        context.DrawEllipse(null, BrushCursorPenDark, position, radius, radius);
        context.DrawEllipse(null, BrushCursorPenLight, position, radius, radius);

        var arm = Math.Min(BrushCenterArm, radius - 1);
        if (arm < 2) return; // 圈已經很小，再畫十字只會糊成一團
        var c = position;
        var h1 = new Point(c.X - arm, c.Y);
        var h2 = new Point(c.X + arm, c.Y);
        var v1 = new Point(c.X, c.Y - arm);
        var v2 = new Point(c.X, c.Y + arm);
        context.DrawLine(BrushCenterHaloPen, h1, h2);
        context.DrawLine(BrushCenterHaloPen, v1, v2);
        context.DrawLine(BrushCenterPen, h1, h2);
        context.DrawLine(BrushCenterPen, v1, v2);
    }

}
