using System.Text;
using Avalonia.Threading;

namespace MinePainter.App.Services;

/// <summary>
/// 最後一道防線：沒人接的例外先寫進紀錄檔，UI 執行緒上的還會就地攔下來，不讓整個 app 無聲消失。
///
/// 起因是使用者 2026-09-04 回報「滑到某個選單項目、還沒點就崩」：當時沒有任何紀錄，
/// 只能靠猜。現在同樣的狀況會留下堆疊，而且視窗還在，使用者不會丟掉正在畫的東西。
/// </summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinePainter", "crash.log");

    /// <summary>攔到的最後一個例外訊息（給 UI 提示用）。</summary>
    public static event Action<string>? Caught;

    /// <summary>行程層級的攔截（可以在 Avalonia 起來之前就裝）。攔不下來，但至少留下堆疊。</summary>
    public static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// UI 執行緒的攔截：記下來並標成已處理 —— 一個工具提示開不起來不該把整份未存檔的圖賠掉。
    ///
    /// 一定要等 Avalonia 平台初始化之後才裝：提早碰 <c>Dispatcher.UIThread</c> 會讓它綁到還沒有
    /// 平台實作的狀態，之後 <c>Dispatcher.MainLoop</c> 直接丟 PlatformNotSupportedException
    /// （在 Program.Main 裝的第一版就是這樣，app 根本開不起來）。
    /// </summary>
    public static void InstallUiHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("UI", e.Exception);
            e.Handled = true;
            Caught?.Invoke(e.Exception.Message);
        };
    }

    private static void Write(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
            var text = new StringBuilder()
                .AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] v{version} ----")
                .AppendLine(ex.ToString())
                .ToString();
            System.IO.File.AppendAllText(Path, text);
        }
        catch
        {
            // 連記錄都寫不出來就算了，總不能在例外處理裡再炸一次
        }
    }
}
