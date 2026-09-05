using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MinePainter.App.Controls;
using MinePainter.App.Services;
using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>
/// 圖層 → AI 去背 的設定對話框。按確定後就地跑（按鈕變「處理中」、可取消），
/// 完成才關窗；結果直接寫進圖層（一步 undo）。
///
/// 模型清單第一項是 remove.bg 線上服務（做法與 paint.net 的 Remove Background 插件相同：
/// 整張圖上傳、拿回伺服器算好的去背圖），其餘是本機的 ONNX 模型。
/// </summary>
public sealed class BackgroundRemovalWindow : ModalDialog
{
    /// <summary>模型清單裡代表 remove.bg 的那一項。</summary>
    public const string RemoveBgName = "remove.bg 線上服務（同 paint.net 插件）";

    // 記住上次的選擇（App 存活期間；API Key 與尺寸另外存進設定檔）
    private static string? _lastModel;
    private static bool _lastGpu = true;
    private static bool _lastSolid = true;
    private static int _lastContrast;
    private static int _lastShift;
    private static bool _lastSelectionOnly = true;

    private readonly EditorSession _session;
    private readonly RasterLayer _layer;
    private readonly string _modelFolder;
    private List<OnnxModelInfo> _models;

    private readonly ComboBox _modelCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _downloadButton;
    private readonly CheckBox _gpuCheck = new() { Content = "使用 GPU（DirectML；不支援時自動改用 CPU）", FontSize = 12 };
    private readonly CheckBox _solidCheck = new() { Content = "內部填實（只在邊緣保留半透明）", FontSize = 12 };
    private readonly CheckBox _selectionCheck = new() { Content = "只處理選取範圍（範圍外一併清除）", FontSize = 12 };
    private readonly BarSlider _contrastBar = new() { Minimum = 0, Maximum = 100, Width = 160, Suffix = "%" };
    private readonly BarSlider _shiftBar = new() { Minimum = -20, Maximum = 20, Width = 160, Suffix = "px" };
    private readonly StackPanel _localPanel = new() { Spacing = 8 };

    // remove.bg
    private readonly TextBox _apiKeyBox = new() { FontSize = 12, PasswordChar = '•', Watermark = "貼上 remove.bg 的 API Key" };
    private readonly ComboBox _sizeCombo = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _remotePanel = new() { Spacing = 8 };

    private readonly TextBlock _hint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    private Button _okButton = null!;
    private Button _cancelButton = null!;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _keyImportedFrom;

    /// <summary>已套用到圖層。</summary>
    public bool Applied { get; private set; }

    /// <summary>失敗訊息（null = 沒失敗）。</summary>
    public string? Error { get; private set; }

    /// <summary>成功但有話要說（例如「模型太大，改用 CPU」「扣了 1 點」）；沒有回 null。</summary>
    public string? Note { get; private set; }

