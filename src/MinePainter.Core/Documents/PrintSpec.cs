using SkiaSharp;

namespace MinePainter.Core.Documents;

/// <summary>
/// 印刷的出血與安全框。只影響畫面上的輔助線與 <see cref="PrintCheck"/>，不影響任何像素、也不會被匯出。
///
/// **畫布本身就是「含出血」的尺寸** —— 送印廠的檔案就是它（名片：裁切 90×54 mm，畫布 92×56 mm）。
/// 裁切線＝畫布往內縮 <see cref="BleedMm"/>；安全框＝裁切線再往內縮 <see cref="SafeMm"/>。
///
/// 刻意不存「裁切尺寸」：存了就會有「規格與畫布對不上」的狀態要處理（改畫布、換 dpi、裁切之後），
/// 而那個狀態沒有正確答案。用「往內縮」定義，線永遠跟著畫布走。
/// </summary>
public sealed record PrintSpec(double BleedMm, double SafeMm)
{
    /// <summary>一邊最多 100 mm —— 再大就不是出血了。</summary>
    public const double MaxMm = 100;

    /// <summary>台灣印刷廠最常見的出血（每邊 3 mm）。</summary>
    public const double CommonBleedMm = 3;

    /// <summary>圖文離裁切邊的最小距離；裁切公差是 ±2 mm，留 3 mm 才安全。</summary>
    public const double CommonSafeMm = 3;

    public double BleedMm { get; init; } = Sane(BleedMm);
    public double SafeMm { get; init; } = Sane(SafeMm);

    private static double Sane(double mm) => double.IsFinite(mm) ? Math.Clamp(mm, 0, MaxMm) : 0;

    public static PrintSpec Common => new(CommonBleedMm, CommonSafeMm);

    /// <summary>公釐 → 像素（不取整；線要畫在半像素上也沒關係）。</summary>
    public static double MmToPixels(double mm, double dpi) => mm / PhysicalUnits.CentimetersPerInch / 10 * dpi;

    public static double PixelsToMm(double pixels, double dpi) => pixels / dpi * PhysicalUnits.CentimetersPerInch * 10;

    /// <summary>
    /// 「裁切後尺寸 + 出血」要開多大的畫布（像素）。新增文件的印刷預設集用這個算。
    /// </summary>
    public static SKSizeI CanvasSizeFor(double trimWidthMm, double trimHeightMm, double bleedMm, double dpi) =>
        new(
            PhysicalUnits.ToPixels(trimWidthMm + bleedMm * 2, LengthUnit.Millimeter, dpi),
            PhysicalUnits.ToPixels(trimHeightMm + bleedMm * 2, LengthUnit.Millimeter, dpi));

    /// <summary>裁切線（畫布座標）。出血 0 時就是整個畫布。</summary>
    public SKRect TrimRect(int canvasWidth, int canvasHeight, double dpi) =>
        Inset(SKRect.Create(canvasWidth, canvasHeight), MmToPixels(BleedMm, dpi));

    /// <summary>安全框（畫布座標）＝裁切線再往內縮。</summary>
    public SKRect SafeRect(int canvasWidth, int canvasHeight, double dpi) =>
        Inset(TrimRect(canvasWidth, canvasHeight, dpi), MmToPixels(SafeMm, dpi));

    public SKRect TrimRect(Document doc) => TrimRect(doc.Width, doc.Height, doc.Dpi);

    public SKRect SafeRect(Document doc) => SafeRect(doc.Width, doc.Height, doc.Dpi);

    /// <summary>裁切後的實體尺寸（公釐）；狀態列與對話框顯示用。</summary>
    public (double WidthMm, double HeightMm) TrimSizeMm(Document doc)
    {
        var rect = TrimRect(doc);
        return (PixelsToMm(rect.Width, doc.Dpi), PixelsToMm(rect.Height, doc.Dpi));
    }

    /// <summary>往內縮，縮到交叉就退成中線（極端設定不該產生反向的矩形）。</summary>
    private static SKRect Inset(SKRect rect, double amount)
    {
        var d = (float)amount;
        if (d <= 0) return rect;
        var left = rect.Left + d;
        var top = rect.Top + d;
        var right = rect.Right - d;
        var bottom = rect.Bottom - d;
        if (left > right) left = right = (rect.Left + rect.Right) / 2;
        if (top > bottom) top = bottom = (rect.Top + rect.Bottom) / 2;
        return new SKRect(left, top, right, bottom);
    }
}
