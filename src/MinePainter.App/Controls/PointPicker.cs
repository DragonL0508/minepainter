using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MinePainter.Core.Effects;

namespace MinePainter.App.Controls;

/// <summary>
/// 中心點選點器（paint.net 的 pan control）：在來源縮圖上拖曳十字，
/// 值為正規化座標（-1..1，0 = 中心）。雙擊回到中心。
/// 有 <see cref="Guide"/> 時另外畫出效果的範圍圈（實線＝不受影響的範圍、虛線＝效果到底的位置），
/// 圈本身可以拖曳（改的是效果的半徑／過渡），使用者不用盯著滑桿數字猜範圍
/// （使用者 2026-09-06：「參數的調整要可以可視化」）。
/// </summary>
public sealed class PointPicker : Control
{
    private enum DragMode { None, Center, Inner, Outer }

    /// <summary>圈邊多少像素內算抓到圈（十字附近永遠是搬中心）。</summary>
    private const double RingGrab = 7;

    private (float X, float Y) _value;
    private PointGuide? _guide;
    private DragMode _drag;

    /// <summary>效果的範圍圈（可省）；改了會重畫。</summary>
    public PointGuide? Guide
    {
        get => _guide;
        set
        {
            _guide = value;
            InvalidateVisual();
        }
    }

    /// <summary>圈能不能拖（效果有沒有提供 WithGuide）。</summary>
    public bool GuideDraggable { get; set; }

    /// <summary>使用者拖圈改了範圍（拖曳中連續發；放開發 <see cref="DragCompleted"/>）。</summary>
    public event Action<PointGuide>? GuideChanged;

    /// <summary>底圖（來源範圍縮圖，可省）。</summary>
    public Bitmap? Thumbnail { get; set; }

    /// <summary>底圖長寬比（沒有縮圖時決定框的形狀）。</summary>
    public double Aspect { get; set; } = 16.0 / 9.0;

    public (float X, float Y) Value
    {
        get => _value;
        set
        {
            _value = (Math.Clamp(value.X, -1f, 1f), Math.Clamp(value.Y, -1f, 1f));
            InvalidateVisual();
        }
    }

    public event Action<(float X, float Y)>? ValueChanged;
    public event Action<(float X, float Y)>? DragCompleted;

    public PointPicker()
    {
        Height = 120;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private Rect Frame
    {
        get
        {
            var aspect = Thumbnail is { } t && t.PixelSize.Height > 0
                ? t.PixelSize.Width / (double)t.PixelSize.Height
                : Aspect;
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 0 || h <= 0) return new Rect(0, 0, 1, 1);
            var fw = Math.Min(w, h * aspect);
            var fh = fw / aspect;
            return new Rect((w - fw) / 2, (h - fh) / 2, fw, fh);
        }
    }

    public override void Render(DrawingContext context)
    {
        var frame = Frame;
        context.FillRectangle(AppTheme.InnerBrush, new Rect(Bounds.Size), 3);
        if (Thumbnail != null)
            context.DrawImage(Thumbnail, new Rect(Thumbnail.Size), frame);
        else
            context.FillRectangle(AppTheme.BarTrackBrush, frame);
        context.DrawRectangle(new Pen(AppTheme.SeparatorBrush, 1), frame);

        var px = frame.Left + (_value.X + 1) / 2 * frame.Width;
        var py = frame.Top + (_value.Y + 1) / 2 * frame.Height;

        // 十字（白底黑線，在任何底圖上都看得見）
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 3);
        var line = new Pen(AppTheme.AccentBrush, 1.5);
        foreach (var pen in new[] { halo, line })
        {
            context.DrawLine(pen, new Point(px - 10, py), new Point(px + 10, py));
            context.DrawLine(pen, new Point(px, py - 10), new Point(px, py + 10));
        }
        context.DrawEllipse(null, halo, new Point(px, py), 5, 5);
        context.DrawEllipse(null, line, new Point(px, py), 5, 5);

