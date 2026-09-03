using System.Diagnostics;
using Microsoft.Win32;
using MinePainter.App.Platform;

// MinePainter 只出 Windows 版；登錄檔 API 的平台警告在這裡沒有意義
#pragma warning disable CA1416

namespace MinePainter.App.Services;

/// <summary>
/// 把 MinePainter 安裝到一個「使用者不會去搬」的固定位置
/// （%LocalAppData%\Programs\MinePainter），檔案關聯才有穩定的落點 ——
/// 一般應用程式靠安裝程式解掉的就是這件事，綠色 zip 沒有安裝程式，所以自己來。
///
/// 全部 per-user，不需要系統管理員；同時建開始功能表捷徑、App Paths
/// （Win+R 打 minepainter）與控制台的移除項目，移除時關聯一起清掉不留死路徑。
/// zip 直接跑（不安裝）照樣能用，只是關聯會指向 zip 解開的位置。
/// </summary>
public static class AppInstaller
{
    private const string AppName = "MinePainter";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MinePainter";
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\MinePainter.exe";

    /// <summary>解除安裝用的命令列旗標（由控制台的「解除安裝」帶進來）。</summary>
    public const string UninstallFlag = "--uninstall";

    /// <summary>安裝目的地資料夾。</summary>
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppName);

    /// <summary>安裝後的執行檔路徑（檔案關聯就指這裡）。</summary>
    public static string InstalledExe => Path.Combine(InstallDir, "MinePainter.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "MinePainter.lnk");

    /// <summary>本機已經有安裝好的一份。</summary>
    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>現在跑的就是安裝好的那一份。</summary>
    public static bool IsRunningInstalled =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>已安裝那一份的版本（沒安裝＝null）。</summary>
    public static Version? InstalledVersion
    {
        get
        {
            if (!IsInstalled) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion;
                return Version.TryParse(v, out var parsed) ? parsed : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 交棒：使用者還是從解壓的 zip 資料夾點 exe 時，直接把已安裝的那份叫起來、自己退場，
    /// 那個 exe 等於自動變成捷徑。不刪使用者的檔案 —— 那是他自己下載的東西，而且
    /// 「複製自己到 AppData 再刪掉原檔」是 dropper 的典型樣態，沒必要惹防毒。
    ///
    /// 在 Program.Main 開啟動畫面之前呼叫；回傳 true＝已經交棒，本程序該直接結束。
    /// </summary>
    public static bool TryHandOff(string[] args)
    {
        // 交棒過來的那份自己就是安裝版，不會再往下傳（這個環境變數只是多一道保險）
        if (Environment.GetEnvironmentVariable(HandoffEnv) == "1") return false;
        if (UpdateService.IsDevBuild || !UpdateService.IsSupported) return false;
        if (IsRunningInstalled) return false;

        // 我比較新：這次照常跑，等視窗開起來再把安裝的那份覆蓋成新版，下次才交棒
        var installed = InstalledVersion;
        if (installed is null || installed < UpdateService.CurrentVersion) return false;

        try
        {
            var psi = new ProcessStartInfo(InstalledExe) { WorkingDirectory = InstallDir };
            foreach (var a in args) psi.ArgumentList.Add(a); // 帶著要開的檔一起交棒
            psi.Environment[HandoffEnv] = "1";
            return Process.Start(psi) is not null;
        }
        catch
        {
            return false; // 叫不起來（檔案壞了之類）就自己跑，不要讓使用者開不了
        }
    }

    private const string HandoffEnv = "MINEPAINTER_HANDOFF";

    /// <summary>
    /// 啟動時跑一次（背景執行緒）：還沒安裝就裝，已安裝但比現在這份舊就更新它。
    /// 回傳 true＝這次真的複製了檔案（呼叫端拿去提示使用者）。
    /// </summary>
    public static bool EnsureInstalled()
    {
        // 開發建置（bin\Debug，還帶一堆 DLL）不是單檔，複製過去也跑不起來
        if (UpdateService.IsDevBuild || !UpdateService.IsSupported) return false;
        if (IsRunningInstalled)
        {
            // 自己就是安裝好的那份：把捷徑／登錄檔補齊（使用者可能刪過捷徑）
            WriteShellEntries();
            return false;
        }

        var installed = InstalledVersion;
        // 已經有一份，而且不比現在這份舊：不用動（舊版被跑起來時不要覆蓋新版）
        if (installed is not null && installed >= UpdateService.CurrentVersion) return false;

        var source = Environment.ProcessPath!;
        Directory.CreateDirectory(InstallDir);
        try
        {
            File.Copy(source, InstalledExe, overwrite: true);
        }
        catch (IOException)
        {
            // 舊的那份正在跑（檔案鎖住）：這次跳過，下次啟動再說
            return false;
        }

        WriteShellEntries();
        return true;
    }

    /// <summary>開始功能表捷徑、App Paths、控制台的移除項目。</summary>
    private static void WriteShellEntries()
    {
        try
        {
            ShellLink.Create(ShortcutPath, InstalledExe, "MinePainter 影像編輯器", InstallDir);
        }
        catch
        {
            // 捷徑建不起來不影響其他部分
        }

        using (var key = Registry.CurrentUser.CreateSubKey(AppPathsKey))
        {
            key.SetValue(null, InstalledExe);
            key.SetValue("Path", InstallDir);
        }

        long sizeKb = 0;
        try
        {
            sizeKb = new FileInfo(InstalledExe).Length / 1024;
        }
        catch { /* 拿不到大小就不寫 */ }

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", UpdateService.CurrentVersion.ToString(3));
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("Publisher", "DragonL0508");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallFlag}");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            if (sizeKb > 0) key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
        }
    }

    /// <summary>
    /// 控制台按下「解除安裝」時走這裡（Program.Main 在建 UI 之前攔下 --uninstall）：
    /// 清掉關聯與捷徑／登錄檔，再交給一段 bat 等本程序結束後刪掉安裝資料夾。
    /// 使用者設定（%APPDATA%\MinePainter）刻意留著，重裝就接得回去。
    /// </summary>
    public static void Uninstall()
    {
        // 使用者明確按了解除安裝：之後再跑 zip 版就純綠色，不要靜默把自己裝回去
        try
        {
            var settings = AppSettings.Instance;
            settings.AutoInstall = false;
            settings.FileAssociationsOptOut = true;
            settings.Save();
        }
        catch { /* 設定檔寫不了不影響清理 */ }

        try { FileAssociations.RemoveAll(); } catch { /* 繼續清剩下的 */ }
        try { File.Delete(ShortcutPath); } catch { /* 捷徑可能已被刪 */ }
        Registry.CurrentUser.DeleteSubKeyTree(AppPathsKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (!IsRunningInstalled) return; // 不是從安裝位置跑的，沒有資料夾要刪

        // 執行中的 exe 不能刪自己：同 UpdateService 的做法，交給 bat 等 PID 消失
        var script = Path.Combine(Path.GetTempPath(), "minepainter-uninstall.bat");
        var bat = $"""
            @echo off
            :wait
            "%SystemRoot%\System32\tasklist.exe" /FI "PID eq {Environment.ProcessId}" 2>nul | "%SystemRoot%\System32\find.exe" "{Environment.ProcessId}" >nul
            if not errorlevel 1 (
                "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                goto wait
            )
            rem 資料夾本身偶爾會因為殘留的 handle 刪不掉（只剩空目錄），重試幾次
            set n=0
            :rmdir
            rmdir /s /q "{InstallDir}" >nul 2>&1
            if not exist "{InstallDir}" goto done
            set /a n+=1
            if %n% lss 10 (
                "%SystemRoot%\System32\timeout.exe" /t 1 /nobreak >nul
                goto rmdir
            )
            :done
            del /q "%~f0" >nul 2>&1
            """;
        try
        {
            File.WriteAllText(script, bat, UpdateService.BatchEncoding());
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 刪不掉資料夾也已經解除關聯了，使用者手動刪即可
        }
    }
}
