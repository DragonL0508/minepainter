using MinePainter.Core.Adjustments;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// 我們的調整 → Photoshop 調整圖層的參數區塊（<see cref="PsdAdjustmentLayer"/> 的反向）。
/// 區塊格式照 Photoshop 檔案格式規格：色階 levl、曲線 curv、亮度／對比 brit＋CgEd、色相／飽和度 hue2、
/// 色彩平衡 blnc、曝光度 expA、臨界值 thrs、負片 nvrt、色調分離 post、相片濾鏡 phfl（版本 3 存 Lab）、通道混合器 mixr。
/// 沒有對應的（3D LUT、色溫、懷舊、黑白…）回 null，呼叫端提示後略過。
/// </summary>
internal static class PsdAdjustmentWriter
{
    public static bool CanWrite(IAdjustment adjustment) => adjustment is LevelsAdjustment or CurvesAdjustment
        or BrightnessContrastAdjustment or HueSaturationAdjustment or ColorBalanceAdjustment or ExposureAdjustment
        or ThresholdAdjustment or InvertAdjustment or PosterizeAdjustment or PhotoFilterAdjustment or ChannelMixerAdjustment;

    public static List<(string Key, byte[] Data)>? Write(IAdjustment adjustment)
    {
        var w = new PsdByteWriter();
        switch (adjustment)
        {
            case LevelsAdjustment l:
                // 版本 2 + 29 筆（輸入黑點、輸入白點、輸出黑點、輸出白點、gamma×100），第 0 筆是 RGB 合成、其餘直線
                w.U16(2);
                for (var i = 0; i < 29; i++)
                {
                    if (i == 0)
                    {
                        w.I16(l.InputLow);
                        w.I16(l.InputHigh);
                        w.I16(l.OutputLow);
                        w.I16(l.OutputHigh);
                        w.I16((int)Math.Round(l.Gamma * 100));
                    }
                    else
                    {
                        w.I16(0); w.I16(255); w.I16(0); w.I16(255); w.I16(100);
                    }
                }
                return [("levl", w.ToArray())];

            case CurvesAdjustment c:
                return [("curv", Curves(c))];

            case BrightnessContrastAdjustment bc:
            {
                var brightness = (int)Math.Round(Math.Clamp(bc.Brightness, -1f, 1f) * 100);
                var contrast = (int)Math.Round(Math.Clamp(bc.Contrast, -1f, 1f) * 100);
                w.I16(brightness);
                w.I16(contrast);
                w.I16(127);   // 平均值
                w.U8(0);      // Lab
                w.U8(0);
                var descriptor = new PsdDescriptorBuilder("null")
                    .Add("Vrsn", 1)
                    .Add("Brgh", brightness)
                    .Add("Cntr", contrast)
                    .Add("means", 127)
                    .Add("Lab ", false)
                    .Add("useLegacy", true)   // ±100 的舊算法，跟我們的範圍一致
                    .Add("Auto", false);
                return [("brit", w.ToArray()), ("CgEd", descriptor.ToBlockWithVersion())];
            }

            case HueSaturationAdjustment h:
            {
                // 版本 2、上色關、上色三值、主調整三值，再六個色域（四個邊界 + 三個值，全 0 = 不動）
                w.U16(2);
                w.U8(0);
                w.U8(0);
                w.I16(0); w.I16(25); w.I16(0);
                w.I16((int)Math.Round(Math.Clamp(h.Hue, -180f, 180f)));
                w.I16((int)Math.Round(Math.Clamp(h.Saturation, -1f, 1f) * 100));
                w.I16((int)Math.Round(Math.Clamp(h.Lightness, -1f, 1f) * 100));
                int[][] ranges =
                [
                    [315, 345, 15, 45], [15, 45, 75, 105], [75, 105, 135, 165],
                    [135, 165, 195, 225], [195, 225, 255, 285], [255, 285, 315, 345],
                ];
                foreach (var range in ranges)
                {
                    foreach (var edge in range) w.I16(edge);
                    w.I16(0); w.I16(0); w.I16(0);
                }
                return [("hue2", w.ToArray())];
            }

            case ColorBalanceAdjustment cb:
                w.I16(cb.ShadowsRed); w.I16(cb.ShadowsGreen); w.I16(cb.ShadowsBlue);
                w.I16(cb.MidtonesRed); w.I16(cb.MidtonesGreen); w.I16(cb.MidtonesBlue);
                w.I16(cb.HighlightsRed); w.I16(cb.HighlightsGreen); w.I16(cb.HighlightsBlue);
                w.U8(cb.PreserveLuminosity ? 1 : 0);
                w.U8(0);
                return [("blnc", w.ToArray())];

            case ExposureAdjustment e:
                w.U16(1);
                w.F32(e.Exposure);
                w.F32(e.Offset);
                w.F32(e.Gamma);
                return [("expA", w.ToArray())];

            case ThresholdAdjustment t:
                w.I16(Math.Clamp(t.Level, 1, 255));
                w.I16(0);
                return [("thrs", w.ToArray())];

            case InvertAdjustment:
                return [("nvrt", [])];

            case PosterizeAdjustment p:
                w.I16(Math.Clamp(p.Red, 2, 255));
                w.I16(0);
                return [("post", w.ToArray())];

            case PhotoFilterAdjustment pf:
            {
                var (l, a, b) = ToLab(pf.Color);
                w.U16(3);
                w.I32((int)Math.Round(l * 100));
                w.I32((int)Math.Round(a * 100));
                w.I32((int)Math.Round(b * 100));
                w.I32(Math.Clamp(pf.Density, 0, 100));
                w.U8(pf.PreserveLuminosity ? 1 : 0);
                w.U8(0);
                return [("phfl", w.ToArray())];
            }

            case ChannelMixerAdjustment m:
            {
                // 版本、單色旗標，紅／綠／藍輸出各（紅、綠、藍、常數），最後一組灰（單色時才有意義）
                w.U16(1);
                w.I16(m.Monochrome ? 1 : 0);
                int[] identity = [100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0];
                var rows = m.Rows.Length >= 12 ? m.Rows : identity;
                for (var i = 0; i < 12; i++) w.I16(m.Monochrome ? identity[i] : rows[i]);
                for (var i = 0; i < 4; i++) w.I16(m.Monochrome ? rows[i] : (i == 0 ? 40 : i == 1 ? 40 : i == 2 ? 20 : 0));
                return [("mixr", w.ToArray())];
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 版本 1、通道位元圖（bit 0 = RGB 合成、1..3 = R/G/B），每個有設的通道：點數 + (輸出, 輸入) 各 int16（0..255）。
    /// 亮度模式只寫合成通道；RGB 模式寫三個分色通道，合成通道給直線。
    /// </summary>
    private static byte[] Curves(CurvesAdjustment c)
    {
        var w = new PsdByteWriter();
        w.U16(1);
        var channels = new SortedDictionary<int, IReadOnlyList<(float X, float Y)>>();
        if (c.Mode == CurvesAdjustment.ModeRgb)
        {
            channels[0] = CurvesAdjustment.Identity;
            for (var i = 0; i < 3; i++) channels[i + 1] = i < c.Curves.Count ? c.Curves[i] : CurvesAdjustment.Identity;
        }
        else
        {
            channels[0] = c.Curves.Count > 0 ? c.Curves[0] : CurvesAdjustment.Identity;
        }
        var bitmap = 0;
        foreach (var channel in channels.Keys) bitmap |= 1 << channel;
        w.I32(bitmap);
        foreach (var (_, points) in channels)
        {
            var sorted = points.OrderBy(p => p.X).ToList();
            if (sorted.Count < 2) sorted = CurvesAdjustment.Identity.ToList();
            w.I16(Math.Min(sorted.Count, 19));   // PS 一條曲線最多 19 個點
            foreach (var (x, y) in sorted.Take(19))
            {
                w.I16((int)Math.Round(Math.Clamp(y, 0, 1) * 255));
                w.I16((int)Math.Round(Math.Clamp(x, 0, 1) * 255));
            }
        }
        return w.ToArray();
    }

    /// <summary>sRGB → CIE Lab（D65），<see cref="PsdAdjustmentLayer"/> 讀相片濾鏡時的反向。</summary>
    private static (double L, double A, double B) ToLab(SKColor color)
    {
        static double Linear(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var r = Linear(color.Red);
        var g = Linear(color.Green);
        var b = Linear(color.Blue);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 1.0;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
