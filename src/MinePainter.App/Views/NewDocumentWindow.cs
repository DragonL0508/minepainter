using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 「新增」對話框（paint.net 的 File → New）：預設尺寸集、寬高、背景色。
/// 上次使用的設定會記到下一次（只在本次執行期間）。
/// </summary>
public sealed class NewDocumentWindow : ModalDialog
{
    public const int MaxSize = 16384;

    private static readonly (string Label, int W, int H)[] Presets =
    [
        ("自訂", 0, 0),
        ("640 × 480", 640, 480),
        ("800 × 600", 800, 600),
        ("1024 × 768", 1024, 768),
        ("1280 × 720（HD）", 1280, 720),
        ("1600 × 1200", 1600, 1200),
        ("1920 × 1080（Full HD）", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
        ("3840 × 2160（4K）", 3840, 2160),
    ];

    private static readonly (string Label, SKColor Color)[] Backgrounds =
    [
        ("白色", SKColors.White),
        ("透明", SKColors.Transparent),
        ("黑色", SKColors.Black),
    ];

    // 記住上次的選擇（App 存活期間）
    private static int _lastWidth = 1920;
    private static int _lastHeight = 1080;
    private static int _lastBackground;

    public int DocWidth { get; private set; }
    public int DocHeight { get; private set; }
    public SKColor DocBackground { get; private set; }

    private readonly ComboBox _presetCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly ComboBox _backgroundCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _memoryLabel = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
    };
    private bool _suppress;

    public NewDocumentWindow() : base("新增影像", 320)
    {
        foreach (var (label, _, _) in Presets) _presetCombo.Items.Add(label);
        foreach (var (label, _) in Backgrounds) _backgroundCombo.Items.Add(label);

        _widthBox.Value = _lastWidth;
        _heightBox.Value = _lastHeight;
        _backgroundCombo.SelectedIndex = _lastBackground;
        SyncPresetFromSize();
        UpdateMemoryLabel();

        _presetCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _presetCombo.SelectedIndex <= 0) return;
            var (_, w, h) = Presets[_presetCombo.SelectedIndex];
            _suppress = true;
            _widthBox.Value = w;
            _heightBox.Value = h;
            _suppress = false;
            UpdateMemoryLabel();
        };
        _widthBox.ValueChanged += _ => OnSizeEdited();
        _heightBox.ValueChanged += _ => OnSizeEdited();

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("預設集", _presetCombo),
                LabeledRow("寬度", WithUnit(_widthBox, "像素")),
                LabeledRow("高度", WithUnit(_heightBox, "像素")),
                LabeledRow("背景", _backgroundCombo),
                new Separator { Margin = new Thickness(0, 3) },
                _memoryLabel,
            },
        };

        SetBody(body, ButtonRow(
            MakeButton("確定", primary: true, confirm: true),
            MakeButton("取消")));

        Closed += (_, _) =>
        {
            if (!Confirmed) return;
            DocWidth = (int)_widthBox.Value;
            DocHeight = (int)_heightBox.Value;
            DocBackground = Backgrounds[Math.Max(0, _backgroundCombo.SelectedIndex)].Color;
            _lastWidth = DocWidth;
            _lastHeight = DocHeight;
            _lastBackground = Math.Max(0, _backgroundCombo.SelectedIndex);
        };
    }

    private void OnSizeEdited()
    {
        if (_suppress) return;
        SyncPresetFromSize();
        UpdateMemoryLabel();
    }

    private void SyncPresetFromSize()
    {
        var w = (int)_widthBox.Value;
        var h = (int)_heightBox.Value;
        var idx = Array.FindIndex(Presets, p => p.W == w && p.H == h);
        _suppress = true;
        _presetCombo.SelectedIndex = idx > 0 ? idx : 0;
        _suppress = false;
    }

    private void UpdateMemoryLabel()
    {
        var bytes = (long)_widthBox.Value * (long)_heightBox.Value * 4;
        _memoryLabel.Text = $"每個圖層約 {bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private static Control LabeledRow(string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = 52,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(text, Dock.Left);
        return new DockPanel { Children = { text, control } };
    }

    private static Control WithUnit(Control control, string unit) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            control,
            new TextBlock
            {
                Text = unit,
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };
}
