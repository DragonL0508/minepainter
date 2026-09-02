using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MinePainter.App.Controls;

/// <summary>
/// 中心點選點器（paint.net 的 pan control）：在來源縮圖上拖曳十字，
/// 值為正規化座標（-1..1，0 = 中心）。雙擊回到中心。
/// </summary>
public sealed class PointPicker : Control
{
    private (float X, float Y) _value;
    private bool _dragging;

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
        _dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this), notify: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        Apply(e.GetPosition(this), notify: true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke(_value);
        e.Handled = true;
    }
}
