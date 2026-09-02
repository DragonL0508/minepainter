using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MinePainter.App.Controls;

/// <summary>256 格直方圖（色階／曲線對話框用）。以平方根尺度畫，暗部細節不會被高峰壓扁。</summary>
public sealed class HistogramView : Control
{
    private long[]? _data;

    public long[]? Data
    {
        get => _data;
        set
        {
            _data = value;
            InvalidateVisual();
        }
    }

    public HistogramView()
    {
        Height = 64;
        MinWidth = 200;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(AppTheme.InnerBrush, rect, 3);
        DrawBars(context, _data, rect, AppTheme.TextMutedBrush);
    }

    /// <summary>把直方圖畫進指定矩形（曲線編輯器背景也用這個）。</summary>
    public static void DrawBars(DrawingContext context, long[]? data, Rect rect, IBrush brush)
    {
        if (data == null || data.Length == 0 || rect.Width <= 0 || rect.Height <= 0) return;
        double max = 0;
        foreach (var v in data) max = Math.Max(max, Math.Sqrt(v));
        if (max <= 0) return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left, rect.Bottom), true);
            for (var i = 0; i < data.Length; i++)
            {
                var x = rect.Left + rect.Width * i / (data.Length - 1);
                var h = rect.Height * (Math.Sqrt(data[i]) / max);
                g.LineTo(new Point(x, rect.Bottom - h));
            }
            g.LineTo(new Point(rect.Right, rect.Bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }
}
