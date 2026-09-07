using Avalonia.Controls;
using Avalonia.Layout;
using MinePainter.App.Controls;
using MinePainter.Core.Documents;

namespace MinePainter.App.Views;

/// <summary>
/// 出血與安全框（送印設定）。廠商要的檔案是「含出血」的尺寸，所以畫布＝裁切尺寸＋兩邊出血；
/// 裁切線與安全框只是畫面上的輔助線，不會被匯出。
/// </summary>
public sealed class PrintSpecDialog : ModalDialog
{
    /// <summary>對話框裡的選項：裁切尺寸（mm）＋出血／安全距離（mm）。</summary>
    private sealed record Choice(string Label, double TrimW, double TrimH, double Bleed, double Safe);

    private static readonly Choice[] Choices =
    [
        new("名片（裁切 90 × 54 mm，出血 1 mm）", 90, 54, 1, 3),
        new("名片（裁切 90 × 54 mm，出血 3 mm）", 90, 54, 3, 3),
        new("明信片（裁切 100 × 148 mm，出血 3 mm）", 100, 148, 3, 3),
        new("A4（裁切 210 × 297 mm，出血 3 mm）", 210, 297, 3, 3),
    ];

    private const string Custom = "自訂";
    private const string None = "不使用（移除輔助線）";

    private readonly ComboBox _preset = new() { Width = 260, FontSize = 12 };
    private readonly NumberBox _trimW = new() { Minimum = 1, Maximum = 5000, Width = 90, Decimals = 1, Step = 1 };
    private readonly NumberBox _trimH = new() { Minimum = 1, Maximum = 5000, Width = 90, Decimals = 1, Step = 1 };
    private readonly NumberBox _bleed = new() { Minimum = 0, Maximum = PrintSpec.MaxMm, Width = 90, Decimals = 1, Step = 0.5 };
    private readonly NumberBox _safe = new() { Minimum = 0, Maximum = PrintSpec.MaxMm, Width = 90, Decimals = 1, Step = 0.5 };
    private readonly NumberBox _dpi = new() { Minimum = 1, Maximum = 10000, Width = 90, Decimals = 0, Step = 10 };
    private readonly CheckBox _keepCanvas = new() { Content = "只加輔助線，不改畫布大小", FontSize = 12 };
    private readonly TextBlock _info = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private bool _suppress;

    /// <summary>套用後的規格；null＝移除。</summary>
    public PrintSpec? Spec { get; private set; }

    /// <summary>套用後的畫布尺寸（可能與現在相同）。</summary>
    public int CanvasWidth { get; private set; }

    public int CanvasHeight { get; private set; }

    /// <summary>使用者改過的解析度（不改畫布時仍可能改到印出來的大小）。</summary>
    public float Dpi { get; private set; }

    public PrintSpecDialog(Document doc) : base("出血與安全框", 380)
    {
        _canvasWidth = doc.Width;
        _canvasHeight = doc.Height;
        CanvasWidth = doc.Width;
        CanvasHeight = doc.Height;
        Dpi = doc.Dpi;

        _preset.ItemsSource = new[] { Custom }
            .Concat(Choices.Select(c => c.Label))
            .Append(None)
            .ToArray();

        _dpi.Value = doc.Dpi;
        var existing = doc.Print;
        _bleed.Value = existing?.BleedMm ?? PrintSpec.CommonBleedMm;
        _safe.Value = existing?.SafeMm ?? PrintSpec.CommonSafeMm;
        // 沒有規格時：把目前畫布當成「裁切後」尺寸，往外加出血（最常見的情況是「圖已經做好了，廠商才說要出血」）
        var trim = existing is { } spec
            ? spec.TrimSizeMm(doc)
            : (PrintSpec.PixelsToMm(doc.Width, doc.Dpi), PrintSpec.PixelsToMm(doc.Height, doc.Dpi));
        _trimW.Value = Math.Round(trim.Item1, 1);
        _trimH.Value = Math.Round(trim.Item2, 1);
        _keepCanvas.IsChecked = existing != null; // 已經是印刷檔：預設不要再動它的畫布
        _preset.SelectedIndex = 0;

        _preset.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            var index = _preset.SelectedIndex - 1;
            if (index < 0 || index >= Choices.Length)
            {
                UpdateInfo();
                return;
            }
            var choice = Choices[index];
            _suppress = true;
            _trimW.Value = choice.TrimW;
            _trimH.Value = choice.TrimH;
            _bleed.Value = choice.Bleed;
            _safe.Value = choice.Safe;
            if (_dpi.Value < PhysicalUnits.PrintDpi) _dpi.Value = PhysicalUnits.PrintDpi; // 印刷至少 300
            _keepCanvas.IsChecked = false; // 選了成品尺寸就是要畫布照著改
            _suppress = false;
            UpdateInfo();
        };

        foreach (var box in new[] { _trimW, _trimH, _bleed, _safe, _dpi })
        {
            box.ValueChanged += _ =>
            {
                if (_suppress) return;
                _suppress = true;
                _preset.SelectedIndex = 0; // 動過任何數字就是自訂
                _suppress = false;
                UpdateInfo();
            };
        }
        _keepCanvas.IsCheckedChanged += (_, _) => UpdateInfo();

