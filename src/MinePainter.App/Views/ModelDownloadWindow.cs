using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MinePainter.Core.AI;

namespace MinePainter.App.Views;

/// <summary>
/// AI 去背 → 下載模型。模型不隨 App 附帶（每個上百 MB），這裡讓使用者一鍵抓進模型資料夾。
///
/// 每個模型都寫清楚「擅長什麼、多快、吃多少記憶體」：不熟模型的人不該被迫去查 ISNet 跟 U2-Net
/// 差在哪；而記憶體那欄尤其重要——BiRefNet 品質最好但一般顯示卡裝不下（見 InferenceBudget）。
/// </summary>
public sealed class ModelDownloadWindow : ModalDialog
{
    private readonly string _directory;
    private readonly List<EntryRow> _rows = new();
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private CancellationTokenSource? _cts;

    /// <summary>這次有沒有裝好任何模型（呼叫端要重新掃資料夾）。</summary>
    public bool Installed { get; private set; }

    public ModelDownloadWindow(string modelDirectory) : base("下載 AI 去背模型", 560)
    {
        _directory = modelDirectory;

        var list = new StackPanel { Spacing = 8 };
        foreach (var entry in ModelCatalog.Entries)
        {
            var row = new EntryRow(entry, this);
            _rows.Add(row);
            list.Children.Add(row.Root);
        }

        var intro = new TextBlock
        {
            Text = "模型不隨程式附帶。以下都來自 rembg 的官方發佈，下載後會驗證檔案完整性，" +
                   "存到「AI 去背模型資料夾」。不確定選哪個就用標了「推薦」的那個。",
            FontSize = 11,
            Foreground = AppTheme.TextMutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                intro,
                new ScrollViewer
                {
                    MaxHeight = 420,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = list,
                },
                _status,
            },
        };

        var close = MakeButton("關閉");
        SetBody(body, ButtonRow(close));
        Closing += (_, _) => _cts?.Cancel();
        RefreshRows();
    }

    private bool Busy => _cts != null;

    private void RefreshRows()
    {
        foreach (var row in _rows) row.Refresh(ModelDownloader.IsInstalled(row.Entry, _directory), Busy);
    }

    private async void Download(EntryRow row)
    {
        if (Busy) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _status.Text = $"正在下載 {row.Entry.Title}（{row.Entry.SizeText}）…";
        row.ShowProgress(0);
        RefreshRows();

        try
        {
            var progress = new Progress<DownloadProgress>(p => row.ShowProgress(p.Fraction));
            await ModelDownloader.DownloadAsync(row.Entry, _directory, progress, ct);
            Installed = true;
            _status.Text = $"{row.Entry.Title} 已安裝。";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消下載。";
        }
        catch (Exception e)
        {
            _status.Text = "下載失敗：" + e.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            row.HideProgress();
            RefreshRows();
        }
    }

    /// <summary>清單裡的一列：說明 ＋ 下載／已安裝／進度。</summary>
    private sealed class EntryRow
    {
        public ModelCatalogEntry Entry { get; }
        public Border Root { get; }

        private readonly ModelDownloadWindow _owner;
        private readonly Button _action = new() { FontSize = 11, Padding = new Thickness(12, 4), Width = 84 };
        private readonly ProgressBar _progress = new()
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            IsVisible = false,
            Foreground = AppTheme.ProgressBrush, // 亮色主題下白條在淺色軌道上看不見
            Background = AppTheme.BarTrackBrush,
        };
        private readonly TextBlock _state = new() { FontSize = 11, Foreground = AppTheme.TextMutedBrush };
        private bool _installed;

        public EntryRow(ModelCatalogEntry entry, ModelDownloadWindow owner)
        {
            Entry = entry;
            _owner = owner;

            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.Title,
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                        Foreground = AppTheme.TextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            if (entry.Recommended) titleRow.Children.Add(Badge("推薦"));
            titleRow.Children.Add(new TextBlock
            {
                Text = entry.SizeText,
                FontSize = 11,
                Foreground = AppTheme.TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var details = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    Detail("品質", entry.Strength),
                    Detail("速度", entry.Speed),
                    Detail("記憶體", entry.Memory),
                },
            };

            _action.Click += (_, _) =>
            {
                if (_owner.Busy) _owner._cts?.Cancel();
                else if (!_installed) _owner.Download(this);
            };

            var right = new StackPanel
            {
                Spacing = 4,
                Width = 92,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _action, _state },
            };
            DockPanel.SetDock(right, Dock.Right);

            Root = new Border
            {
                Background = AppTheme.InnerBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new DockPanel
                        {
                            Children =
                            {
                                right,
                                new StackPanel { Spacing = 4, Children = { titleRow, details } },
                            },
                        },
                        _progress,
                    },
                },
            };
        }

        /// <summary>下載中：顯示進度條與百分比。Progress 會從背景執行緒回報，所以派回 UI 執行緒。</summary>
        public void ShowProgress(double fraction) => Dispatcher.UIThread.Post(() =>
        {
            _progress.IsVisible = true;
            _progress.Value = fraction;
            _state.Text = $"{fraction * 100:0}%";
        });

        public void HideProgress() => _progress.IsVisible = false;

        public void Refresh(bool installed, bool busy)
        {
            _installed = installed;
            var downloading = busy && _progress.IsVisible;
            _action.Content = installed ? "已安裝" : downloading ? "取消" : "下載";
            _action.IsEnabled = !installed && (downloading || !busy);
            if (!downloading) _state.Text = installed ? "可以使用" : "";
        }

        private static Control Detail(string label, string text) => new DockPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Width = 44,
                    Foreground = AppTheme.TextMutedBrush,
                    VerticalAlignment = VerticalAlignment.Top,
                },
                new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    Foreground = AppTheme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        private static Control Badge(string text) => new Border
        {
            Background = AppTheme.AccentBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = Brushes.White },
        };
    }
}
