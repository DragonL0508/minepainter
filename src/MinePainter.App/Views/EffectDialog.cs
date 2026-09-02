using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MinePainter.App.Controls;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using IEffect = MinePainter.Core.Effects.IEffect;

namespace MinePainter.App.Views;

/// <summary>
/// 效果／調整對話框（paint.net 式）：參數一動就在畫布上即時預覽，
/// 確定才進 history、取消把圖層還原（還原由呼叫端透過 EffectSession 做）。
/// 預覽在背景執行緒跑，參數連續變動時取消前一次、最多再排一次。
/// </summary>
public sealed class EffectDialog : ModalDialog
{
    private readonly IEffectPreviewTarget _fx;
    private readonly ParamEditor _editor;
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = AppTheme.TextMutedBrush,
        Text = "",
        MinHeight = 16,
    };
    private readonly Button _ok;
    private readonly Button _cancel;

    private CancellationTokenSource? _cts;
    private Task _renderTask = Task.CompletedTask;
    private bool _pending;
    private bool _finishing;

    /// <summary>最後一次套用的效果（含使用者調好的參數）。</summary>
    public IEffect Result => (IEffect)_editor.Current;

    public EffectDialog(IEffectPreviewTarget fx, IEffect effect, string title) : base(title, 360)
    {
        _fx = fx;

        long[]? histogram = null;
        if (effect is AdjustmentEffect { Adjustment: LevelsAdjustment or CurvesAdjustment })
            histogram = fx.Histogram();

        Avalonia.Media.Imaging.Bitmap? thumbnail = null;
        if (effect.Parameters.Any(d => d is PointParam))
        {
            using var thumb = fx.RenderThumbnail(320);
            using var image = SkiaSharp.SKImage.FromBitmap(thumb);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream(data.ToArray());
            thumbnail = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        _editor = new ParamEditor(effect, o => ((IEffect)o).Parameters, histogram) { Thumbnail = thumbnail };
        _editor.SetTarget(effect); // Thumbnail 設定後重建，選點器才有底圖
        _editor.Changed += _ => SchedulePreview();

        var body = new StackPanel { Spacing = 8 };
        if (histogram != null && effect is AdjustmentEffect { Adjustment: LevelsAdjustment })
            body.Children.Add(new HistogramView { Data = histogram });
        body.Children.Add(_editor);
        body.Children.Add(_status);

        _ok = new Button { Content = "確定", Padding = new Thickness(14, 6), FontSize = 12 };
        _ok.Classes.Add("accent");
        _ok.Click += (_, _) => _ = FinishAsync();
        _cancel = new Button { Content = "取消", Padding = new Thickness(14, 6), FontSize = 12 };
        _cancel.Click += (_, _) => Close();
        SetBody(body, ButtonRow(_ok, _cancel));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SchedulePreview(); // 預設參數也先套上，一開窗就看得到效果
    }

    protected override void OnConfirmKey() => _ = FinishAsync();

    private void SchedulePreview()
    {
        if (_finishing) return;
        if (!_renderTask.IsCompleted)
        {
            _pending = true;
            _cts?.Cancel();
            return;
        }
        StartRender();
    }

    private void StartRender()
    {
        _pending = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var effect = Result;
        _status.Text = "計算中…";

        _renderTask = Task.Run(() => _fx.Preview(effect, token), token);
        _renderTask.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.IsFaulted && !token.IsCancellationRequested)
                _status.Text = $"錯誤：{t.Exception?.InnerException?.Message}";
            else
                _status.Text = "";

            if (_pending) StartRender();
            else if (_finishing) CloseConfirmed();
        }));
    }

    private async Task FinishAsync()
    {
        if (_finishing) return;
        _finishing = true;
        _ok.IsEnabled = false;
        _cancel.IsEnabled = false;

        // 最後一次參數還沒套上（或還在算）就等它算完再關
        if (!_renderTask.IsCompleted)
        {
            _status.Text = "套用中…";
            return; // ContinueWith 會呼叫 CloseConfirmed
        }
        if (_pending)
        {
            StartRender();
            return;
        }
        await Task.Yield();
        CloseConfirmed();
    }

    private void CloseConfirmed()
    {
        Confirmed = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return; // 退場動畫中，之後會再進來一次
        _cts?.Cancel();
    }

    /// <summary>關窗後由呼叫端等待：確保背景渲染已停（避免取消還原後又被寫回）。</summary>
    public async Task WaitIdleAsync()
    {
        try
        {
            await _renderTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }
}
