using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MinePainter.App.Views;

namespace MinePainter.App;

public partial class App : Application
{
    /// <summary>啟動畫面最短顯示時間：進場動畫要播得完，主視窗建得再快也不閃一下就收。</summary>
    private static readonly TimeSpan SplashMinShow = TimeSpan.FromMilliseconds(1050);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 主視窗建立前先套使用者設定（主題／畫布背景圖），開起來就是對的樣子
        var settings = Services.AppSettings.Instance;
        AppTheme.Apply(settings.Theme);
        CanvasBackdrop.Set(settings.BackdropPath, settings.BackdropOpacity);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            // 開發驗證用：MINEPAINTER_DEBUG_OFFSCREEN=1 時啟動畫面也擺到主螢幕右側之外
            if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OFFSCREEN") == "1" &&
                splash.Screens.Primary is { } primary)
            {
                splash.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual;
                splash.Position = new PixelPoint(primary.Bounds.Right + 40, primary.Bounds.Y + 40);
            }
            splash.Show();

            // 先讓啟動畫面畫出第一幀，再做真正的重活（建主視窗）——
            // Background 優先權排在 Render 之後，所以這裡跑的時候 icon 已經在螢幕上
            Dispatcher.UIThread.Post(() =>
            {
                MainWindow main;
                try
                {
                    main = new MainWindow(desktop.Args?.FirstOrDefault());
                }
                catch
                {
                    splash.Close();
                    throw;
                }
                desktop.MainWindow = main;
                main.Opened += (_, _) =>
                {
                    // 主視窗第一幀畫出來後才收啟動畫面；來不及播完進場就再等一下
                    Dispatcher.UIThread.Post(async () =>
                    {
                        var hold = SplashMinShow;
                        // 開發驗證用：MINEPAINTER_DEBUG_SPLASH_HOLD=<毫秒> 讓啟動畫面停久一點（截圖用）
                        if (int.TryParse(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_SPLASH_HOLD"), out var ms))
                            hold = TimeSpan.FromMilliseconds(ms);
                        var remaining = hold - splash.Elapsed;
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                        await splash.FadeOutAndCloseAsync();
                    }, DispatcherPriority.Background);
                };
                main.Show();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