    public BackgroundRemovalWindow(EditorSession session, RasterLayer layer, IReadOnlyList<OnnxModelInfo> models,
        string modelFolder)
        : base("AI 去背", 400)
    {
        _session = session;
        _layer = layer;
        _modelFolder = modelFolder;
        _models = models.ToList();

        _downloadButton = MakeButton("下載模型…");
        _downloadButton.Padding = new Thickness(8, 4);
        ToolTip.SetTip(_downloadButton, "從 rembg 官方發佈下載本機去背模型（離線可用，不用 API Key）");

        _sizeCombo.Items.Add("自動（有點數給最高解析度、扣 1 點；沒點數給預覽）");
        _sizeCombo.Items.Add("預覽（約 0.25 百萬像素；免費額度）");
        var settings = AppSettings.Instance;
        _sizeCombo.SelectedIndex = settings.RemoveBgPreview ? 1 : 0;
        _apiKeyBox.Text = settings.RemoveBgApiKey ?? "";
        if (string.IsNullOrWhiteSpace(_apiKeyBox.Text) && TryImportPaintNetKey() is { } imported)
        {
            _apiKeyBox.Text = imported;
            _keyImportedFrom = "paint.net 的 Remove Background 插件";
        }

        var keyLink = new TextBlock
        {
            Text = "取得 API Key",
            FontSize = 11,
            Foreground = AppTheme.AccentBrush,
            TextDecorations = Avalonia.Media.TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        keyLink.PointerPressed += (_, _) => OpenUrl(RemoveBgClient.ApiKeyUrl);
        ToolTip.SetTip(keyLink, RemoveBgClient.ApiKeyUrl);
        ToolTip.SetTip(_apiKeyBox, "登入 remove.bg 後在儀表板取得；會存進設定檔，下次不用再填");
        ToolTip.SetTip(_sizeCombo, "自動＝依圖片大小與帳號點數給最高解析度（paint.net 插件的做法），帳號沒點數時伺服器只會回預覽；預覽＝免費、約 0.25 百萬像素。不管哪種，顏色都是原圖的原解析度像素，伺服器結果只當遮罩用");

        var keyRow = new DockPanel();
        DockPanel.SetDock(keyLink, Dock.Right);
        keyLink.Margin = new Thickness(8, 0, 0, 0);
        keyRow.Children.Add(keyLink);
        keyRow.Children.Add(_apiKeyBox);
        _remotePanel.Children.Add(LabeledRow("API Key", keyRow));
        _remotePanel.Children.Add(LabeledRow("解析度", _sizeCombo));

        _localPanel.Children.Add(_gpuCheck);

        _gpuCheck.IsChecked = _lastGpu;
        _solidCheck.IsChecked = _lastSolid;
        _contrastBar.Value = _lastContrast;
        _shiftBar.Value = _lastShift;

        // 有選取範圍才給這個選項：圈出要去背的東西，模型的解析度全用在它身上（更準），範圍外直接清掉
        _selectionCheck.IsVisible = session.Selection is { IsEmpty: false };
        _selectionCheck.IsChecked = _lastSelectionOnly;
        ToolTip.SetTip(_selectionCheck, "只把選取範圍內的像素送去處理（範圍外是透明），解析度全用在圈出來的物件上；選取範圍外的像素一律清成透明，選取的羽化邊也會保留");
        ToolTip.SetTip(_solidCheck, "模型的機率圖在物件內部常只有六、七成，會讓內部變半透明；勾選後離邊界夠遠的內部一律不透明，半透明只留在邊緣（髮絲、毛邊）");
        ToolTip.SetTip(_contrastBar, "遮罩對比：拉高可去掉半透明的殘影，但也會失去柔邊");
        ToolTip.SetTip(_shiftBar, "邊緣收縮（負）／擴張（正）：收縮可吃掉殘留的背景色邊");

        FillModelCombo(initialName: _lastModel);

        // 選好模型就先講清楚這台機器會怎麼跑（GPU／CPU、要多少記憶體、跑不跑得動），
        // 而不是按下去才發現記憶體不夠
        _modelCombo.SelectionChanged += (_, _) => OnModelChanged();
        _gpuCheck.IsCheckedChanged += (_, _) => UpdatePlanHint();
        _downloadButton.Click += async (_, _) => await DownloadModelsAsync();
        OnModelChanged();

        var modelRow = new DockPanel();
        DockPanel.SetDock(_downloadButton, Dock.Right);
        _downloadButton.Margin = new Thickness(6, 0, 0, 0);
        modelRow.Children.Add(_downloadButton);
        modelRow.Children.Add(_modelCombo);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("模型", modelRow),
                _remotePanel,
                _localPanel,
                _solidCheck,
                LabeledRow("遮罩對比", _contrastBar),
                LabeledRow("邊緣收縮", _shiftBar),
                _selectionCheck,
                new Separator { Margin = new Thickness(0, 3) },
                _hint,
                _status,
            },
        };

        _okButton = MakeButton("確定", primary: true, confirm: true);
        _cancelButton = MakeButton("取消");
        SetBody(body, ButtonRow(_okButton, _cancelButton));

