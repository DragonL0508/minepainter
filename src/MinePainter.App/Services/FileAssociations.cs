using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// MinePainter 只出 Windows 版（啟動畫面就是純 Win32），登錄檔 API 的平台警告在這裡沒有意義
#pragma warning disable CA1416

namespace MinePainter.App.Services;

/// <summary>
/// Windows 檔案關聯（只寫 HKCU，不需要系統管理員）。
///
/// Windows 10/11 不讓程式自己把自己設成預設開啟程式（UserChoice 有簽章保護），
/// 所以這裡能做的是「登記」：註冊 ProgID 與 Capabilities 之後，MinePainter 才會
/// 出現在「開啟方式」清單與「設定 → 預設應用程式」裡，使用者按兩下就能指定。
/// <see cref="OpenWindowsDefaultAppsSettings"/> 會直接跳到 MinePainter 那一頁。
/// </summary>
public static class FileAssociations
{
    /// <summary>一種可關聯的副檔名與它在檔案總管顯示的名稱。</summary>
    public readonly record struct Kind(string Extension, string Description);

    /// <summary>MinePainter 開得起來的所有格式（順序＝對話框的顯示順序）。</summary>
    public static readonly Kind[] All =
    [
        new(".png", "PNG 影像"),
        new(".jpg", "JPEG 影像"),
        new(".jpeg", "JPEG 影像"),
        new(".bmp", "BMP 影像"),
        new(".gif", "GIF 影像"),
        new(".webp", "WebP 影像"),
        new(".mpp", "MinePainter 專案"),
        new(".pdn", "paint.net 專案"),
    ];

    private const string AppName = "MinePainter";
    private const string CapabilitiesPath = @"Software\MinePainter\Capabilities";
    private const string AppExeKey = @"Software\Classes\Applications\MinePainter.exe";

    /// <summary>目前執行檔的完整路徑（單一檔案發佈也拿得到）。</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    /// <summary>
    /// 關聯要指向的執行檔：裝過就指安裝位置（使用者不會去搬那裡），沒裝才指現在這份。
    /// 兩份同時存在時也因此不會互搶 —— 它們算出來的目標是同一個。
    /// </summary>
    public static string TargetExe =>
        AppInstaller.IsInstalled ? AppInstaller.InstalledExe : ExePath;

    private static string ProgId(string ext) => "MinePainter" + ext;

    /// <summary>這個副檔名有沒有登記過（不代表它就是預設程式）。</summary>
    public static bool IsRegistered(string ext) =>
        CommandOf(ext) is not null;

    /// <summary>登記的路徑是不是還指向該指的執行檔（搬過位置就會是 false）。</summary>
    public static bool IsStale(string ext) =>
        CommandOf(ext) is { } cmd && !cmd.Contains(TargetExe, StringComparison.OrdinalIgnoreCase);

