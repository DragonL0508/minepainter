using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

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

        // 已經有一個 MinePainter 在跑：把要開的檔交給它（開成新分頁），自己退場
        if (Services.SingleInstance.TryPassToRunning(args)) return;

        // 還是從解壓的 zip 點進來的：交給已安裝的那份跑，自己退場（要在啟動畫面之前，
        // 不然使用者會看到兩次 splash）
        if (Services.AppInstaller.TryHandOff(args)) return;

        // 第一件事就是秀啟動畫面（純 Win32、自己的執行緒），Avalonia 初始化在它後面跑
        Platform.NativeSplash.Show();

        // Skia 的字形遮罩快取預設只有 2 MB —— 一個 120px、帶外光暈／外框的字，光是它自己一幀
        // （光暈、陰影、每層外框、字身，最多八趟）就把 2 MB 塞爆，於是同一幀裡後面幾趟得把
        // 剛做好的遮罩再做一次。實測拉大之後，帶陰影／光暈的字旋轉一幀省下一成多
        // （陰影 2.7→2.3 ms、光暈 4.3→3.7 ms）。上限是「用到才佔」，閒著不吃記憶體。
        SkiaSharp.SKGraphics.SetFontCacheLimit(64 * 1024 * 1024);
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
            .With(Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_NOFALLBACK") == "1"
                ? new FontManagerOptions() // 效能對照：不掛內嵌字型後備
                : new FontManagerOptions
            {
                // 英文版 Windows 可能一支中日韓字型都沒有（Microsoft JhengHei 這類屬 Features on
                // Demand），系統後備找不到字 → UI 中文全是豆腐框。內嵌一支墊底，語系無關。
                // 只當後備不當預設：有系統中文字型的機器維持原本的 Segoe UI 外觀。
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily(Services.EmbeddedFonts.FamilyUri) },
                ],
            })
            .LogToTrace();
}
