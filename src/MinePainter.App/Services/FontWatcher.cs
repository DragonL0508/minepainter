using Avalonia.Threading;
using MinePainter.Core.Vectors;

namespace MinePainter.App.Services;

/// <summary>
/// 監看 Windows 的字型資料夾（全機 <c>%WINDIR%\Fonts</c>、個人 <c>%LocalAppData%\Microsoft\Windows\Fonts</c>），
/// 程式跑著時新裝的字型檔登記到 <see cref="ExtraFonts"/>，字型清單跟著更新 —— 不用重開程式。
/// Windows 安裝字型是先複製檔案再登記，檔案剛出現時可能還鎖著，讀不到就下一輪再試（最多幾次）。
/// </summary>
public static class FontWatcher
{
    private static readonly List<FileSystemWatcher> Watchers = [];
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> Retries = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _debounce;
    private static bool _started;

    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc"];

    public static void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var dir in FontDirectories())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir)) Known.Add(file);   // 啟動時就有的：系統自己認得
                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += (_, _) => Schedule();
                watcher.Renamed += (_, _) => Schedule();
                watcher.Changed += (_, _) => Schedule();
                Watchers.Add(watcher);
            }
            catch (Exception)
            {
                // 資料夾不存在或沒權限：這個目錄就不看
            }
        }
    }

    private static IEnumerable<string> FontDirectories()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(system) && Directory.Exists(system)) yield return system;
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        if (Directory.Exists(user)) yield return user;
    }

    /// <summary>事件在背景執行緒上來、而且一次安裝會來好幾個：合併成 1 秒後在 UI 執行緒掃一次。</summary>
    private static void Schedule()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _debounce.Tick -= Rescan;
            _debounce.Tick += Rescan;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private static void Rescan(object? sender, EventArgs e)
    {
        _debounce!.Stop();
        var registered = false;
        var retryLater = false;
        foreach (var dir in FontDirectories())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                if (Known.Contains(file)) continue;
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) { Known.Add(file); continue; }
                if (!IsReadable(file) || !ExtraFonts.Register(file))
                {
                    // 還在複製／鎖著：留到下一輪；試太多次就當它壞了
                    var tries = Retries.GetValueOrDefault(file) + 1;
                    Retries[file] = tries;
                    if (tries >= 5) Known.Add(file);
                    else retryLater = true;
                    continue;
                }
                Known.Add(file);
                registered = true;
            }
        }
        if (registered) FontCatalog.Invalidate();
        if (retryLater) _debounce.Start();
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
