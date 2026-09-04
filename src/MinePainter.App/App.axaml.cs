using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MinePainter.App.Platform;
using MinePainter.App.Views;

namespace MinePainter.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // 畫布排版（Skia）看不到 avares 的內嵌字型，得在任何文字排版之前手動交給 Core
        Services.EmbeddedFonts.Register();

        // MINEPAINTER_DEBUG_FONTCACHE=<MB>：Skia 字形快取上限（效能對照；預設 2MB／2048 個字形）
        if (int.TryParse(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_FONTCACHE"), out var fontCacheMb) && fontCacheMb > 0)
        {
            SkiaSharp.SKGraphics.SetFontCacheLimit(fontCacheMb * 1024L * 1024L);
            SkiaSharp.SKGraphics.SetFontCacheCountLimit(65536);
        }

        // MINEPAINTER_DEBUG_NOANIM=1：拿掉全域微動畫（效能對照用）
        if (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_NOANIM") == "1")
        {
            for (var i = Styles.Count - 1; i >= 0; i--)
            {
                if (Styles[i] is Avalonia.Markup.Xaml.Styling.StyleInclude { Source: { } src } &&
                    src.ToString().EndsWith("Animations.axaml", StringComparison.OrdinalIgnoreCase))
                {
                    Styles.RemoveAt(i);
                }
            }
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 平台已經起來了，這時才能碰 Dispatcher.UIThread（見 CrashLog.InstallUiHandler）
        Services.CrashLog.InstallUiHandler();

        // 主視窗建立前先套使用者設定（主題／畫布背景圖），開起來就是對的樣子
        var settings = Services.AppSettings.Instance;
        AppTheme.Apply(settings.Theme);
        CanvasBackdrop.Set(settings.BackdropPath, settings.BackdropOpacity);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 啟動畫面（Program.Main 一開始就秀出來的）還在跑，這裡放心做重活
            MainWindow main;
            try
            {
                main = new MainWindow(desktop.Args?.FirstOrDefault());
                // 預熱：Show() 的大頭是第一次套模板＋排版與載入初始文件（實測合計 600ms+），
                // 趁啟動畫面還在時先做掉，之後真正 Show 只剩建 OS 視窗與第一幀
                main.PrepareBeforeShow();
            }
            catch
            {
                NativeSplash.Kill();
                throw;
            }

            // 主視窗在啟動畫面「開始退場」時就 Show：建 OS 視窗＋第一幀實測要 300ms 以上，
            // 剛好在 260ms 的退場結束時接上，使用者看到的是 splash 淡出、視窗隨即出現，中間沒有空白。
            // 注意不能在這裡就設 desktop.MainWindow —— lifetime 會在這個方法回傳後立刻 Show 它。
            NativeSplash.RequestFadeOut();
            Services.StartupSounds.LoadingFinished();
            NativeSplash.FadeOutStarted.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                desktop.MainWindow = main;
                main.Show();
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