        Closing += (_, _) => _cts?.Cancel();
    }

    private bool IsRemote => _modelCombo.SelectedIndex == 0;

    /// <summary>目前選的本機模型（選 remove.bg 或沒有本機模型時回 null）。</summary>
    private OnnxModelInfo? SelectedLocalModel
    {
        get
        {
            var i = _modelCombo.SelectedIndex - 1;
            return i >= 0 && i < _models.Count ? _models[i] : null;
        }
    }

    private void FillModelCombo(string? initialName)
    {
        _modelCombo.Items.Clear();
        _modelCombo.Items.Add(RemoveBgName);
        foreach (var m in _models) _modelCombo.Items.Add(m.Name);

        var idx = -1;
        if (initialName == RemoveBgName) idx = 0;
        else if (initialName != null) idx = _models.FindIndex(m => m.Name == initialName) is var f && f >= 0 ? f + 1 : -1;
        if (idx < 0 && _models.Count > 0)
        {
            var isnet = _models.FindIndex(m => m.Name.Contains("isnet", StringComparison.OrdinalIgnoreCase));
            idx = (isnet >= 0 ? isnet : 0) + 1;
        }
        // 沒有本機模型，或已經有 remove.bg 的 key：預設走線上
        if (idx < 0 || (initialName == null && !string.IsNullOrWhiteSpace(_apiKeyBox.Text))) idx = 0;
        _modelCombo.SelectedIndex = idx;
    }

    private void OnModelChanged()
    {
        _remotePanel.IsVisible = IsRemote;
        _localPanel.IsVisible = !IsRemote;
        var flatten = _layer.HasActiveEffects || _layer.HasElements
            ? "本圖層的效果堆疊／文字物件會先平面化成像素，再去背。"
            : "去背結果直接寫進本圖層（可 undo）。";
        _hint.Text = IsRemote
            ? flatten + "影像會上傳到 remove.bg（同 paint.net 的 Remove Background 插件）。伺服器結果只當遮罩：顏色一律取自原圖的原解析度像素；伺服器只回預覽尺寸時，遮罩會以原圖做引導濾波精修放大。"
            : flatten + "邊緣一律以高清原圖做引導濾波精修。";
        UpdatePlanHint();
    }

    /// <summary>
    /// 把「這個模型擅長什麼」與「這台機器會怎麼跑它」寫進狀態列。
    /// 前者讓不熟模型的人選得下去，後者讓他不會按下去才發現記憶體不夠。
    /// </summary>
    private void UpdatePlanHint()
    {
        if (_running) return;
        if (IsRemote)
        {
            _status.Text = _keyImportedFrom != null
                ? $"已從{_keyImportedFrom}帶入 API Key。需要網路；每張圖扣 remove.bg 點數（預覽尺寸走免費額度）。"
                : "需要網路；每張圖扣 remove.bg 點數（預覽尺寸走免費額度）。";
            return;
        }
        if (SelectedLocalModel is not { } model)
        {
            _status.Text = "還沒有本機模型：按「下載模型…」或改用 remove.bg。";
            return;
        }
        var about = ModelCatalog.Find(Path.GetFileName(model.Path));
        var plan = InferenceBudget.Describe(model, model.Preset.Size, _gpuCheck.IsChecked == true);
        _status.Text = about == null ? plan : $"{about.Strength}。{plan}";
    }

    private async Task DownloadModelsAsync()
    {
        if (_running) return;
        var dialog = new ModelDownloadWindow(_modelFolder);
        await dialog.ShowDialog(this);
        _models = OnnxModels.Scan().ToList();
        var keep = IsRemote ? RemoveBgName : SelectedLocalModel?.Name;
        FillModelCombo(dialog.Installed && keep == RemoveBgName ? null : keep);
        OnModelChanged();
    }

    /// <summary>
    /// 從 paint.net 的 Remove Background 插件設定（文件\paint.net App Files\Effects\config.json 的 "api-key"）
    /// 帶入 API Key；沒有那個檔或讀不出來回 null。
    /// </summary>
    private static string? TryImportPaintNetKey()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(docs, "paint.net App Files", "Effects", "config.json");
            if (!File.Exists(path)) return null;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("api-key", out var key) && key.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = key.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch
        {
            // 讀不到就當沒有
        }
        return null;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 沒有預設瀏覽器就算了
        }
    }

    /// <summary>「確定」= 開始跑；跑完自己關窗。回傳 false 讓對話框留著。</summary>
    protected override bool Validate()
    {
        if (_running) return false;

        BackgroundRemovalOptions options;
        var remote = IsRemote;
        if (remote)
        {
            var key = (_apiKeyBox.Text ?? "").Trim();
            if (key.Length == 0)
            {
                _status.Text = "請先填 remove.bg 的 API Key（按右邊「取得 API Key」到官網拿）。";
                _apiKeyBox.Focus();
                return false;
            }
            var preview = _sizeCombo.SelectedIndex == 1;
            var settings = AppSettings.Instance;
            if (settings.RemoveBgApiKey != key || settings.RemoveBgPreview != preview)
            {
                settings.RemoveBgApiKey = key;
                settings.RemoveBgPreview = preview;
                settings.Save();
            }
            options = new BackgroundRemovalOptions
            {
                RemoveBg = new RemoveBgOptions(key, preview ? RemoveBgSize.Preview : RemoveBgSize.Auto),
                SolidCore = _solidCheck.IsChecked == true,
                Contrast = (int)_contrastBar.Value,
                Shift = (int)_shiftBar.Value,
                Selection = _selectionCheck.IsVisible && _selectionCheck.IsChecked == true ? _session.Selection : null,
            };
            _lastModel = RemoveBgName;
            _lastSolid = options.SolidCore;
            _lastContrast = options.Contrast;
            _lastShift = options.Shift;
        }
        else
        {
            if (SelectedLocalModel is not { } model)
            {
                _status.Text = "還沒有本機模型：按「下載模型…」或改用 remove.bg。";
                return false;
            }
            options = new BackgroundRemovalOptions
            {
                Model = model,
                UseGpu = _gpuCheck.IsChecked == true,
                SolidCore = _solidCheck.IsChecked == true,
                Contrast = (int)_contrastBar.Value,
                Shift = (int)_shiftBar.Value,
                Selection = _selectionCheck.IsVisible && _selectionCheck.IsChecked == true ? _session.Selection : null,
            };
            _lastModel = model.Name;
            _lastGpu = options.UseGpu;
            _lastSolid = options.SolidCore;
            _lastContrast = options.Contrast;
            _lastShift = options.Shift;
        }
        _lastSelectionOnly = _selectionCheck.IsChecked == true;
        _running = true;

        _okButton.IsEnabled = false;
        _downloadButton.IsEnabled = false;
        _modelCombo.IsEnabled = _gpuCheck.IsEnabled = _solidCheck.IsEnabled = _selectionCheck.IsEnabled = false;
        _contrastBar.IsEnabled = _shiftBar.IsEnabled = false;
        _apiKeyBox.IsEnabled = _sizeCombo.IsEnabled = false;
        _status.Text = remote ? "上傳到 remove.bg 處理中…" : "處理中…（第一次載入模型會多花幾秒）";

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
                        if (!Applied) Error = options.Selection != null ? "選取範圍內沒有內容" : "圖層沒有內容";
                        else Note = BackgroundRemover.LastPlanNote;
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
