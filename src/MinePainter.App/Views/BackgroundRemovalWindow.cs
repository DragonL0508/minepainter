using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MinePainter.App.Controls;
using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>
/// 圖層 → AI 去背 的設定對話框。按確定後就地跑（按鈕變「處理中」、可取消），
/// 完成才關窗；結果直接寫進圖層（一步 undo）。
/// </summary>
public sealed class BackgroundRemovalWindow : ModalDialog
{
    // 記住上次的選擇（App 存活期間）
    private static string? _lastModel;
    private static bool _lastGpu = true;
    private static bool _lastSolid = true;
    private static int _lastContrast;
    private static int _lastShift;

    private readonly EditorSession _session;
    private readonly RasterLayer _layer;
    private readonly IReadOnlyList<OnnxModelInfo> _models;

    private readonly ComboBox _modelCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _gpuCheck = new() { Content = "使用 GPU（DirectML；不支援時自動改用 CPU）", FontSize = 12 };
    private readonly CheckBox _solidCheck = new() { Content = "內部填實（只在邊緣保留半透明）", FontSize = 12 };
    private readonly BarSlider _contrastBar = new() { Minimum = 0, Maximum = 100, Width = 160, Suffix = "%" };
    private readonly BarSlider _shiftBar = new() { Minimum = -20, Maximum = 20, Width = 160, Suffix = "px" };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush };
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>已套用到圖層。</summary>
    public bool Applied { get; private set; }
    /// <summary>失敗訊息（null = 沒失敗）。</summary>
    public string? Error { get; private set; }

    public BackgroundRemovalWindow(EditorSession session, RasterLayer layer, IReadOnlyList<OnnxModelInfo> models)
        : base("AI 去背", 380)
    {
        _session = session;
        _layer = layer;
        _models = models;

        foreach (var m in models) _modelCombo.Items.Add(m.Name);
        var idx = _lastModel == null ? -1 : models.ToList().FindIndex(m => m.Name == _lastModel);
        if (idx < 0) idx = models.ToList().FindIndex(m => m.Name.Contains("isnet", StringComparison.OrdinalIgnoreCase));
        _modelCombo.SelectedIndex = Math.Max(0, idx);
        _gpuCheck.IsChecked = _lastGpu;
        _solidCheck.IsChecked = _lastSolid;
        _contrastBar.Value = _lastContrast;
        _shiftBar.Value = _lastShift;
        ToolTip.SetTip(_solidCheck, "模型的機率圖在物件內部常只有六、七成，會讓內部變半透明；勾選後離邊界夠遠的內部一律不透明，半透明只留在邊緣（髮絲、毛邊）");
        ToolTip.SetTip(_contrastBar, "遮罩對比：拉高可去掉半透明的殘影，但也會失去柔邊");
        ToolTip.SetTip(_shiftBar, "邊緣收縮（負）／擴張（正）：收縮可吃掉殘留的背景色邊");

        var hint = new TextBlock
        {
            Text = (layer.HasActiveEffects || layer.HasElements
                ? "本圖層的效果堆疊／文字物件會先平面化成像素，再去背。"
                : "去背結果直接寫進本圖層（可 undo）。") + "邊緣一律以高清原圖做引導濾波精修。",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("模型", _modelCombo),
                _gpuCheck,
                _solidCheck,
                LabeledRow("遮罩對比", _contrastBar),
                LabeledRow("邊緣收縮", _shiftBar),
                new Separator { Margin = new Thickness(0, 3) },
                hint,
                _status,
            },
        };

        _okButton = MakeButton("確定", primary: true, confirm: true);
        _cancelButton = MakeButton("取消");
        SetBody(body, ButtonRow(_okButton, _cancelButton));

        Closing += (_, _) => _cts?.Cancel();
    }

    /// <summary>「確定」= 開始跑；跑完自己關窗。回傳 false 讓對話框留著。</summary>
    protected override bool Validate()
    {
        if (_running) return false;
        _running = true;

        var model = _models[Math.Clamp(_modelCombo.SelectedIndex, 0, _models.Count - 1)];
        var options = new BackgroundRemovalOptions
        {
            Model = model,
            UseGpu = _gpuCheck.IsChecked == true,
            SolidCore = _solidCheck.IsChecked == true,
            Contrast = (int)_contrastBar.Value,
            Shift = (int)_shiftBar.Value,
        };
        _lastModel = model.Name;
        _lastGpu = options.UseGpu;
        _lastSolid = options.SolidCore;
        _lastContrast = options.Contrast;
        _lastShift = options.Shift;

        _okButton.IsEnabled = false;
        _modelCombo.IsEnabled = _gpuCheck.IsEnabled = _solidCheck.IsEnabled = false;
        _contrastBar.IsEnabled = _shiftBar.IsEnabled = false;
        _status.Text = "處理中…（第一次載入模型會多花幾秒）";

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var started = DateTime.UtcNow;
        _ = Task.Run(() => BackgroundRemovalCommand.Run(_session, _layer, options, ct), ct)
            .ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (t.IsCanceled || (t.IsFaulted && t.Exception?.InnerException is OperationCanceledException))
                    {
                        Error = null;
                    }
                    else if (t.IsFaulted)
                    {
                        Error = t.Exception?.InnerException?.Message ?? t.Exception?.Message;
                    }
                    else
                    {
                        Applied = t.Result;
                        if (!Applied) Error = "圖層沒有內容";
                    }
                    _running = false;
                    Confirmed = Applied;
                    _status.Text = $"完成（{(DateTime.UtcNow - started).TotalSeconds:0.0} 秒）";
                    Close();
                });
            });
        return false;
    }

    private static Control LabeledRow(string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Width = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppTheme.TextBrush,
        };
        return new DockPanel
        {
            Children = { text, control },
        };
    }
}
