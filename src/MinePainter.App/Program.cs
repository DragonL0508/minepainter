using Avalonia;

namespace MinePainter.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
