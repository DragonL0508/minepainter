using Avalonia.Controls;
using Avalonia.Threading;
using MinePainter.Core.AI;
using MinePainter.Core.History;
using MinePainter.Core.Layers;
using MinePainter.Core.Tools;

namespace MinePainter.App.Views;

/// <summary>
/// 圖層 → AI 去背 的進度視窗：一開就送 remove.bg，跑完自己關；只有「取消」可按。
/// 結果直接寫進圖層（一步 undo）。設定（API Key、解析度、後處理）在 設定 → AI 去背。
/// 用模態視窗擋住編輯：命令開始時讀像素、結束時乘遮罩，中間若讓使用者改圖層會對不上。
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

    /// <summary>成功時的補充說明（伺服器回的解析度、扣幾點）；沒有回 null。</summary>
    public string? Note { get; private set; }

    public BackgroundRemovalWindow(EditorSession session, RasterLayer layer, BackgroundRemovalOptions options)
        : base("AI 去背", 360)
    {
        _session = session;
        _layer = layer;
        _options = options;

        _status.Text = (layer.HasActiveEffects || layer.HasElements
            ? "先把本圖層的效果堆疊／文字物件平面化，再上傳到 remove.bg 處理中…"
            : "上傳到 remove.bg 處理中…") +
            (options.Selection != null ? "（只處理選取範圍，範圍外一併清除）" : "");

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
        var started = DateTime.UtcNow;
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
                        if (!Applied) Error = _options.Selection != null ? "選取範圍內沒有內容" : "圖層沒有內容";
                        else Note = BackgroundRemover.LastNote;
                    }
                    Confirmed = Applied;
                    _status.Text = $"完成（{(DateTime.UtcNow - started).TotalSeconds:0.0} 秒）";
                    Close();
                });
            });
    }
}
