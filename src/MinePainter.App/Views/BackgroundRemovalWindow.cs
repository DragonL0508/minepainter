using Avalonia.Controls;
using Avalonia.Threading;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>
/// AI 去背的進度視窗：一開就跑，跑完自己關，只能取消。
/// 模態是為了擋住編輯：命令開始時讀像素、結束時乘遮罩，中間改圖層會對不上。
/// </summary>
public sealed class BackgroundRemovalWindow : ModalDialog
{
    private readonly EditorSession _session;
    private readonly RasterLayer _layer;
    private readonly BackgroundRemovalOptions _options;
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = AppTheme.TextBrush,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    /// <summary>已套用到圖層。</summary>
    public bool Applied { get; private set; }

    /// <summary>失敗訊息（null = 沒失敗或使用者取消）。</summary>
    public string? Error { get; private set; }

    public BackgroundRemovalWindow(EditorSession session, RasterLayer layer, BackgroundRemovalOptions options, string title)
        : base(title, 360)
    {
        _session = session;
        _layer = layer;
        _options = options;

        _status.Text = "處理中…";

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _status,
                new ProgressBar { IsIndeterminate = true, Height = 6 },
            },
        };
        SetBody(body, ButtonRow(MakeButton("取消")), showClose: false);
        Closing += (_, _) => _cts.Cancel();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_started) return;
        _started = true;

        var ct = _cts.Token;
        _ = Task.Run(() => BackgroundRemovalCommand.Run(_session, _layer, _options, ct), ct)
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
                        if (!Applied) Error = "沒有內容";
                    }
                    Confirmed = Applied;
                    Close();
                });
            });
    }
}
