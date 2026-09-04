namespace MinePainter.Core.Documents;

/// <summary>
/// 快速模式（實驗）的規則：多大的專案值得用代理畫布、代理畫布要多大。
///
/// 想法：4K 專案的每一次合成、每一個效果都在算 830 萬個像素，但編輯時眼睛看到的是縮小後的畫面。
/// 那就讓「編輯」發生在 1080p 級的代理上，真正要出圖時再整份放大重算（見 <see cref="OutputRender"/>）。
/// 代價是筆刷畫上去的像素只能重新取樣 —— 所以這是選項而不是預設，而且專案本身完全沒變：
/// 同一個檔案以一般模式開啟就是把整份放大成專案解析度再編輯。
/// </summary>
public static class FastMode
{
    /// <summary>代理畫布的上限（Full HD）。</summary>
    public const int ProxyWidth = 1920;

    public const int ProxyHeight = 1080;

    /// <summary>超過這個像素數才值得問（＝比 Full HD 大）。</summary>
    public static bool ShouldOffer(int width, int height) =>
        (long)width * height > (long)ProxyWidth * ProxyHeight;

    /// <summary>
    /// 這個尺寸的代理畫布該多大：等比縮到裝得進 1920×1080。
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
