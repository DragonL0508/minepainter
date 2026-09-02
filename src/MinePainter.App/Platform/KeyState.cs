using System.Runtime.InteropServices;

namespace MinePainter.App.Platform;

/// <summary>
/// Avalonia 的 KeyModifiers 只有 Shift/Ctrl/Alt/Meta，拿不到 Caps Lock —— 它在
/// Windows 是「開關鍵」不是修飾鍵。這裡直接問 Win32：
/// GetKeyState 的高位元 = 鍵當下被壓著，低位元 = 切換狀態（燈亮）。
/// 我們只取「被壓著」，這樣大寫鎖定不小心開著時滾輪照常縮放，不會誤觸。
/// GetKeyState 回報的是「處理到目前這則訊息時」的狀態，正好對得上正在處理的滾輪事件。
/// 非 Windows 平台一律回傳 false。
/// </summary>
internal static class KeyState
{
    private const int VK_CAPITAL = 0x14;

    /// <summary>Caps Lock 鍵是否正被按住（不是「燈是否亮著」）。</summary>
    public static bool IsCapsLockHeld =>
        OperatingSystem.IsWindows() && (GetKeyState(VK_CAPITAL) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
