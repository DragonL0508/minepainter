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

    private static string ProgId(string ext) => "MinePainter" + ext;

    /// <summary>這個副檔名有沒有登記過（不代表它就是預設程式）。</summary>
    public static bool IsRegistered(string ext) =>
        CommandOf(ext) is not null;

    /// <summary>登記的路徑是不是還指向現在這個執行檔（搬過位置就會是 false）。</summary>
    public static bool IsStale(string ext) =>
        CommandOf(ext) is { } cmd && !cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase);

    private static string? CommandOf(string ext)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\{ProgId(ext)}\shell\open\command");
        return key?.GetValue(null) as string;
    }

    /// <summary>把指定副檔名登記給 MinePainter；沒列到的副檔名會被取消登記。</summary>
    public static void Apply(IReadOnlyCollection<string> extensions)
    {
        var exe = ExePath;
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
