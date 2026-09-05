using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;
using MinePainter.Core.Documents;
using SkiaSharp;

namespace MinePainter.App.Views;

/// <summary>
/// 「新增」對話框（paint.net 的 File → New，加上 Photoshop 的實體單位與解析度）：
/// 預設集（螢幕像素／印刷尺寸）、寬高（像素、公分、公釐、英寸）、解析度、背景色。
/// 文件內部永遠是像素 + dpi；公分／英寸只是換算著顯示（見 <see cref="PhysicalUnits"/>）。
/// 換解析度時：用實體單位在編輯 → 實體尺寸不變、像素跟著變（印刷的想法）；用像素在編輯 → 像素不變。
/// 上次使用的設定會記到下一次（只在本次執行期間）。
/// </summary>
public sealed class NewDocumentWindow : ModalDialog
{
    public const int MaxSize = 16384;

    private static readonly (string Label, SKColor Color)[] Backgrounds =
    [
        ("白色", SKColors.White),
        ("透明", SKColors.Transparent),
        ("黑色", SKColors.Black),
    ];

    private static readonly LengthUnit[] Units = [LengthUnit.Pixel, LengthUnit.Centimeter, LengthUnit.Millimeter, LengthUnit.Inch];
    private static readonly ResolutionUnit[] ResolutionUnits = [ResolutionUnit.PixelsPerInch, ResolutionUnit.PixelsPerCentimeter];

    // 記住上次的選擇（App 存活期間）
    private static int _lastWidth = 1920;
    private static int _lastHeight = 1080;
    private static double _lastDpi = PhysicalUnits.ScreenDpi;
    private static LengthUnit _lastUnit = LengthUnit.Pixel;
    private static ResolutionUnit _lastResolutionUnit = ResolutionUnit.PixelsPerInch;
    private static int _lastBackground;
    private static bool _lastFastMode;

    public int DocWidth { get; private set; }
    public int DocHeight { get; private set; }
    public float Dpi { get; private set; } = PhysicalUnits.ScreenDpi;
    public SKColor DocBackground { get; private set; }

    /// <summary>使用者選了快速模式：畫布用 <see cref="ProxyWidth"/>×<see cref="ProxyHeight"/>，輸出仍是 Doc 尺寸。</summary>
    public bool FastMode { get; private set; }

    public int ProxyWidth { get; private set; }

    public int ProxyHeight { get; private set; }

    // 內部狀態（像素 + dpi 為準）
    private int _widthPx;
    private int _heightPx;
    private double _dpi;
    private LengthUnit _unit;
    private ResolutionUnit _resolutionUnit;
    private bool _suppress;

