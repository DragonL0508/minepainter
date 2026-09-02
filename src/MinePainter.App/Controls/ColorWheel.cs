using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MinePainter.App.Controls;

/// <summary>
/// paint.net 式色輪：角度 = 色相、半徑 = 飽和度（明度由外部滑桿控制）。
/// </summary>
public sealed class ColorWheel : Control
{
    private const int BitmapSize = 176;
    private WriteableBitmap? _wheel;

    private double _hue;        // 0..360
    private double _saturation; // 0..1
    private bool _dragging;

    public double Hue
    {
        get => _hue;
        set { _hue = Math.Clamp(value, 0, 360); InvalidateVisual(); }
    }

    public double Saturation
    {
        get => _saturation;
        set { _saturation = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public event Action? HueSatChanged;

    public ColorWheel()
    {
        Width = BitmapSize;
        Height = BitmapSize;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public override void Render(DrawingContext context)
    {
        _wheel ??= BuildWheel();
        context.DrawImage(_wheel, new Rect(0, 0, BitmapSize, BitmapSize));

        // 目前 H/S 的標記點
        var radius = BitmapSize / 2.0 - 2;
        var rad = _hue * Math.PI / 180.0;
        var cx = BitmapSize / 2.0 + Math.Cos(rad) * _saturation * radius;
        var cy = BitmapSize / 2.0 + Math.Sin(rad) * _saturation * radius;
        context.DrawEllipse(null, new Pen(Brushes.Black, 2), new Point(cx, cy), 5, 5);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.2), new Point(cx, cy), 5, 5);
    }

    private static unsafe WriteableBitmap BuildWheel()
    {
        var bmp = new WriteableBitmap(new PixelSize(BitmapSize, BitmapSize), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var ptr = (uint*)fb.Address;
        var center = BitmapSize / 2.0;
        var radius = center - 2;

        for (var y = 0; y < BitmapSize; y++)
        {
            var row = ptr + y * fb.RowBytes / 4;
            for (var x = 0; x < BitmapSize; x++)
            {
                var dx = x + 0.5 - center;
                var dy = y + 0.5 - center;
                var r = Math.Sqrt(dx * dx + dy * dy);
                if (r > radius + 1)
                {
                    row[x] = 0;
                    continue;
                }

                var hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (hue < 0) hue += 360;
                var sat = Math.Min(1.0, r / radius);
                var (rr, gg, bb) = HsvToRgb(hue, sat, 1.0);

                // 邊緣 1px 抗鋸齒
                var alpha = r <= radius ? 1.0 : 1.0 - (r - radius);
                var a = (byte)(alpha * 255);
                row[x] = ((uint)a << 24) |
                         ((uint)(rr * alpha) << 16) |
                         ((uint)(gg * alpha) << 8) |
                         (uint)(bb * alpha);
            }
        }
        return bmp;
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (r, g, b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Apply(Point p)
    {
        var center = BitmapSize / 2.0;
        var radius = center - 2;
        var dx = p.X - center;
        var dy = p.Y - center;

        var hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (hue < 0) hue += 360;
        _hue = hue;
        _saturation = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1);

        InvalidateVisual();
        HueSatChanged?.Invoke();
    }
}
