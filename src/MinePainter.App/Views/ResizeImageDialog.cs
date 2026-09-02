using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>調整影像大小（paint.net 的 Resize）：百分比或絕對尺寸，可維持長寬比。</summary>
public sealed class ResizeImageDialog : ModalDialog
{
    private const int MaxSize = 16384;

    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly NumberBox _percentBox = new() { Minimum = 1, Maximum = 1000, Width = 90 };
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly CheckBox _keepAspect = new() { Content = "維持長寬比", FontSize = 12, IsChecked = true };
    private readonly TextBlock _info = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush };
    private bool _suppress;

    public int NewWidth => (int)Math.Round(_widthBox.Value);
    public int NewHeight => (int)Math.Round(_heightBox.Value);

    public ResizeImageDialog(int width, int height) : base("調整影像大小", 320)
    {
        _originalWidth = width;
        _originalHeight = height;
        _percentBox.Value = 100;
        _widthBox.Value = width;
        _heightBox.Value = height;
        UpdateInfo();

        _percentBox.ValueChanged += v =>
        {
            if (_suppress) return;
            _suppress = true;
            _widthBox.Value = Math.Clamp(Math.Round(_originalWidth * v / 100), 1, MaxSize);
            _heightBox.Value = Math.Clamp(Math.Round(_originalHeight * v / 100), 1, MaxSize);
            _suppress = false;
            UpdateInfo();
        };
        _widthBox.ValueChanged += v =>
        {
            if (_suppress) return;
            _suppress = true;
            if (_keepAspect.IsChecked == true)
                _heightBox.Value = Math.Clamp(Math.Round(v * _originalHeight / _originalWidth), 1, MaxSize);
            _percentBox.Value = Math.Clamp(Math.Round(v * 100 / _originalWidth), 1, 1000);
            _suppress = false;
            UpdateInfo();
        };
        _heightBox.ValueChanged += v =>
        {
            if (_suppress) return;
            _suppress = true;
            if (_keepAspect.IsChecked == true)
                _widthBox.Value = Math.Clamp(Math.Round(v * _originalWidth / _originalHeight), 1, MaxSize);
            _percentBox.Value = Math.Clamp(Math.Round(v * 100 / _originalHeight), 1, 1000);
            _suppress = false;
            UpdateInfo();
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Row("百分比", _percentBox, "%"),
                Row("寬度", _widthBox, "px"),
                Row("高度", _heightBox, "px"),
                _keepAspect,
                _info,
            },
        };
        SetBody(body, ButtonRow(MakeButton("確定", primary: true, confirm: true), MakeButton("取消")));
    }

    private void UpdateInfo()
    {
        var w = NewWidth;
        var h = NewHeight;
        _info.Text = $"{_originalWidth} × {_originalHeight}  →  {w} × {h}（約 {w * (long)h * 4 / (1024.0 * 1024.0):0.#} MB／層）";
    }

    internal static Control Row(string label, Control control, string unit)
    {
        var text = new TextBlock { Text = label, FontSize = 12, Width = 56, VerticalAlignment = VerticalAlignment.Center };
        var unitText = new TextBlock
        {
            Text = unit,
            FontSize = 12,
            Foreground = AppTheme.TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { text, control, unitText } };
    }

    protected override bool Validate() => NewWidth >= 1 && NewHeight >= 1;
}

/// <summary>調整畫布大小（paint.net 的 Canvas Size）：尺寸 + 3×3 錨點。</summary>
public sealed class CanvasSizeDialog : ModalDialog
{
    private const int MaxSize = 16384;

    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly CheckBox _keepAspect = new() { Content = "維持長寬比", FontSize = 12, IsChecked = false };
    private readonly AnchorPicker _anchor = new();
    private int _anchorIndex = 4; // 中央
    private bool _suppress;

    public int NewWidth => (int)Math.Round(_widthBox.Value);
    public int NewHeight => (int)Math.Round(_heightBox.Value);
    public float AnchorX => (_anchorIndex % 3) / 2f;
    public float AnchorY => (_anchorIndex / 3) / 2f;

    public CanvasSizeDialog(int width, int height) : base("調整畫布大小", 320)
    {
        _originalWidth = width;
        _originalHeight = height;
        _widthBox.Value = width;
        _heightBox.Value = height;

        _widthBox.ValueChanged += v =>
        {
            if (_suppress || _keepAspect.IsChecked != true) return;
            _suppress = true;
            _heightBox.Value = Math.Clamp(Math.Round(v * _originalHeight / _originalWidth), 1, MaxSize);
            _suppress = false;
        };
        _heightBox.ValueChanged += v =>
        {
            if (_suppress || _keepAspect.IsChecked != true) return;
            _suppress = true;
            _widthBox.Value = Math.Clamp(Math.Round(v * _originalWidth / _originalHeight), 1, MaxSize);
            _suppress = false;
        };

        _anchor.Index = _anchorIndex;
        _anchor.Changed += i => _anchorIndex = i;

        var anchorLabel = new TextBlock { Text = "錨點", FontSize = 12, Width = 56, VerticalAlignment = VerticalAlignment.Top };
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { anchorLabel, _anchor } };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                ResizeImageDialog.Row("寬度", _widthBox, "px"),
                ResizeImageDialog.Row("高度", _heightBox, "px"),
                _keepAspect,
                anchorRow,
            },
        };
        SetBody(body, ButtonRow(MakeButton("確定", primary: true, confirm: true), MakeButton("取消")));
    }

    protected override bool Validate() => NewWidth >= 1 && NewHeight >= 1;
}