        ToolTip.SetTip(_keepCanvas,
            "打勾：畫布不動，裁切線＝畫布往內縮一個出血。適合畫布已經是含出血的尺寸。\n" +
            "不打勾：畫布改成「裁切尺寸＋兩邊出血」，現有內容置中。");
        ToolTip.SetTip(_bleed, "底圖要延伸出去、之後會被裁掉的一圈。台灣印刷廠常見 3 mm，也有廠商要 1 mm。");
        ToolTip.SetTip(_safe, "圖文離裁切邊至少要留的距離。裁切公差大約 ±2 mm，留 3 mm 以上才安全。");

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "預設集", FontSize = 12, Width = 56, VerticalAlignment = VerticalAlignment.Center },
                        _preset,
                    },
                },
                ResizeImageDialog.Row("裁切寬", _trimW, "mm"),
                ResizeImageDialog.Row("裁切高", _trimH, "mm"),
                ResizeImageDialog.Row("出血", _bleed, "mm"),
                ResizeImageDialog.Row("安全距離", _safe, "mm"),
                ResizeImageDialog.Row("解析度", _dpi, "dpi"),
                _keepCanvas,
                _info,
            },
        };
        UpdateInfo();
        SetBody(body, ButtonRow(MakeButton("套用", primary: true, confirm: true), MakeButton("取消")));

        Closed += (_, _) =>
        {
            if (!Confirmed) return;
            Dpi = (float)_dpi.Value;
            if (Removing)
            {
                Spec = null;
                CanvasWidth = _canvasWidth;
                CanvasHeight = _canvasHeight;
                return;
            }
            Spec = new PrintSpec(_bleed.Value, _safe.Value);
            var (w, h) = TargetCanvas();
            CanvasWidth = w;
            CanvasHeight = h;
        };
    }

    private bool Removing => _preset.SelectedItem as string == None;

    /// <summary>套用後的畫布尺寸：打勾「不改畫布」就維持原樣，否則＝裁切尺寸＋兩邊出血。</summary>
    private (int Width, int Height) TargetCanvas()
    {
        if (_keepCanvas.IsChecked == true) return (_canvasWidth, _canvasHeight);
        var size = PrintSpec.CanvasSizeFor(_trimW.Value, _trimH.Value, _bleed.Value, _dpi.Value);
        return (size.Width, size.Height);
    }

    private void UpdateInfo()
    {
        if (Removing)
        {
            _info.Text = "移除出血與安全框：畫布與像素都不動，只是不再顯示輔助線。";
            return;
        }

        var (w, h) = TargetCanvas();
        var dpi = _dpi.Value;
        var trimW = PrintSpec.PixelsToMm(w - PrintSpec.MmToPixels(_bleed.Value, dpi) * 2, dpi);
        var trimH = PrintSpec.PixelsToMm(h - PrintSpec.MmToPixels(_bleed.Value, dpi) * 2, dpi);
        var same = w == _canvasWidth && h == _canvasHeight;
        _info.Text =
            $"畫布 {w} × {h} px（{PrintSpec.PixelsToMm(w, dpi):0.#} × {PrintSpec.PixelsToMm(h, dpi):0.#} mm）" +
            (same ? "，維持不變" : $"，目前 {_canvasWidth} × {_canvasHeight} px（內容置中）") +
            $"\n裁切後 {trimW:0.#} × {trimH:0.#} mm；底圖要鋪滿整張畫布，圖文留在安全框內。";
    }

    protected override bool Validate()
    {
        if (Removing) return true;
        var (w, h) = TargetCanvas();
        return w >= 1 && h >= 1 && w <= 16384 && h <= 16384;
    }
}

/// <summary>送印檢查的結果（<see cref="PrintCheck"/>）：一條一條列出來，讓使用者知道要改哪裡。</summary>
public sealed class PrintCheckDialog : ModalDialog
{
    public PrintCheckDialog(PrintCheckResult result, PrintSpec spec) : base("送印檢查", 380)
    {
        var lines = new StackPanel { Spacing = 6 };
        if (result.Ok)
        {
            lines.Children.Add(Line("底圖有鋪滿出血區，圖文也都在安全框內，可以送印。", ok: true));
        }
        else
        {
            if (!result.BleedCovered)
            {
                lines.Children.Add(Line(
                    $"出血區有 {result.UncoveredBleedRatio:P0} 不是不透明的：底圖沒有延伸到裁切線外。" +
                    $"把底圖拉滿整張畫布（會被裁掉 {spec.BleedMm:0.#} mm，那正是出血的用途）。", ok: false));
            }

            if (result.LayersOutsideSafe.Count > 0)
            {
                lines.Children.Add(Line(
                    $"這些圖層超出安全框，可能被裁到：{string.Join("、", result.LayersOutsideSafe)}。" +
                    $"把它們往內移到離裁切線 {spec.SafeMm:0.#} mm 以上。", ok: false));
            }
        }

        SetBody(lines, ButtonRow(MakeButton("知道了", primary: true, confirm: true)));
    }

    private static TextBlock Line(string text, bool ok) => new()
    {
        Text = (ok ? "✓ " : "• ") + text,
        FontSize = 12,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
}
