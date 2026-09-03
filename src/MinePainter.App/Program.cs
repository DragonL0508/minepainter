using Avalonia;

namespace MinePainter.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 控制台的「解除安裝」會帶這個旗標進來：清完就走，不要開視窗
        if (args.Contains(Services.AppInstaller.UninstallFlag))
        {
            Services.AppInstaller.Uninstall();
            return;
        }

        // 還是從解壓的 zip 點進來的：交給已安裝的那份跑，自己退場（要在啟動畫面之前，
        // 不然使用者會看到兩次 splash）
        if (Services.AppInstaller.TryHandOff(args)) return;

        // 第一件事就是秀啟動畫面（純 Win32、自己的執行緒），Avalonia 初始化在它後面跑
        Platform.NativeSplash.Show();
        Services.StartupSounds.SplashShown();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions
            {
                // 預設 8MB 會讓大量 tile 貼圖每幀重新上傳（cache thrash）；
                // tile 常駐 GPU 是上屏效能的前提。
                MaxGpuResourceSizeBytes = 512L * 1024 * 1024,
            })
            .LogToTrace();
}
