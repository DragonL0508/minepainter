namespace MinePainter.Core.Documents;

/// <summary>
/// 快速模式（實驗）的規則：多大的專案值得用代理畫布、代理畫布要多大。
///
/// 想法：4K 專案的每一次合成、每一個效果都在算 830 萬個像素，但編輯時眼睛看到的是縮小後的畫面。
/// 那就讓「編輯」發生在較小的代理上（預設 1080p，可在設定改），真正要出圖時再整份放大重算
/// （見 <see cref="OutputRender"/>）。
/// 代價是筆刷畫上去的像素只能重新取樣 —— 所以這是選項而不是預設，而且專案本身完全沒變：
/// 同一個檔案以一般模式開啟就是把整份放大成專案解析度再編輯。
/// </summary>
public static class FastMode
{
    /// <summary>預設的代理級別＝Full HD 的高度。</summary>
    public const int DefaultProxyHeight = 1080;

    /// <summary>設定裡可選的代理級別（高度；寬度一律照 16:9 由高度算）。</summary>
    public static readonly int[] Levels = [360, 480, 720, 1080, 1440, 2160];

    private static int _proxyHeight = DefaultProxyHeight;

    /// <summary>
    /// 代理畫布上限的高度，也就是「多大的畫布才提示快速模式」的門檻
    /// （使用者可在「設定 → 一般 → 快速模式」改，預設 1080）。
    /// </summary>
    public static int ProxyHeight
    {
        get => _proxyHeight;
        set => _proxyHeight = Math.Clamp(value, 120, 4320);
    }

    /// <summary>代理畫布上限的寬度：照 16:9 由高度算（1080 → 1920、720 → 1280）。</summary>
    public static int ProxyWidth => WidthFor(_proxyHeight);

    /// <summary>某個代理級別的寬度（16:9，取偶數：480 → 854、720 → 1280）。</summary>
    public static int WidthFor(int height) => Math.Max(2, (int)Math.Round(height * 16.0 / 9.0 / 2) * 2);

    /// <summary>超過這個像素數才值得問（＝比代理級別大）。</summary>
    public static bool ShouldOffer(int width, int height) =>
        (long)width * height > (long)ProxyWidth * ProxyHeight;

    /// <summary>
    /// 這個尺寸的代理畫布該多大：等比縮到裝得進代理級別（預設 1920×1080）。
    /// 直向、超寬的專案也一樣處理（看的是「兩邊都要裝得下」）。
    /// </summary>
    public static (int Width, int Height) ProxySize(int width, int height)
    {
        if (width <= 0 || height <= 0) return (Math.Max(1, width), Math.Max(1, height));
        var scale = Math.Min(1.0, Math.Min(ProxyWidth / (double)width, ProxyHeight / (double)height));
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return (w, h);
    }

    /// <summary>代理畫布相對專案解析度縮了多少（1 = 沒縮）。</summary>
    public static double ProxyScale(int width, int height)
    {
        var (w, _) = ProxySize(width, height);
        return width <= 0 ? 1 : w / (double)width;
    }
}
