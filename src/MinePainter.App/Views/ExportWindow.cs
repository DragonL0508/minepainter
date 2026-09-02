using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MinePainter.App.Controls;

namespace MinePainter.App.Views;

/// <summary>
/// 「匯出影像」對話框：格式（PNG／JPEG）、JPEG 品質、匯出尺寸（等比縮放）。
/// 確定後由 MainWindow 接手開檔案儲存對話框。
/// </summary>
public sealed class ExportWindow : ModalDialog
{
    // 記住上次的選擇（App 存活期間）；尺寸每次都回到 100%，因為它跟著文件走
    private static int _lastFormat;
    private static double _lastQuality = 92;

    private readonly int _docWidth;
    private readonly int _docHeight;

    // 結果一律是「關閉時快照的純值」，不懶讀控制項 —— 匯出 lambda 在背景執行緒跑，
    // 背景執行緒讀 Avalonia 控制項（BarSlider.Value 等）會炸 Call from invalid thread
    public bool IsJpeg { get; private set; }
    public int Quality { get; private set; } = 92;
    public int OutWidth { get; private set; }
    public int OutHeight { get; private set; }

    private bool JpegSelected => _formatCombo.SelectedIndex == 1;

    private readonly ComboBox _formatCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly BarSlider _qualityBar = new() { Minimum = 1, Maximum = 100, Label = "品質", Height = 26 };
    private readonly NumberBox _percentBox = new() { Minimum = 1, Maximum = 1000, Width = 70 };
    private readonly NumberBox _widthBox = new() { Minimum = 1, Maximum = NewDocumentWindow.MaxSize, Width = 90 };
    private readonly NumberBox _heightBox = new() { Minimum = 1, Maximum = NewDocumentWindow.MaxSize, Width = 90 };
    private readonly TextBlock _formatHint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };
    private bool _suppress;

    public ExportWindow(int docWidth, int docHeight) : base("匯出影像", 340)
    {
        _docWidth = docWidth;
        _docHeight = docHeight;
        OutWidth = docWidth;
        OutHeight = docHeight;

        _formatCombo.Items.Add("PNG（無損、支援透明）");
        _formatCombo.Items.Add("JPEG（有損、較小檔案）");
        _formatCombo.SelectedIndex = _lastFormat;
        _qualityBar.Value = _lastQuality;

        _percentBox.Value = 100;
        _widthBox.Value = docWidth;
        _heightBox.Value = docHeight;

        _formatCombo.SelectionChanged += (_, _) => SyncFormatUi();
        _percentBox.ValueChanged += v => ApplyScale(v / 100.0);
        _widthBox.ValueChanged += v => ApplyScale(v / _docWidth);
        _heightBox.ValueChanged += v => ApplyScale(v / _docHeight);

        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _widthBox,
                new TextBlock { Text = "×", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                _heightBox,
                new TextBlock
                {
                    Text = "像素",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var percentRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _percentBox,
                new TextBlock
                {
                    Text = $"%（原始 {docWidth} × {docHeight}）",
                    FontSize = 11,
                    Foreground = AppTheme.TextMutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("格式", _formatCombo),
                _qualityBar,
                _formatHint,
                new Separator { Margin = new Thickness(0, 3) },
                LabeledRow("縮放", percentRow),
                LabeledRow("尺寸", sizeRow),
            },
        };

        SetBody(body, ButtonRow(
            MakeButton("匯出…", primary: true, confirm: true),
            MakeButton("取消")));
        SyncFormatUi();

        Closed += (_, _) =>
        {
            // 還在 UI 執行緒時把結果拍下來（ShowDialog 回來後呼叫端才會讀）
            IsJpeg = JpegSelected;
            Quality = (int)_qualityBar.Value;
            OutWidth = (int)_widthBox.Value;
            OutHeight = (int)_heightBox.Value;
            if (!Confirmed) return;
            _lastFormat = _formatCombo.SelectedIndex;
            _lastQuality = _qualityBar.Value;
        };
    }

    /// <summary>三個輸入框互相連動（永遠維持長寬比）。</summary>
    private void ApplyScale(double scale)
    {
        if (_suppress) return;
        _suppress = true;
        _percentBox.Value = Math.Round(scale * 100);
        _widthBox.Value = Math.Max(1, (int)Math.Round(_docWidth * scale));
        _heightBox.Value = Math.Max(1, (int)Math.Round(_docHeight * scale));
        _suppress = false;
    }

    private void SyncFormatUi()
    {
        _qualityBar.IsVisible = JpegSelected;
        _formatHint.Text = JpegSelected
            ? "JPEG 不支援透明：透明區域會鋪上白色底。"
            : "PNG 為無損壓縮，透明區域會保留。";
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
}
