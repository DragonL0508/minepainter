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

    /// <summary>使用者選了快速模式：畫布用 <see cref="ProxyWidth"/>×<see cref="ProxyHeight"/>，輸出仍是 Doc 尺寸。</summary>
    public bool FastMode { get; private set; }

    public int ProxyWidth { get; private set; }

    public int ProxyHeight { get; private set; }

    private readonly ComboBox _presetCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly ComboBox _backgroundCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _memoryLabel = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
    };

    private readonly CheckBox _fastMode = new()
    {
        Content = "快速模式（實驗）",
        FontSize = 12,
        IsVisible = false,
    };

    private readonly TextBlock _fastModeHint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
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
                _fastMode,
                _fastModeHint,
            },
        };

        SetBody(body, ButtonRow(
            MakeButton("確定", primary: true, confirm: true),
            MakeButton("取消")));

        _fastMode.IsCheckedChanged += (_, _) => _lastFastMode = _fastMode.IsChecked == true;

        Closed += (_, _) =>
        {
            if (!Confirmed) return;
            DocWidth = (int)_widthBox.Value;
            DocHeight = (int)_heightBox.Value;
            FastMode = _fastMode.IsVisible && _fastMode.IsChecked == true;
            var (proxyW, proxyH) = Core.Documents.FastMode.ProxySize(DocWidth, DocHeight);
            ProxyWidth = proxyW;
            ProxyHeight = proxyH;
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

    // 記住上次的選擇（App 存活期間）
    private static bool _lastFastMode;

    private void UpdateMemoryLabel()
    {
        var w = (int)_widthBox.Value;
        var h = (int)_heightBox.Value;
        var bytes = (long)w * h * 4;
        _memoryLabel.Text = $"每個圖層約 {bytes / (1024.0 * 1024.0):0.#} MB";

        // 比 Full HD 大才提議快速模式（見 Core.Documents.FastMode）
        var offer = Core.Documents.FastMode.ShouldOffer(w, h);
        _fastMode.IsVisible = offer;
        _fastModeHint.IsVisible = offer;
        if (!offer)
        {
            _fastMode.IsChecked = false;
            return;
        }

        var (proxyW, proxyH) = Core.Documents.FastMode.ProxySize(w, h);
        _fastMode.IsChecked = _lastFastMode;
        _fastModeHint.Text =
            $"以 {proxyW} × {proxyH} 製作，輸出時整份重算成 {w} × {h}。" +
            "文字、形狀、效果都會用輸出解析度重畫；筆刷畫上去的像素則是放大取樣（會比較軟）。" +
            "隨時可以用「影像 → 轉成完整解析度」切回一般模式。";
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
