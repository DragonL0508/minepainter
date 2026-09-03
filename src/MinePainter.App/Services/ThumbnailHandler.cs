using System.Reflection;
using Microsoft.Win32;

// MinePainter 只出 Windows 版；登錄檔 API 的平台警告在這裡沒有意義
#pragma warning disable CA1416

namespace MinePainter.App.Services;

/// <summary>
/// 檔案總管的 .mpp 縮圖：把嵌在 exe 裡的原生 DLL（MinePainter.Thumbnails，NativeAOT
/// 編出來的 COM 伺服器）寫到安裝資料夾並註冊給 .mpp。
///
/// 為什麼是獨立的 DLL：縮圖處理常式會被檔案總管載進它自己的 COM 代理程序，
/// 不能是我們這支單一檔案的 exe，也不能依賴 .NET 執行階段（使用者不見得有裝）。
/// </summary>
public static class ThumbnailHandler
{
    /// <summary>DLL 裡寫死的 CLSID，兩邊要一致（MppThumbnailProvider.Clsid）。</summary>
    private const string Clsid = "{f2ac8991-f45f-40c9-aab0-8768c822eec4}";

    /// <summary>外殼的縮圖處理常式介面（IThumbnailProvider）。</summary>
    private const string ThumbnailProviderIid = "{e357fccd-a995-4576-b01f-234630154e96}";

    private const string ResourceName = "MinePainterThumbs.dll";
    private const string FileName = "MinePainterThumbs.dll";

    private static string ClsidKey => $@"Software\Classes\CLSID\{Clsid}";

    /// <summary>開發建置沒有嵌這個資源，整個功能就靜靜跳過。</summary>
    public static bool IsAvailable => Assembly.GetExecutingAssembly()
        .GetManifestResourceNames().Contains(ResourceName);

    /// <summary>把 DLL 寫進安裝資料夾並註冊。已經是同一版就只補登錄檔。</summary>
    public static void Install(string installDir)
    {
        var payload = ReadEmbedded();
        if (payload is null) return;

        var dll = Path.Combine(installDir, FileName);
        Directory.CreateDirectory(installDir);

        // 檔案總管的 dllhost 可能正抓著舊的 DLL：那就沿用舊檔，下次啟動再換
        // （大小一樣就當作同一份，省下每次啟動都覆寫 2MB）
        if (!File.Exists(dll) || new FileInfo(dll).Length != payload.Length)
        {
            try
            {
                File.WriteAllBytes(dll, payload);
            }
            catch (IOException)
            {
                if (!File.Exists(dll)) return;
            }
            catch (UnauthorizedAccessException)
            {
                if (!File.Exists(dll)) return;
            }
        }

        using (var key = Registry.CurrentUser.CreateSubKey(ClsidKey))
        {
            key.SetValue(null, "MinePainter .mpp 縮圖");
            using var inproc = key.CreateSubKey("InprocServer32");
            inproc.SetValue(null, dll);
            inproc.SetValue("ThreadingModel", "Both");
        }

        // 副檔名與 ProgID 兩邊都掛：使用者把預設程式換來換去也還看得到縮圖
        foreach (var owner in new[] { @"Software\Classes\.mpp", @"Software\Classes\MinePainter.mpp" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{owner}\ShellEx\{ThumbnailProviderIid}");
            key.SetValue(null, Clsid);
        }
    }

    /// <summary>解除註冊（DLL 本身跟著安裝資料夾一起被刪）。</summary>
    public static void Uninstall()
    {
        foreach (var owner in new[] { @"Software\Classes\.mpp", @"Software\Classes\MinePainter.mpp" })
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{owner}\ShellEx", throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
    }

    private static byte[]? ReadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