        if (_guide is { } g) DrawGuide(context, frame, g, new Point(px, py));
    }

    private static void DrawGuide(DrawingContext context, Rect frame, PointGuide g, Point c)
    {
        using var clip = context.PushClip(frame);
        var (rxI, ryI) = RingRadii(frame, g, g.Inner);
        var (rxO, ryO) = RingRadii(frame, g, g.Outer);

        // 受影響的那側淡淡塗暗：一眼看出哪邊會變（反轉時在圈內、平常在圈外）
        var shade = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        if (g.Invert)
        {
            context.DrawEllipse(shade, null, c, rxO, ryO);
        }
        else
        {
            var outside = new PathGeometry { FillRule = FillRule.EvenOdd };
            outside.Figures!.Add(RectFigure(frame));
            outside.Figures.Add(EllipseFigure(c, rxO, ryO));
            context.DrawGeometry(shade, null, outside);
        }

        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 3);
        var inner = new Pen(AppTheme.AccentBrush, 1.5);
        var outer = new Pen(AppTheme.AccentBrush, 1.2, dashStyle: new DashStyle([4, 3], 0));
        if (rxI > 0.5)
        {
            context.DrawEllipse(null, halo, c, rxI, ryI);
            context.DrawEllipse(null, inner, c, rxI, ryI);
        }
        if (rxO - rxI > 0.5)
        {
            context.DrawEllipse(null, halo, c, rxO, ryO);
            context.DrawEllipse(null, outer, c, rxO, ryO);
        }
    }

    private static PathFigure RectFigure(Rect r)
    {
        var f = new PathFigure { StartPoint = r.TopLeft, IsClosed = true };
        f.Segments!.Add(new LineSegment { Point = r.TopRight });
        f.Segments.Add(new LineSegment { Point = r.BottomRight });
        f.Segments.Add(new LineSegment { Point = r.BottomLeft });
        return f;
    }

    private static PathFigure EllipseFigure(Point c, double rx, double ry)
    {
        var f = new PathFigure { StartPoint = new Point(c.X + rx, c.Y), IsClosed = true };
        f.Segments!.Add(new ArcSegment { Point = new Point(c.X - rx, c.Y), Size = new Size(rx, ry), SweepDirection = SweepDirection.Clockwise });
        f.Segments.Add(new ArcSegment { Point = new Point(c.X + rx, c.Y), Size = new Size(rx, ry), SweepDirection = SweepDirection.Clockwise });
        return f;
    }

    /// <summary>倍率（半對角線的幾倍）→ 縮圖上的橢圓半徑；橢圓模式 y 方向依框的長寬比縮（與效果的距離公式一致）。</summary>
    private static (double Rx, double Ry) RingRadii(Rect frame, PointGuide g, float scale)
    {
        var halfDiag = Math.Sqrt(frame.Width * frame.Width + frame.Height * frame.Height) / 2;
        var r = Math.Max(0, scale) * halfDiag;
        return g.Elliptical ? (r, r * frame.Height / frame.Width) : (r, r);
    }

    /// <summary>縮圖上的一點離中心多遠，換算回倍率。</summary>
    private double ScaleAt(Rect frame, PointGuide g, Point p)
    {
        var cx = frame.Left + (_value.X + 1) / 2 * frame.Width;
        var cy = frame.Top + (_value.Y + 1) / 2 * frame.Height;
        var dx = p.X - cx;
        var dy = (p.Y - cy) * (g.Elliptical ? frame.Width / frame.Height : 1);
        var halfDiag = Math.Sqrt(frame.Width * frame.Width + frame.Height * frame.Height) / 2;
        return Math.Sqrt(dx * dx + dy * dy) / halfDiag;
    }

    private DragMode HitTest(Point p)
    {
        if (_guide is not { } g || !GuideDraggable) return DragMode.Center;
        var frame = Frame;
        var halfDiag = Math.Sqrt(frame.Width * frame.Width + frame.Height * frame.Height) / 2;
        var n = ScaleAt(frame, g, p);
        if (n * halfDiag < 12) return DragMode.Center; // 十字附近永遠是搬中心
        var dInner = Math.Abs(n - g.Inner) * halfDiag;
        var dOuter = Math.Abs(n - g.Outer) * halfDiag;
        if (dInner <= RingGrab && dInner <= dOuter) return DragMode.Inner;
        if (dOuter <= RingGrab) return DragMode.Outer;
        return DragMode.Center;
    }

    private void ApplyRing(Point p, bool notify)
    {
        if (_guide is not { } g) return;
        var n = (float)Math.Clamp(ScaleAt(Frame, g, p), 0, 4);
        // 內圈不能超過外圈、外圈不能小於內圈：推著另一圈走
        var next = _drag == DragMode.Inner
            ? g with { Inner = n, Outer = Math.Max(n, g.Outer) }
            : g with { Outer = n, Inner = Math.Min(n, g.Inner) };
        if (next == g) return;
        _guide = next;
        InvalidateVisual();
        if (notify) GuideChanged?.Invoke(next);
    }

    private void Apply(Point p, bool notify)
    {
        var frame = Frame;
        var x = (float)Math.Clamp((p.X - frame.Left) / frame.Width * 2 - 1, -1, 1);
        var y = (float)Math.Clamp((p.Y - frame.Top) / frame.Height * 2 - 1, -1, 1);
        if (Math.Abs(x - _value.X) < 1e-4 && Math.Abs(y - _value.Y) < 1e-4) return;
        _value = (x, y);
        InvalidateVisual();
        if (notify) ValueChanged?.Invoke(_value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            _value = (0f, 0f);
            InvalidateVisual();
            ValueChanged?.Invoke(_value);
            DragCompleted?.Invoke(_value);
            e.Handled = true;
            return;
        }
        var pos = e.GetPosition(this);
        _drag = HitTest(pos);
        e.Pointer.Capture(this);
        if (_drag == DragMode.Center) Apply(pos, notify: true);
        else ApplyRing(pos, notify: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragMode.None)
        {
            // 游標提示：圈上是移動箭頭，其他地方是十字
            var onRing = HitTest(e.GetPosition(this)) != DragMode.Center;
            Cursor = new Cursor(onRing ? StandardCursorType.SizeAll : StandardCursorType.Cross);
            return;
        }
        if (_drag == DragMode.Center) Apply(e.GetPosition(this), notify: true);
        else ApplyRing(e.GetPosition(this), notify: true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke(_value);
        e.Handled = true;
    }
}
