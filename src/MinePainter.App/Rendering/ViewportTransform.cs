using Avalonia;

namespace MinePainter.App.Rendering;

/// <summary>
/// 文件座標 ↔ 檢視座標的變換（縮放 + 平移）。
/// pan/zoom 只改這個結構，永遠不觸發文件重新合成。
/// </summary>
public readonly record struct ViewportTransform(double Scale, double OffsetX, double OffsetY)
{
    public const double MinScale = 1.0 / 64;
    public const double MaxScale = 64.0;

    public static ViewportTransform Identity => new(1, 0, 0);

    public Point DocToView(Point doc) => new(doc.X * Scale + OffsetX, doc.Y * Scale + OffsetY);

    public Point ViewToDoc(Point view) => new((view.X - OffsetX) / Scale, (view.Y - OffsetY) / Scale);

    /// <summary>以檢視座標 anchor 為中心縮放（游標下的文件點保持不動）。</summary>
    public ViewportTransform ZoomAt(Point anchor, double factor)
    {
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        var actual = newScale / Scale;
        return new ViewportTransform(
            newScale,
            anchor.X - (anchor.X - OffsetX) * actual,
            anchor.Y - (anchor.Y - OffsetY) * actual);
    }

    public ViewportTransform PanBy(double dx, double dy) => new(Scale, OffsetX + dx, OffsetY + dy);

    /// <summary>讓整份文件置中並縮放到塞進 viewport（留 margin 邊距）。</summary>
    public static ViewportTransform Fit(double docWidth, double docHeight, double viewWidth, double viewHeight, double margin = 24)
    {
        var usableW = Math.Max(1, viewWidth - margin * 2);
        var usableH = Math.Max(1, viewHeight - margin * 2);
        var scale = Math.Clamp(Math.Min(usableW / docWidth, usableH / docHeight), MinScale, MaxScale);
        return new ViewportTransform(
            scale,
            (viewWidth - docWidth * scale) / 2,
            (viewHeight - docHeight * scale) / 2);
    }

    /// <summary>切到指定倍率，維持 viewport 中心對準的文件點不變。</summary>
    public ViewportTransform WithScaleAroundCenter(double newScale, double viewWidth, double viewHeight)
    {
        var anchor = new Point(viewWidth / 2, viewHeight / 2);
        var clamped = Math.Clamp(newScale, MinScale, MaxScale);
        return ZoomAt(anchor, clamped / Scale);
    }
}