    private static string? CommandOf(string ext)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\{ProgId(ext)}\shell\open\command");
        return key?.GetValue(null) as string;
    }

    /// <summary>把指定副檔名登記給 MinePainter；沒列到的副檔名會被取消登記。</summary>
    public static void Apply(IReadOnlyCollection<string> extensions)
    {
        var exe = TargetExe;
        if (string.IsNullOrEmpty(exe)) return;

        foreach (var kind in All)
        {
            if (extensions.Contains(kind.Extension)) Register(kind, exe);
            else Unregister(kind);
        }

        if (extensions.Count > 0) WriteCapabilities(extensions, exe);
        else RemoveCapabilities();

        NotifyShell();
    }

    /// <summary>全部取消登記，登錄檔回到沒裝過 MinePainter 的樣子。</summary>
    public static void RemoveAll() => Apply([]);

    private static void Register(Kind kind, string exe)
    {
        var progId = ProgId(kind.Extension);

        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
        {
            key.SetValue(null, kind.Description);
            key.SetValue("FriendlyTypeName", kind.Description);
            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = key.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 「開啟方式」清單要看得到 MinePainter，靠的是這兩處
        using (var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\{kind.Extension}\OpenWithProgids"))
        {
            key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(AppExeKey))
        {
            key.SetValue("FriendlyAppName", AppName);
            using (var cmd = key.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            using (var types = key.CreateSubKey("SupportedTypes"))
                types.SetValue(kind.Extension, "");
        }
    }

    private static void Unregister(Kind kind)
    {
        var progId = ProgId(kind.Extension);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);

        using (var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\{kind.Extension}\OpenWithProgids", writable: true))
        {
            key?.DeleteValue(progId, throwOnMissingValue: false);
        }

        using (var key = Registry.CurrentUser.OpenSubKey($@"{AppExeKey}\SupportedTypes", writable: true))
        {
            key?.DeleteValue(kind.Extension, throwOnMissingValue: false);
        }
    }

    private static void WriteCapabilities(IReadOnlyCollection<string> extensions, string exe)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            key.SetValue("ApplicationName", AppName);
            key.SetValue("ApplicationDescription", "MinePainter 影像編輯器");
            key.SetValue("ApplicationIcon", $"\"{exe}\",0");
            // 下次啟動靠這兩筆判斷「登記的是不是還是我、是不是比我新」
            key.SetValue("InstalledPath", exe);
            key.SetValue("InstalledVersion", UpdateService.CurrentVersion.ToString());
            using var assoc = key.CreateSubKey("FileAssociations");
            foreach (var name in assoc.GetValueNames()) assoc.DeleteValue(name, false);
            foreach (var ext in extensions) assoc.SetValue(ext, ProgId(ext));
        }

        // 有這一筆，「設定 → 預設應用程式」才找得到 MinePainter（深層連結也才有效）
        using var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        reg.SetValue(AppName, CapabilitiesPath);
    }

    private static void RemoveCapabilities()
    {
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            reg?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\MinePainter", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(AppExeKey, throwOnMissingSubKey: false);
    }

    /// <summary>目前登記給 MinePainter 的副檔名（沒登記就是空的）。</summary>
    public static List<string> RegisteredExtensions() =>
        All.Where(k => IsRegistered(k.Extension)).Select(k => k.Extension).ToList();

    private static T? Capability<T>(string name) where T : class
    {
        using var key = Registry.CurrentUser.OpenSubKey(CapabilitiesPath);
        return key?.GetValue(name) as T;
    }

    /// <summary>
    /// 啟動時跑一次（背景執行緒）：第一次執行自動登記全部格式；之後只在
    /// 登記的路徑不是現在這個執行檔時把它改過來（例如使用者把 MinePainter 換了資料夾，
    /// 或裝了新版）。回傳 true＝這次有寫登錄檔（呼叫端要存 settings）。
    ///
    /// 「自動登記」只是讓 MinePainter 出現在「開啟方式」與預設應用程式清單，
    /// 不會動到使用者現有的預設程式 —— 搶預設 Windows 本來就不允許。
    /// </summary>
    public static bool EnsureUpToDate(bool optedOut, bool autoRegisteredBefore)
    {
        // 開發建置會指到 bin\Debug 底下的 exe，別讓它污染使用者的關聯
        if (optedOut || UpdateService.IsDevBuild) return false;

        var exe = TargetExe;
        if (string.IsNullOrEmpty(exe)) return false;

        var registered = RegisteredExtensions();
        if (registered.Count == 0)
        {
            // 曾經自動登記過又變成沒有＝使用者自己清掉了，別再塞回去
            if (autoRegisteredBefore) return false;
            Apply(All.Select(k => k.Extension).ToList());
            return true;
        }

        var path = Capability<string>("InstalledPath");
        if (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) return false;

        // 都沒安裝、只是解了好幾份 zip 的情況：更新的那份還在原地就不搶
        // （有安裝的話 TargetExe 兩邊算出來一樣，根本走不到這裡）
        if (!AppInstaller.IsInstalled &&
            Version.TryParse(Capability<string>("InstalledVersion"), out var registeredVersion) &&
            registeredVersion > UpdateService.CurrentVersion &&
            path is not null && File.Exists(path))
        {
            return false;
        }

        // 格式維持使用者原本勾的那組，只把路徑換成現在這個執行檔
        Apply(registered);
        return true;
    }

    /// <summary>跳到「設定 → 預設應用程式 → MinePainter」，使用者在那裡指定預設。</summary>
    public static void OpenWindowsDefaultAppsSettings()
    {
        var uri = $"ms-settings:defaultapps?registeredAppName={Uri.EscapeDataString(AppName)}";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 深層連結在某些版本會被擋掉，退回預設應用程式主頁
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            }
            catch { /* 沒有設定頁就算了 */ }
        }
    }

    /// <summary>通知檔案總管關聯變了，不用登出重進就會更新圖示／「開啟方式」。</summary>
    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
