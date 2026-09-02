using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
using GradientStops = MinePainter.Core.Effects.GradientStops;
using GradientStop = MinePainter.Core.Effects.GradientStop;

namespace MinePainter.App.Controls;

/// <summary>
/// 多節點漸層編輯器（paint.net／PS 式）：上方一條漸層預覽，下方每個節點一個「小房子」標記。
/// • 拖標記＝移動節點　• 點預覽條空白處＝在該處新增節點（顏色取當處漸層色）
/// • 點標記＝選取（顏色／位置由外面的欄位改）　• 右鍵標記或把標記往下拖出去＝刪除（至少留兩個）
/// • 雙擊標記＝<see cref="StopActivated"/>（外面開選色）。
/// 拖曳中發 <see cref="Changed"/>，放開發 <see cref="Committed"/>。
/// </summary>
public sealed class GradientEditor : Control
{
    private const double BarHeight = 22;
    private const double MarkerHeight = 12;
    private const double MarkerHalfWidth = 6;
    private const double DetachDistance = 28; // 往下拖超過這距離放開＝刪除

    private GradientStops _stops = GradientStops.Two(SKColors.Black, SKColors.White);
    private int _selected;
    private int _dragging = -1;
    private bool _detaching;
    private GradientStops? _dragStart;

    private static readonly IPen BorderPen = new Pen(AppTheme.SeparatorBrush);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x9D, 0xF4)), 2);
    private static readonly IPen MarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x58)));
    private static readonly IBrush CheckerLight = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly IBrush CheckerDark = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public GradientStops Stops
    {
        get => _stops;
        set
        {
            _stops = value;
            _selected = Math.Clamp(_selected, 0, _stops.Count - 1);
            InvalidateVisual();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, _stops.Count - 1);
            if (v == _selected) return;
            _selected = v;
            SelectionChanged?.Invoke(v);
            InvalidateVisual();
        }
    }

    public GradientStop SelectedStop => _stops[_selected];

    public event Action<GradientStops>? Changed;
    public event Action<GradientStops>? Committed;
    public event Action<int>? SelectionChanged;
    /// <summary>雙擊節點（外面開選色面板）。</summary>
    public event Action<int>? StopActivated;

    public GradientEditor()
    {
        Height = BarHeight + MarkerHeight + 4;
        MinWidth = 120;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private Rect BarRect => new(MarkerHalfWidth, 0, Math.Max(1, Bounds.Width - MarkerHalfWidth * 2), BarHeight);

    private double XOf(float position) => BarRect.X + position * BarRect.Width;
    private float PositionOf(double x) => (float)Math.Clamp((x - BarRect.X) / BarRect.Width, 0, 1);

    public override void Render(DrawingContext context)
    {
        var bar = BarRect;

        // 棋盤格（看得出半透明節點）
        using (context.PushClip(new RoundedRect(bar, 3)))
        {
            const double cell = 6;
            for (var y = 0.0; y < bar.Height; y += cell)
            for (var x = 0.0; x < bar.Width; x += cell)
            {
                var dark = ((int)(x / cell) + (int)(y / cell)) % 2 == 1;
                context.FillRectangle(dark ? CheckerDark : CheckerLight, new Rect(bar.X + x, bar.Y + y, cell, cell));
            }
            context.FillRectangle(BuildBrush(), bar);
        }
        context.DrawRectangle(null, BorderPen, new RoundedRect(bar, 3));

        // 標記：小房子（上尖下方），填節點色
        for (var i = 0; i < _stops.Count; i++)
        {
            var s = _stops[i];
            var x = XOf(s.Position);
            var top = bar.Bottom + 1;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x, top), true);
                g.LineTo(new Point(x + MarkerHalfWidth, top + 5));
                g.LineTo(new Point(x + MarkerHalfWidth, top + MarkerHeight));
                g.LineTo(new Point(x - MarkerHalfWidth, top + MarkerHeight));
                g.LineTo(new Point(x - MarkerHalfWidth, top + 5));
                g.EndFigure(true);
            }
            var fill = new SolidColorBrush(Color.FromArgb(255, s.Color.Red, s.Color.Green, s.Color.Blue));
            var faded = _dragging == i && _detaching;
            context.DrawGeometry(fill, i == _selected ? SelectedPen : MarkerPen, geo);
            if (faded) context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xFF, 0xFF)),
                new Rect(x - MarkerHalfWidth, top, MarkerHalfWidth * 2, MarkerHeight));
        }
    }

    private LinearGradientBrush BuildBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        foreach (var s in _stops.Stops)
            brush.GradientStops.Add(new Avalonia.Media.GradientStop(Color.FromArgb(s.Color.Alpha, s.Color.Red, s.Color.Green, s.Color.Blue), s.Position));
        return brush;
    }

    private int HitMarker(Point p)
    {
        var bar = BarRect;
        if (p.Y < bar.Bottom - 2 || p.Y > bar.Bottom + MarkerHeight + 6) return -1;
        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _stops.Count; i++)
        {
            var d = Math.Abs(p.X - XOf(_stops[i].Position));
            if (d <= MarkerHalfWidth + 2 && d < bestDist) { best = i; bestDist = d; }
        }
        // 重疊時偏好已選取的那個（才拖得動它）
        if (best >= 0 && _selected != best && Math.Abs(p.X - XOf(_stops[_selected].Position)) <= MarkerHalfWidth + 2)
            return _selected;
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var hit = HitMarker(p);

        if (props.IsRightButtonPressed)
        {
            if (hit >= 0) RemoveStop(hit);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (hit >= 0)
        {
            SelectedIndex = hit;
            if (e.ClickCount == 2)
            {
                StopActivated?.Invoke(hit);
                e.Handled = true;
                return;
            }
        }
        else if (BarRect.Contains(p) || (p.Y >= 0 && p.Y <= BarRect.Bottom + MarkerHeight + 6))
        {
            // 空白處：新增節點
            var t = PositionOf(p.X);
            var added = _stops.Insert(t);
            var stop = new GradientStop(t, _stops.ColorAt(t));
            _stops = added;
            _selected = Math.Max(0, added.IndexOf(stop));
            SelectionChanged?.Invoke(_selected);
            Changed?.Invoke(_stops);
            hit = _selected;
        }
        else return;

        _dragging = hit;
        _dragStart = _stops;
        _detaching = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging < 0) return;
        var p = e.GetPosition(this);
        var t = PositionOf(p.X);
        var canDetach = _stops.Count > 2;
        _detaching = canDetach && p.Y > BarRect.Bottom + MarkerHeight + DetachDistance;

        var moved = _stops.WithPosition(_dragging, t);
        var stop = new GradientStop(t, _stops[_dragging].Color);
        _stops = moved;
        _dragging = Math.Max(0, moved.IndexOf(stop));
        if (_selected != _dragging) { _selected = _dragging; SelectionChanged?.Invoke(_selected); }
        Changed?.Invoke(_stops);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging < 0) return;
        e.Pointer.Capture(null);
        var idx = _dragging;
        _dragging = -1;
        if (_detaching)
        {
            _detaching = false;
            RemoveStop(idx);
            return;
        }
        if (!Equals(_dragStart, _stops)) Committed?.Invoke(_stops);
        _dragStart = null;
        InvalidateVisual();
        e.Handled = true;
    }

    private void RemoveStop(int index)
    {
        if (_stops.Count <= 2) return;
        _stops = _stops.RemoveAt(index);
        _selected = Math.Clamp(_selected > index ? _selected - 1 : _selected, 0, _stops.Count - 1);
        SelectionChanged?.Invoke(_selected);
        Changed?.Invoke(_stops);
        Committed?.Invoke(_stops);
        InvalidateVisual();
    }

    // ---- 外面欄位改選中的節點 ----

    public void SetSelectedColor(SKColor color, bool commit)
    {
        _stops = _stops.WithColor(_selected, color);
        Changed?.Invoke(_stops);
        if (commit) Committed?.Invoke(_stops);
        InvalidateVisual();
    }

    public void SetSelectedPosition(float position, bool commit)
    {
        var stop = new GradientStop(Math.Clamp(position, 0f, 1f), _stops[_selected].Color);
        var moved = _stops.WithPosition(_selected, stop.Position);
        _stops = moved;
        var idx = Math.Max(0, moved.IndexOf(stop));
        if (idx != _selected) { _selected = idx; SelectionChanged?.Invoke(idx); }
        Changed?.Invoke(_stops);
        if (commit) Committed?.Invoke(_stops);
        InvalidateVisual();
    }

    public void RemoveSelected() => RemoveStop(_selected);

    public void Reverse()
    {
        var stop = _stops[_selected] with { Position = 1f - _stops[_selected].Position };
        _stops = _stops.Reversed();
        _selected = Math.Max(0, _stops.IndexOf(stop));
        SelectionChanged?.Invoke(_selected);
        Changed?.Invoke(_stops);
        Committed?.Invoke(_stops);
        InvalidateVisual();
    }

    /// <summary>節點平均分佈（保留顏色順序）。</summary>
    public void Distribute()
    {
        if (_stops.Count < 2) return;
        var list = new List<GradientStop>();
        for (var i = 0; i < _stops.Count; i++)
            list.Add(new GradientStop(i / (float)(_stops.Count - 1), _stops[i].Color));
        _stops = new GradientStops(list);
        Changed?.Invoke(_stops);
        Committed?.Invoke(_stops);
        InvalidateVisual();
    }
}
