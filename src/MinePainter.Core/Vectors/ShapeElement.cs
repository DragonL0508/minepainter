using SkiaSharp;

namespace MinePainter.Core.Vectors;
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
}

public sealed record ShapeElement : VectorElement
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;

    /// <summary>形狀外框；Line 以 (Left,Top)→(Right,Bottom) 為端點（可為「負尺寸」表達方向）。</summary>
    public SKRect Rect { get; init; }

    public SKColor? FillColor { get; init; }
    public SKColor StrokeColor { get; init; } = SKColors.Black;
    public float StrokeWidth { get; init; } = 4f;

    public override SKRectI Bounds
    {
        get
        {
            var r = SKRect.Create(
                Math.Min(Rect.Left, Rect.Right), Math.Min(Rect.Top, Rect.Bottom),
                Math.Abs(Rect.Width), Math.Abs(Rect.Height));
            var pad = StrokeWidth / 2 + 2;
            r.Inflate(pad, pad);
            return SKRectI.Ceiling(r);
        }
    }

    public override void Render(SKCanvas canvas)
    {
        var r = SKRect.Create(
            Math.Min(Rect.Left, Rect.Right), Math.Min(Rect.Top, Rect.Bottom),
            Math.Abs(Rect.Width), Math.Abs(Rect.Height));

        if (Kind == ShapeKind.Line)
        {
            using var stroke = new SKPaint
            {
                Color = StrokeColor,
                StrokeWidth = StrokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.DrawLine(Rect.Left, Rect.Top, Rect.Right, Rect.Bottom, stroke);
            return;
        }

        if (FillColor is { } fill)
        {
            using var fillPaint = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
            if (Kind == ShapeKind.Rectangle) canvas.DrawRect(r, fillPaint);
            else canvas.DrawOval(r, fillPaint);
        }

        if (StrokeWidth > 0)
        {
            using var strokePaint = new SKPaint
            {
                Color = StrokeColor,
                StrokeWidth = StrokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
            };
            if (Kind == ShapeKind.Rectangle) canvas.DrawRect(r, strokePaint);
            else canvas.DrawOval(r, strokePaint);
        }
    }

    public override VectorElement Translated(float dx, float dy) =>
        this with { Rect = new SKRect(Rect.Left + dx, Rect.Top + dy, Rect.Right + dx, Rect.Bottom + dy) };

    /// <summary>形狀不支援旋轉參數：取矩陣映射後的外接矩形（近似）。</summary>
    public override VectorElement TransformedBy(SKMatrix matrix, float sx, float sy, float rotationDeg) =>
        this with
        {
            Rect = matrix.MapRect(Rect),
            StrokeWidth = Math.Max(0f, StrokeWidth * (Math.Abs(sx) + Math.Abs(sy)) / 2),
        };
}