    private readonly ComboBox _presetCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = MaxSize, Width = 90 };
    private readonly ComboBox _unitCombo = new() { FontSize = 12, Width = 78 };
    private readonly NumberBox _dpiBox = new() { Minimum = 1, Maximum = 10000, Width = 90, Presets = "72,96,150,200,300,600" };
    private readonly ComboBox _resolutionUnitCombo = new() { FontSize = 12, Width = 96 };
    private readonly ComboBox _backgroundCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _infoLabel = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _suggestionLabel = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush, IsVisible = false };

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

    public NewDocumentWindow() : base("新增影像", 360)
    {
        _widthPx = _lastWidth;
        _heightPx = _lastHeight;
        _dpi = _lastDpi;
        _unit = _lastUnit;
        _resolutionUnit = _lastResolutionUnit;

        _presetCombo.Items.Add("自訂");
        foreach (var preset in PhysicalUnits.Presets) _presetCombo.Items.Add($"{preset.Group} · {preset.Label}");
        foreach (var unit in Units) _unitCombo.Items.Add(PhysicalUnits.Label(unit));
        foreach (var unit in ResolutionUnits) _resolutionUnitCombo.Items.Add(PhysicalUnits.Label(unit));
        foreach (var (label, _) in Backgrounds) _backgroundCombo.Items.Add(label);

        _unitCombo.SelectedIndex = Array.IndexOf(Units, _unit);
        _resolutionUnitCombo.SelectedIndex = Array.IndexOf(ResolutionUnits, _resolutionUnit);
        _backgroundCombo.SelectedIndex = _lastBackground;
        RefreshBoxesFromState();
        SyncPresetFromSize();
        UpdateInfo();

        _presetCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _presetCombo.SelectedIndex <= 0) return;
            var preset = PhysicalUnits.Presets[_presetCombo.SelectedIndex - 1];
            _widthPx = preset.Width;
            _heightPx = preset.Height;
            _dpi = preset.Dpi;
            // 印刷預設集用公釐看比較直覺；螢幕的看像素
            _unit = preset.Group == "印刷" && _unit == LengthUnit.Pixel ? LengthUnit.Millimeter
                : preset.Group == "螢幕" ? LengthUnit.Pixel : _unit;
            _suppress = true;
            _unitCombo.SelectedIndex = Array.IndexOf(Units, _unit);
            _suppress = false;
            RefreshBoxesFromState();
            UpdateInfo();
        };
        _unitCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _unitCombo.SelectedIndex < 0) return;
            _unit = Units[_unitCombo.SelectedIndex];
            RefreshBoxesFromState();
        };
        _resolutionUnitCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress || _resolutionUnitCombo.SelectedIndex < 0) return;
            _resolutionUnit = ResolutionUnits[_resolutionUnitCombo.SelectedIndex];
            RefreshBoxesFromState();
        };
        _widthBox.ValueChanged += v => OnSizeEdited(v, isWidth: true);
        _heightBox.ValueChanged += v => OnSizeEdited(v, isWidth: false);
        _dpiBox.ValueChanged += OnDpiEdited;

        var swap = new Button
        {
            Content = "⇄",
            FontSize = 12,
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(swap, "交換寬高（直式／橫式）");
        swap.Click += (_, _) =>
        {
            (_widthPx, _heightPx) = (_heightPx, _widthPx);
            RefreshBoxesFromState();
            SyncPresetFromSize();
            UpdateInfo();
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("預設集", _presetCombo),
                LabeledRow("寬度", Row(_widthBox, _unitCombo)),
                LabeledRow("高度", Row(_heightBox, swap)),
                LabeledRow("解析度", Row(_dpiBox, _resolutionUnitCombo)),
                LabeledRow("背景", _backgroundCombo),
                new Separator { Margin = new Thickness(0, 3) },
                _infoLabel,
                _suggestionLabel,
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
            DocWidth = _widthPx;
            DocHeight = _heightPx;
            Dpi = (float)_dpi;
            FastMode = _fastMode.IsVisible && _fastMode.IsChecked == true;
            var (proxyW, proxyH) = Core.Documents.FastMode.ProxySize(DocWidth, DocHeight);
            ProxyWidth = proxyW;
            ProxyHeight = proxyH;
            DocBackground = Backgrounds[Math.Max(0, _backgroundCombo.SelectedIndex)].Color;
            _lastWidth = DocWidth;
            _lastHeight = DocHeight;
            _lastDpi = _dpi;
            _lastUnit = _unit;
            _lastResolutionUnit = _resolutionUnit;
            _lastBackground = Math.Max(0, _backgroundCombo.SelectedIndex);
        };
    }

    // ---- 測試鉤子：直接戳內部狀態，不用模擬打字 ----
    internal int WidthPixels => _widthPx;
    internal int HeightPixels => _heightPx;
    internal double CurrentDpi => _dpi;
    internal LengthUnit CurrentUnit => _unit;
    internal string InfoText => _infoLabel.Text ?? "";
    internal void SelectUnit(LengthUnit unit) => _unitCombo.SelectedIndex = Array.IndexOf(Units, unit);
    internal void SelectPreset(string labelContains) =>
        _presetCombo.SelectedIndex = 1 + Array.FindIndex(PhysicalUnits.Presets, p => p.Label.Contains(labelContains, StringComparison.Ordinal));
    // NumberBox 的 Value 由程式設定時不會發 ValueChanged（那是使用者輸入才有的），所以這裡自己叫處理常式
    internal void EnterWidth(double value) { _widthBox.Value = value; OnSizeEdited(_widthBox.Value, isWidth: true); }
    internal void EnterResolution(double value) { _dpiBox.Value = value; OnDpiEdited(_dpiBox.Value); }
    internal double ShownWidth => _widthBox.Value;

    /// <summary>外部建議的尺寸（例如剪貼簿裡影像的大小）：先填進去，並說明是哪來的。</summary>
    public void SuggestSize(int width, int height, string source)
    {
        if (width <= 0 || height <= 0) return;
        _widthPx = Math.Min(width, MaxSize);
        _heightPx = Math.Min(height, MaxSize);
        RefreshBoxesFromState();
        SyncPresetFromSize();
        UpdateInfo();
        _suggestionLabel.Text = $"已帶入{source}的尺寸";
        _suggestionLabel.IsVisible = true;
    }

    /// <summary>寬高欄位改了：換算成像素存起來。</summary>
    private void OnSizeEdited(double value, bool isWidth)
    {
        if (_suppress) return;
        var px = Math.Clamp(PhysicalUnits.ToPixels(value, _unit, _dpi), 1, MaxSize);
        if (isWidth) _widthPx = px;
        else _heightPx = px;
        _suggestionLabel.IsVisible = false;
        SyncPresetFromSize();
        UpdateInfo();
    }

    /// <summary>解析度改了：用實體單位在看的話保持實體尺寸、像素跟著變；用像素在看的話像素不動。</summary>
    private void OnDpiEdited(double value)
    {
        if (_suppress) return;
        var newDpi = Math.Clamp(PhysicalUnits.ToDpi(value, _resolutionUnit), 1, 10000);
        if (_unit != LengthUnit.Pixel && Math.Abs(newDpi - _dpi) > 1e-9)
        {
            var physicalW = PhysicalUnits.FromPixels(_widthPx, _unit, _dpi);
            var physicalH = PhysicalUnits.FromPixels(_heightPx, _unit, _dpi);
            _widthPx = Math.Clamp(PhysicalUnits.ToPixels(physicalW, _unit, newDpi), 1, MaxSize);
            _heightPx = Math.Clamp(PhysicalUnits.ToPixels(physicalH, _unit, newDpi), 1, MaxSize);
        }
        _dpi = newDpi;
        RefreshBoxesFromState();
        SyncPresetFromSize();
        UpdateInfo();
    }

    /// <summary>把內部狀態（像素 + dpi）依目前單位寫回三個數字框。</summary>
    private void RefreshBoxesFromState()
    {
        _suppress = true;
        var decimals = PhysicalUnits.Decimals(_unit);
        foreach (var box in new[] { _widthBox, _heightBox })
        {
            box.Decimals = decimals;
            box.Minimum = _unit == LengthUnit.Pixel ? 1 : 0.01;
            box.Maximum = PhysicalUnits.FromPixels(MaxSize, _unit, _dpi);
            box.Step = _unit switch { LengthUnit.Pixel => 1, LengthUnit.Millimeter => 1, LengthUnit.Inch => 0.1, _ => 0.1 };
        }
        _widthBox.Value = PhysicalUnits.FromPixels(_widthPx, _unit, _dpi);
        _heightBox.Value = PhysicalUnits.FromPixels(_heightPx, _unit, _dpi);
        _dpiBox.Decimals = _resolutionUnit == ResolutionUnit.PixelsPerCentimeter ? 2 : 0;
        _dpiBox.Value = PhysicalUnits.FromDpi(_dpi, _resolutionUnit);
        _suppress = false;
        UpdateInfo();
    }

    private void SyncPresetFromSize()
    {
        var idx = Array.FindIndex(PhysicalUnits.Presets, p => p.Width == _widthPx && p.Height == _heightPx && Math.Abs(p.Dpi - _dpi) < 0.5);
        _suppress = true;
        _presetCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
        _suppress = false;
    }

    private void UpdateInfo()
    {
        var w = _widthPx;
        var h = _heightPx;
        var bytes = (long)w * h * 4;
        var cmW = PhysicalUnits.FromPixels(w, LengthUnit.Centimeter, _dpi);
        var cmH = PhysicalUnits.FromPixels(h, LengthUnit.Centimeter, _dpi);
        _infoLabel.Text =
            $"{w} × {h} 像素 · 印出約 {cmW:0.##} × {cmH:0.##} 公分（{_dpi:0.##} dpi） · 每個圖層約 {bytes / (1024.0 * 1024.0):0.#} MB";

        // 比代理級別大才提議快速模式（預設 Full HD，可在設定改；見 Core.Documents.FastMode）
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

    private static Control Row(Control first, Control second) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { first, second },
    };
}
