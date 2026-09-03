using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MinePainter.App.Services;

/// <summary>
/// 同時只跑一個 MinePainter（同 paint.net）：在檔案總管點第二張圖時，把路徑丟給
/// 已經開著的那個視窗開成新分頁，而不是再開一個程式。
///
/// 偵測用具名 Mutex，傳遞用具名管線；兩者都是 per-user 命名，所以不同使用者
/// 各跑各的。管線接不上（前一個程序當掉留下的殘骸之類）就照常自己啟動。
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    // 注意：不能用 string.GetHashCode() —— .NET 每個程序的雜湊種子不同，兩邊會算出
    // 不一樣的名字，就永遠偵測不到彼此。要的是跨程序穩定的雜湊。
    // 開發驗證（MINEPAINTER_DEBUG_OFFSCREEN）另起一組名字：驗證用的程序不該被使用者正在跑的那份接走
    private static readonly string Suffix = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.UserName +
                (Environment.GetEnvironmentVariable("MINEPAINTER_DEBUG_OFFSCREEN") is { Length: > 0 } ? "|debug" : ""))))[..8];
    private static string MutexName => "Local\\MinePainter.Instance." + Suffix;
    private static string PipeName => $"MinePainter.Open.{Suffix}";

    /// <summary>
    /// 在 Program.Main 開啟動畫面之前呼叫。回傳 true＝已經有一個 MinePainter 在跑，
    /// 要開的檔案已經交給它了，本程序該直接結束。
    /// </summary>
    public static bool TryPassToRunning(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
            if (first) return false; // 我就是第一個，繼續正常啟動
        }
        catch
        {
            return false; // Mutex 建不起來（權限之類）：照常自己跑
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);

            // 對方是背景程序，沒有這個它 Activate 不到前景
            AllowSetForegroundWindow(ASFW_ANY);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            return true;
        }
        catch
        {
            // 對方還在啟動、或是留下 Mutex 的程序已經不在了：自己跑，別讓使用者開不了
            return false;
        }
    }

    /// <summary>
    /// 主視窗開好後呼叫：開始收其他程序傳來的檔案路徑。
    /// callback 在背景執行緒被呼叫，呼叫端自己切回 UI 執行緒。
    /// </summary>
    public static void StartServer(Action<string[]> onFilesRequested)
    {
        if (!OperatingSystem.IsWindows()) return;

        var thread = new Thread(() => ServerLoop(onFilesRequested))
        {
            IsBackground = true,
            Name = "MinePainter single-instance",
        };
        thread.Start();
    }

    private static void ServerLoop(Action<string[]> onFilesRequested)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                pipe.WaitForConnection();

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var text = reader.ReadToEnd();
                var files = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                onFilesRequested(files);
            }
            catch
            {
                // 單次連線出錯不該讓之後的點擊都失效，繼續等下一個
                Thread.Sleep(200);
            }
        }
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
