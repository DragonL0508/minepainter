using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MinePainter.Core.Adjustments;

namespace MinePainter.App.Controls;

/// <summary>
/// 曲線編輯器（paint.net 的 Curves）：左下 (0,0) → 右上 (1,1)，
/// 點空白處新增控制點、拖曳移動、右鍵刪除；兩端點只能上下移。
/// 多通道時由外部切換 <see cref="ActiveChannel"/>，非作用通道淡色顯示。
/// </summary>
public sealed class CurveEditor : Control
{
    private static readonly Color[] ChannelColors =
    [
        Color.FromRgb(0xE0, 0x50, 0x50), Color.FromRgb(0x50, 0xC0, 0x50), Color.FromRgb(0x50, 0x90, 0xF0),
    ];

    private List<List<(float X, float Y)>> _curves = [[(0f, 0f), (1f, 1f)]];
    private int _active;
    private int _dragIndex = -1;

    public long[]? Histogram { get; set; }

    /// <summary>目前的控制點（每通道一組）。</summary>
    public IReadOnlyList<IReadOnlyList<(float X, float Y)>> Curves
    {
        get => _curves;
        set
        {
            _curves = value.Select(c => c.OrderBy(p => p.X).ToList()).ToList();
            if (_curves.Count == 0) _curves.Add([(0f, 0f), (1f, 1f)]);
            _active = Math.Clamp(_active, 0, _curves.Count - 1);
            InvalidateVisual();
        }
    }

    public int ActiveChannel
    {
        get => _active;
        set
        {
            _active = Math.Clamp(value, 0, _curves.Count - 1);
            InvalidateVisual();
        }
    }

    /// <summary>拖曳中即時觸發。</summary>
    public event Action? Changed;

    /// <summary>一次操作結束（放開／新增／刪除）。</summary>
    public event Action? Committed;

    public CurveEditor()
    {
        Width = 256;
        Height = 256;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private const double Pad = 6;

    private Rect Plot => new(Pad, Pad, Math.Max(1, Bounds.Width - Pad * 2), Math.Max(1, Bounds.Height - Pad * 2));

    private Point ToScreen((float X, float Y) p)
    {
        var r = Plot;
        return new Point(r.Left + p.X * r.Width, r.Bottom - p.Y * r.Height);
    }

    private (float X, float Y) ToCurve(Point p)
    {
        var r = Plot;
        return ((float)Math.Clamp((p.X - r.Left) / r.Width, 0, 1), (float)Math.Clamp((r.Bottom - p.Y) / r.Height, 0, 1));
    }

    public override void Render(DrawingContext context)
    {
        var full = new Rect(Bounds.Size);
        context.FillRectangle(AppTheme.InnerBrush, full, 3);
        var plot = Plot;

        HistogramView.DrawBars(context, Histogram, plot, new SolidColorBrush(Color.FromArgb(0x50, 0x9A, 0x9A, 0xA2)));

        var grid = new Pen(AppTheme.SeparatorBrush, 1);
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var y = plot.Top + plot.Height * i / 4;
            context.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        context.DrawLine(new Pen(AppTheme.SeparatorBrush, 1, dashStyle: DashStyle.Dash),
            new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Top));

        for (var c = 0; c < _curves.Count; c++)
        {
            if (c == _active) continue;
            DrawCurve(context, c, dim: true);
        }
        DrawCurve(context, _active, dim: false);
    }

    private void DrawCurve(DrawingContext context, int channel, bool dim)
    {
        var points = _curves[channel];
        var table = CurvesAdjustment.BuildTable(points);
        var color = _curves.Count == 3 ? ChannelColors[channel] : AppTheme.AccentBrush.Color;
        var brush = new SolidColorBrush(dim ? Color.FromArgb(0x60, color.R, color.G, color.B) : color);
        var pen = new Pen(brush, dim ? 1 : 2);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToScreen((0f, table[0] / 255f)), false);
            for (var i = 1; i < 256; i++)
                g.LineTo(ToScreen((i / 255f, table[i] / 255f)));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);

        if (dim) return;
        var fill = new SolidColorBrush(Colors.White);
        for (var i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i]);
            context.DrawEllipse(fill, pen, p, 4, 4);
        }
    }

    private int HitPoint(Point p)
    {
        var points = _curves[_active];
        var best = -1;
        var bestD = 8.0 * 8.0;
        for (var i = 0; i < points.Count; i++)
        {
            var s = ToScreen(points[i]);
            var d = (s - p).X * (s - p).X + (s - p).Y * (s - p).Y;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var points = _curves[_active];
        var hit = HitPoint(pos);

        if (props.IsRightButtonPressed)
        {
            // 右鍵刪除（端點不能刪）
            if (hit > 0 && hit < points.Count - 1)
            {
                points.RemoveAt(hit);
                InvalidateVisual();
                Changed?.Invoke();
                Committed?.Invoke();
            }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (hit < 0)
        {
            var cp = ToCurve(pos);
            // 插入到 X 排序位置；與既有點 X 太近就不加
            var insert = points.FindIndex(q => q.X > cp.X);
            if (insert < 0) insert = points.Count;
            var left = points[Math.Max(0, insert - 1)];
            var right = points[Math.Min(points.Count - 1, insert)];
            if (Math.Abs(left.X - cp.X) < 0.01f || Math.Abs(right.X - cp.X) < 0.01f)
            {
                e.Handled = true;
                return;
            }
            points.Insert(insert, cp);
            hit = insert;
        }

        _dragIndex = hit;
        e.Pointer.Capture(this);
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0) return;
        var points = _curves[_active];
        var cp = ToCurve(e.GetPosition(this));

        var isEnd = _dragIndex == 0 || _dragIndex == points.Count - 1;
        float x;
        if (isEnd)
        {
            x = points[_dragIndex].X;
        }
        else
        {
            var lo = points[_dragIndex - 1].X + 0.005f;
            var hi = points[_dragIndex + 1].X - 0.005f;
            x = Math.Clamp(cp.X, lo, hi);
        }
        points[_dragIndex] = (x, cp.Y);
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        e.Pointer.Capture(null);
        Committed?.Invoke();
        e.Handled = true;
    }

    /// <summary>重設作用通道為直線。</summary>
    public void ResetActive()
    {
        _curves[_active] = [(0f, 0f), (1f, 1f)];
        InvalidateVisual();
        Changed?.Invoke();
        Committed?.Invoke();
    }
}
