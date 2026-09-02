using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MinePainter.App.Controls;

/// <summary>
/// 3×3 錨點選擇器（paint.net 的 Canvas Size 錨點）：自繪格子，
/// 選中的格子填強調色代表「原內容在這裡」，其餘格子畫箭頭指向畫布展開的方向。
/// </summary>
public sealed class AnchorPicker : Control
{
    private int _index = 4;
    private int _hover = -1;

    /// <summary>0..8，列優先（0 = 左上、4 = 中央、8 = 右下）。</summary>
    public int Index
    {
        get => _index;
        set
        {
            var clamped = Math.Clamp(value, 0, 8);
            if (clamped == _index) return;
            _index = clamped;
            InvalidateVisual();
            Changed?.Invoke(_index);
        }
    }

    public float AnchorX => (_index % 3) / 2f;
    public float AnchorY => (_index / 3) / 2f;

    public event Action<int>? Changed;

    public AnchorPicker()
    {
        Width = 108;
        Height = 108;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppTheme.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AppTheme.Changed -= InvalidateVisual;
    }

    private Rect CellRect(int i)
    {
        var cw = Bounds.Width / 3;
        var ch = Bounds.Height / 3;
        return new Rect((i % 3) * cw, (i / 3) * ch, cw, ch);
    }

    public override void Render(DrawingContext context)
    {
        var full = new Rect(Bounds.Size);
        context.FillRectangle(AppTheme.InnerBrush, full, 4);

        var gridPen = new Pen(AppTheme.SeparatorBrush, 1);
        for (var i = 1; i < 3; i++)
        {
            var x = Math.Round(full.Width * i / 3) + 0.5;
            var y = Math.Round(full.Height * i / 3) + 0.5;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, full.Height));
            context.DrawLine(gridPen, new Point(0, y), new Point(full.Width, y));
        }

        var sx = _index % 3;
        var sy = _index / 3;
        var arrowPen = new Pen(AppTheme.TextMutedBrush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        for (var i = 0; i < 9; i++)
        {
            var cell = CellRect(i).Deflate(3);
            if (i == _hover && i != _index)
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), cell, 3);

            if (i == _index)
            {
                context.FillRectangle(AppTheme.AccentBrush, cell, 3);
                // 內容示意：白色小方塊
                var inner = cell.Deflate(cell.Width * 0.3);
                context.FillRectangle(Brushes.White, inner, 2);
                continue;
            }

            // 箭頭：由選中格指向此格（只畫相鄰的，含斜向）
            var dx = i % 3 - sx;
            var dy = i / 3 - sy;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1) continue;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var ux = dx / len;
            var uy = dy / len;
            var c = cell.Center;
            var half = cell.Width * 0.24;
            var tail = new Point(c.X - ux * half, c.Y - uy * half);
            var head = new Point(c.X + ux * half, c.Y + uy * half);
            context.DrawLine(arrowPen, tail, head);
            // 箭頭兩翼
            var wing = half * 0.55;
            var left = new Point(head.X - (ux * 0.7071 - uy * 0.7071) * wing, head.Y - (uy * 0.7071 + ux * 0.7071) * wing);
            var right = new Point(head.X - (ux * 0.7071 + uy * 0.7071) * wing, head.Y - (uy * 0.7071 - ux * 0.7071) * wing);
            context.DrawLine(arrowPen, head, left);
            context.DrawLine(arrowPen, head, right);
        }
    }

    private int HitCell(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= Bounds.Width || p.Y >= Bounds.Height) return -1;
        var cx = (int)(p.X * 3 / Bounds.Width);
        var cy = (int)(p.Y * 3 / Bounds.Height);
        return Math.Clamp(cy, 0, 2) * 3 + Math.Clamp(cx, 0, 2);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitCell(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var hit = HitCell(e.GetPosition(this));
        if (hit >= 0) Index = hit;
        e.Handled = true;
    }
}
