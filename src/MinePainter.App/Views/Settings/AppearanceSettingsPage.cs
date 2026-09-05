using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MinePainter.App.Controls;
using MinePainter.App.Services;

namespace MinePainter.App.Views.Settings;

/// <summary>
/// 設定 → 外觀：四種主題（午夜黑／暗色／亮色／極淨白）即點即套用，
/// 加上畫布外圍的背景圖（自選圖片 + 不透明度，預設 10%）。
/// 變更立即生效並記進 AppSettings（設定視窗關窗後 Save）。
/// </summary>
public sealed class AppearanceSettingsPage : SettingsPage
{
    public override string Description => "配色主題與畫布外圍的背景圖。";

    private readonly List<(AppTheme.Palette Palette, Border Card)> _cards = new();
    private readonly TextBlock _backdropLabel = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly BarSlider _opacityBar = new()
    {
        Minimum = 1,
        Maximum = 100,
        Label = "不透明度",
        Suffix = "%",
        Height = 26,
        Width = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public AppearanceSettingsPage()
    {
        var grid = new UniformGrid { Columns = 4, Rows = 1 };
        foreach (var palette in AppTheme.Palettes)
            grid.Children.Add(BuildThemeCard(palette));

        var pickButton = new Button { Content = "選擇圖片…", FontSize = 12, Padding = new Thickness(10, 5) };
        pickButton.Click += OnPickBackdrop;
        var clearButton = new Button { Content = "移除", FontSize = 12, Padding = new Thickness(10, 5) };
        clearButton.Click += (_, _) =>
        {
            CanvasBackdrop.Set(null, (int)_opacityBar.Value);
            AppSettings.Instance.BackdropPath = null;
            UpdateBackdropLabel();
        };

        _opacityBar.Value = AppSettings.Instance.BackdropOpacity;
        _opacityBar.ValueChanged += v =>
        {
            CanvasBackdrop.SetOpacity((int)v); // 即時預覽
            AppSettings.Instance.BackdropOpacity = (int)v;
        };

        Content = SettingsUi.Scroll(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SettingsUi.Section("主題"),
                grid,

                new Separator { Margin = new Thickness(0, 6) },

                SettingsUi.Section("畫布背景圖"),
                SettingsUi.Hint("畫布外圍（灰底那一圈）鋪的圖，只影響畫面、不影響輸出。"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { pickButton, clearButton, _backdropLabel },
                },
                _opacityBar,
            },
        });

        UpdateBackdropLabel();
        HighlightCurrent();
    }

    /// <summary>主題卡片：迷你配色預覽（外框/面板/標題列/文字）+ 名稱。點擊立即套用。</summary>
    private Border BuildThemeCard(AppTheme.Palette p)
    {
        Color C(uint v) => Color.FromUInt32(v);

        var preview = new Border
        {
            Background = new SolidColorBrush(C(p.Chrome)),
            CornerRadius = new CornerRadius(3),
            Height = 46,
            Child = new StackPanel
            {
                Margin = new Thickness(6),
                Spacing = 3,
                Children =
                {
                    new Border { Background = new SolidColorBrush(C(p.Header)), Height = 8, CornerRadius = new CornerRadius(2) },
                    new Border
                    {
                        Background = new SolidColorBrush(C(p.Panel)),
                        Height = 23,
                        CornerRadius = new CornerRadius(2),
                        Padding = new Thickness(5, 4),
                        Child = new Border
                        {
                            Background = new SolidColorBrush(C(p.Text)),
                            Height = 4,
                            Width = 42,
                            CornerRadius = new CornerRadius(2),
                            HorizontalAlignment = HorizontalAlignment.Left,
                        },
                    },
                },
            },
        };

        var card = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6),
            Margin = new Thickness(3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    preview,
                    new TextBlock
                    {
                        Text = p.Name,
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        card.PointerPressed += (_, _) =>
        {
            AppTheme.Apply(p.Id);
            AppSettings.Instance.Theme = p.Id;
            HighlightCurrent();
        };

        _cards.Add((p, card));
        return card;
    }

    private void HighlightCurrent()
    {
        foreach (var (palette, card) in _cards)
        {
            card.BorderBrush = palette.Id == AppTheme.CurrentId ? AppTheme.AccentBrush : Brushes.Transparent;
        }
    }

    private async void OnPickBackdrop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇背景圖",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("影像檔") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;

        if (CanvasBackdrop.Set(path, (int)_opacityBar.Value))
        {
            AppSettings.Instance.BackdropPath = path;
        }
        else
        {
            _backdropLabel.Text = "無法載入這張圖";
            return;
        }
        UpdateBackdropLabel();
    }

    private void UpdateBackdropLabel() =>
        _backdropLabel.Text = CanvasBackdrop.Path is { } p ? System.IO.Path.GetFileName(p) : "（未設定）";
}
