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
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
