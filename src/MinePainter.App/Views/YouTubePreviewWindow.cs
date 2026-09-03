using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MinePainter.App.Controls;
using MinePainter.App.Gadgets;

namespace MinePainter.App.Views;

/// <summary>
/// 小工具「YouTube 縮圖預覽」的參數視窗：填影片資訊與版面，確定後由 MainWindow
/// 產生本機網頁並丟給瀏覽器開。結果一律在關閉時拍成純值（產生網頁在背景執行緒跑）。
/// </summary>
public sealed class YouTubePreviewWindow : ModalDialog
{
    // App 存活期間記住上次填的東西；標題例外，它跟著文件走
    private static string _lastChannel = "MinePainter";
    private static double _lastViews = 12345;
    private static int _lastUploaded = 2;
    private static string _lastDuration = "10:32";
    private static int _lastTheme;
    private static int _lastFit;
    private static bool _lastAvatar;

    private static readonly string[] UploadedOptions =
        ["剛剛", "3 小時前", "1 天前", "5 天前", "2 週前", "1 個月前", "1 年前"];

    private readonly TextBox _titleBox = new() { FontSize = 12 };
    private readonly TextBox _channelBox = new() { FontSize = 12 };
    private readonly NumberBox _viewsBox = new()
    {
        Minimum = 0,
        Maximum = 999_999_999,
        AdaptiveStep = true,
        Width = 110,
    };
    private readonly TextBlock _viewsHint = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ComboBox _uploadedCombo = Combo(UploadedOptions);
    private readonly TextBox _durationBox = new() { FontSize = 12, Width = 70 };
    private readonly ComboBox _themeCombo = Combo(["深色", "淺色"]);
    private readonly ComboBox _fitCombo = Combo(["裁切填滿 16:9", "完整顯示（留黑邊）"]);
    private readonly CheckBox _avatarCheck = new() { Content = "頻道頭像也用這張圖", FontSize = 12 };

    public YouTubeMockupOptions Options { get; private set; } = new();

    public YouTubePreviewWindow(string suggestedTitle) : base("YouTube 縮圖預覽", 380)
    {
        _titleBox.Text = suggestedTitle;
        _channelBox.Text = _lastChannel;
        _viewsBox.Value = _lastViews;
        _uploadedCombo.SelectedIndex = _lastUploaded;
        _durationBox.Text = _lastDuration;
        _themeCombo.SelectedIndex = _lastTheme;
        _fitCombo.SelectedIndex = _lastFit;
        _avatarCheck.IsChecked = _lastAvatar;

        _viewsBox.ValueChanged += _ => SyncViewsHint();
        SyncViewsHint();

        var viewsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _viewsBox, _viewsHint },
        };
        var lengthRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _durationBox, _uploadedCombo },
        };

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                LabeledRow("標題", _titleBox),
                LabeledRow("頻道", _channelBox),
                LabeledRow("觀看數", viewsRow),
                LabeledRow("長度", lengthRow),
                new Separator { Margin = new Thickness(0, 3) },
                LabeledRow("主題", _themeCombo),
                LabeledRow("縮圖", _fitCombo),
                _avatarCheck,
            },
        };

        SetBody(body, ButtonRow(
            MakeButton("開啟預覽", primary: true, confirm: true),
            MakeButton("取消")));

        Closed += (_, _) =>
        {
            Options = new YouTubeMockupOptions
            {
                Title = Trimmed(_titleBox.Text, "未命名影片"),
                Channel = Trimmed(_channelBox.Text, "MinePainter"),
                Views = (long)_viewsBox.Value,
                Uploaded = UploadedOptions[Math.Max(0, _uploadedCombo.SelectedIndex)],
                Duration = Trimmed(_durationBox.Text, "10:32"),
                Dark = _themeCombo.SelectedIndex == 0,
                Cover = _fitCombo.SelectedIndex == 0,
                AvatarFromImage = _avatarCheck.IsChecked == true,
            };
            if (!Confirmed) return;
            _lastChannel = Options.Channel;
            _lastViews = _viewsBox.Value;
            _lastUploaded = _uploadedCombo.SelectedIndex;
            _lastDuration = Options.Duration;
            _lastTheme = _themeCombo.SelectedIndex;
            _lastFit = _fitCombo.SelectedIndex;
            _lastAvatar = Options.AvatarFromImage;
        };

        Opened += (_, _) =>
        {
            _titleBox.Focus();
            _titleBox.SelectAll();
        };
    }

    private void SyncViewsHint() => _viewsHint.Text = YouTubeMockup.FormatViews((long)_viewsBox.Value);

    private static string Trimmed(string? text, string fallback) =>
        text?.Trim() is { Length: > 0 } value ? value : fallback;

    private static ComboBox Combo(string[] items)
    {
        var combo = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = 0;
        return combo;
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
