using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace MinePainter.App.Services;

/// <summary>GitHub Release 上的一個新版本。</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string AssetName, string DownloadUrl, long Size, string PageUrl);

/// <summary>
/// 應用程式內更新（方案 A：整包覆蓋）：
/// 1. 打 GitHub Releases API 拿最新版，與目前 exe 版本比對；
/// 2. 下載對應的 zip（自含版／框架相依版依目前執行方式選）到 LocalAppData；
/// 3. 解出單檔 MinePainter.exe，寫一支 updater.bat：等本程式結束 → 覆蓋原 exe（OneDrive 鎖檔會重試）→ 重新啟動。
/// exe 執行中不能覆寫自己，所以最後一步一定要交給外部腳本。
/// </summary>
public static class UpdateService
{
    public const string Owner = "DragonL0508";
    public const string Repo = "minepainter";
    private const string ApiLatest = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MinePainter", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>目前版本（publish.bat 以 -p:Version 寫進去；開發建置是 1.0.0）。</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>開發建置（沒經過 publish.bat）：版本號是預設的 1.0.0，啟動時不自動檢查。</summary>
    public static bool IsDevBuild => CurrentVersion.Major == 1 && CurrentVersion.Minor == 0 && CurrentVersion.Build == 0;

    /// <summary>目前這份 exe 的路徑（單檔發佈：就是那一個檔）。</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>框架相依版跑在 dotnet 共用 runtime 上；自含版的 runtime 目錄在自己（或解壓）資料夾裡。</summary>
    public static bool IsFrameworkDependent
    {
        get
        {
            var rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            return rt.Contains(Path.Combine("dotnet", "shared"), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindows() && ExePath != null;

    /// <summary>查最新版；沒有更新（或查不到）回 null。網路錯誤往外拋。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(ApiLatest, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;
        if (version <= CurrentVersion) return null;

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        var wanted = IsFrameworkDependent ? "framework-dependent.zip" : "win-x64.zip";
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            return new UpdateInfo(version, tag, notes, name, url, size, page);
        }
        return null;
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(t, out var v))
        {
            // 補齊成 x.y.z.0，才能跟 AssemblyVersion（四段）比
            version = new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), 0);
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    private static string UpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MinePainter", "update");

    /// <summary>
    /// 下載 zip、解出新 exe、寫好 updater 腳本。回傳腳本路徑；由呼叫端在程式真的要關閉時啟動它。
    /// </summary>
    public static async Task<string> PrepareAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("只支援 Windows 的發佈版。");
        var exe = ExePath!;
        Directory.CreateDirectory(UpdateDir);
        var zipPath = Path.Combine(UpdateDir, info.AssetName);
        var newExe = Path.Combine(UpdateDir, "MinePainter.new.exe");
        var script = Path.Combine(UpdateDir, "apply-update.bat");

        // 下載（帶進度）
        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(0.95, (double)done / total));
            }
        }

        // 解出 exe（zip 裡是 MinePainter-x.y.z-.../MinePainter.exe 一個檔）
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("MinePainter.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("更新包裡找不到 MinePainter.exe。");
            if (File.Exists(newExe)) File.Delete(newExe);
            entry.ExtractToFile(newExe);
        }
        File.Delete(zipPath);
        progress.Report(1);

        // updater：等本程式結束 → 覆蓋（重試 60 次，OneDrive／防毒可能短暫鎖檔）→ 重啟 → 自刪
        var pid = Environment.ProcessId;
        var bat = $"""
            @echo off
            title MinePainter update
            :wait
            rem full paths: a Git-for-Windows "find" earlier on PATH would shadow the Windows one
            "%SystemRoot%\System32\tasklist.exe" /FI "PID eq {pid}" 2>nul | "%SystemRoot%\System32\find.exe" "{pid}" >nul
            if not errorlevel 1 (
                "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                goto wait
            )
            set n=0
            :copy
            copy /y "{newExe}" "{exe}" >nul 2>&1
            if not errorlevel 1 goto ok
            set /a n+=1
            if %n% lss 60 (
                "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                goto copy
            )
            echo Could not replace "{exe}".
            echo The new version is at "{newExe}" - copy it over by hand.
            pause
            exit /b 1
            :ok
            del /q "{newExe}" >nul 2>&1
            start "" "{exe}"
            del /q "%~f0" >nul 2>&1
            """;
        await File.WriteAllTextAsync(script, bat, BatchEncoding(), ct);
        return script;
    }

    /// <summary>cmd 讀 .bat 用 OEM 字碼頁（繁中 Windows 是 950）；路徑含中文時得用它寫，不然 copy 找不到檔。</summary>
    internal static System.Text.Encoding BatchEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return System.Text.Encoding.Default;
        }
    }

    /// <summary>啟動 updater 腳本（在程式關閉的最後一步呼叫）。</summary>
    public static void Launch(string script)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{script}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script),
        });
    }
}
