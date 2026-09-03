using Avalonia.Input;

namespace MinePainter.App.Controls;

/// <summary>
/// 全專案的滑鼠滾輪約定（使用者 2026-09-04 明示要一致）：
///
/// **往上滾＝變大（＋）、往下滾＝變小（−）。**
///
/// 與畫布的 Ctrl+滾輪縮放同向（往上滾＝放大），也與 Windows／paint.net／Photoshop 的
/// 數值控制項一致。任何「滾輪會改數值」的控制項都走這裡，不要各自寫 <c>Math.Sign(e.Delta.Y)</c>。
///
/// （捲動畫面不算數值增減：那是「往上滾＝看上面的內容」，與捲軸同向，見 CanvasView。）
/// </summary>
internal static class WheelInput
{
    /// <summary>這次滾動的方向：+1＝變大、−1＝變小、0＝沒動。垂直沒有值時退而取橫向（傾斜輪／觸控板）。</summary>
    public static int Direction(PointerWheelEventArgs e) => System.Math.Sign(Amount(e));

    /// <summary>這次滾動了幾格（至少 1；觸控板一次可能送好幾格）。</summary>
    public static int Notches(PointerWheelEventArgs e) =>
        System.Math.Max(1, (int)System.Math.Round(System.Math.Abs(Amount(e))));

    private static double Amount(PointerWheelEventArgs e) => e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
}
