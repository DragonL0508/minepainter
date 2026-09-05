using System.Buffers.Binary;
using MinePainter.Core.Adjustments;
using MinePainter.Core.Effects;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Photoshop 調整圖層的資料區塊 → 我們的 <see cref="IAdjustment"/>。
///
/// 舊型調整（色階、曲線、亮度／對比、色相／飽和度、色調分離、臨界值、負片、曝光度、色彩平衡、通道混合器）
/// 是各自的二進位格式（大端序 int16 為主）；新一點的（黑白、自然飽和度、相片濾鏡）是描述子。
/// 對不上的（漸層對應、選取顏色、色版查詢表…）回 null 並說明原因，呼叫端提示後略過。
/// </summary>
internal static class PsdAdjustmentLayer
{
    /// <summary>會出現在圖層附加資訊裡、代表「這是調整圖層」的 key。</summary>
    public static readonly HashSet<string> Keys =
    [
        "levl", "curv", "brit", "hue2", "hue ", "blnc", "phfl", "expA", "vibA", "thrs", "nvrt", "post", "blwh", "mixr",
        "grdm", "selc", "clrL", "CgEd",
    ];

    public static string DisplayName(string key) => key switch
    {
        "levl" => "色階", "curv" => "曲線", "brit" => "亮度 / 對比", "hue2" or "hue " => "色相 / 飽和度",
        "blnc" => "色彩平衡", "phfl" => "相片濾鏡", "expA" => "曝光度", "vibA" => "自然飽和度", "thrs" => "臨界值",
        "nvrt" => "負片效果", "post" => "色調分離", "blwh" => "黑白", "mixr" => "通道混合器",
        "grdm" => "漸層對應", "selc" => "選取顏色", "clrL" => "色版查詢表", _ => key.Trim(),
    };

    /// <summary>
    /// 解析。<paramref name="blocks"/> 是這一層所有附加資訊區塊（key → 原始位元組）；
    /// 同一個調整可能同時有舊格式與描述子（例如 brit + CgEd），優先拿描述子。
    /// </summary>
    public static IAdjustment? TryBuild(IReadOnlyDictionary<string, byte[]> blocks, List<string> notes, out string? failure)
    {
        failure = null;
        try
        {
            if (blocks.TryGetValue("levl", out var levl)) return Levels(levl);
            if (blocks.TryGetValue("curv", out var curv)) return Curves(curv);
            if (blocks.TryGetValue("brit", out var brit)) return BrightnessContrast(brit, blocks.GetValueOrDefault("CgEd"));
            if (blocks.TryGetValue("hue2", out var hue2)) return HueSaturation(hue2, notes);
            if (blocks.TryGetValue("blnc", out var blnc)) return ColorBalance(blnc);
            if (blocks.TryGetValue("expA", out var expA)) return Exposure(expA);
            if (blocks.TryGetValue("thrs", out var thrs)) return new ThresholdAdjustment(Math.Clamp(I16(thrs, 0), 1, 255));
            if (blocks.ContainsKey("nvrt")) return new InvertAdjustment();
            if (blocks.TryGetValue("post", out var post))
            {
                var levels = Math.Clamp(I16(post, 0), 2, 64);
                return new PosterizeAdjustment(levels, levels, levels);
            }
            if (blocks.TryGetValue("mixr", out var mixr)) return ChannelMixer(mixr);
            if (blocks.TryGetValue("phfl", out var phfl)) return PhotoFilter(phfl, notes);
            if (blocks.TryGetValue("blwh", out _))
            {
                notes.Add("黑白調整圖層的各色權重沒有對應，用預設的黑白。");
                return new BlackWhiteAdjustment();
            }
            if (blocks.TryGetValue("vibA", out var vibA)) return Vibrance(vibA, notes);
            if (blocks.TryGetValue("hue ", out _))
            {
                failure = "舊版（Photoshop 4）的色相／飽和度";
                return null;
            }
            foreach (var key in new[] { "grdm", "selc", "clrL", "CgEd" })
            {
                if (!blocks.ContainsKey(key)) continue;
                failure = DisplayName(key) + "沒有對應";
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            failure = "資料無法解析";
            return null;
        }
        failure = "不認得的調整";
        return null;
    }

    private static int I16(byte[] d, int offset) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(offset));
    private static int I32(byte[] d, int offset) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(offset));
    private static float F32(byte[] d, int offset) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(offset));

    /// <summary>色階：版本 + 29 筆（輸入黑點、輸入白點、輸出黑點、輸出白點、gamma×100），第 0 筆是 RGB 合成。</summary>
    private static LevelsAdjustment Levels(byte[] d)
    {
        var inLow = Math.Clamp(I16(d, 2), 0, 254);
        var inHigh = Math.Clamp(I16(d, 4), inLow + 1, 255);
        var outLow = Math.Clamp(I16(d, 6), 0, 255);
        var outHigh = Math.Clamp(I16(d, 8), 0, 255);
        var gamma = Math.Clamp(I16(d, 10) / 100f, 0.1f, 10f);
        return new LevelsAdjustment(inLow, inHigh, gamma, outLow, outHigh);
    }

    /// <summary>
    /// 曲線：版本 + 通道位元圖（bit 0 = RGB 合成、1..3 = R/G/B），每個有設的通道：點數 + (輸出, 輸入) 各 int16。
    /// 只有合成通道 → 亮度模式；有分色通道 → RGB 模式（沒設的通道用直線）。
    /// </summary>
    private static CurvesAdjustment Curves(byte[] d)
    {
        var bitmap = I32(d, 2);
        var offset = 6;
        var perChannel = new Dictionary<int, List<(float X, float Y)>>();
        for (var channel = 0; channel < 32 && offset + 2 <= d.Length; channel++)
        {
            if ((bitmap & (1 << channel)) == 0) continue;
            var count = I16(d, offset);
            offset += 2;
            var points = new List<(float, float)>(count);
            for (var i = 0; i < count && offset + 4 <= d.Length; i++)
            {
                var y = I16(d, offset) / 255f;
                var x = I16(d, offset + 2) / 255f;
                offset += 4;
                points.Add((Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
            }
            if (points.Count >= 2) perChannel[channel] = points;
        }

        var hasRgb = perChannel.ContainsKey(1) || perChannel.ContainsKey(2) || perChannel.ContainsKey(3);
        if (!hasRgb)
        {
            return new CurvesAdjustment
            {
                Mode = CurvesAdjustment.ModeLuminosity,
                Curves = [perChannel.GetValueOrDefault(0) ?? CurvesAdjustment.Identity.ToList()],
            };
        }
        var composite = perChannel.GetValueOrDefault(0);
        IReadOnlyList<(float X, float Y)> Channel(int c) => perChannel.GetValueOrDefault(c) ?? composite ?? CurvesAdjustment.Identity;
        return new CurvesAdjustment { Mode = CurvesAdjustment.ModeRgb, Curves = [Channel(1), Channel(2), Channel(3)] };
    }

    /// <summary>亮度／對比：舊格式兩個 int16（−100..100、−50..100）；有 CgEd 描述子（新演算法，−150..150）就以它為準。</summary>
    private static BrightnessContrastAdjustment BrightnessContrast(byte[] legacy, byte[]? descriptorBlock)
    {
        float brightness = I16(legacy, 0);
        float contrast = I16(legacy, 2);
        if (descriptorBlock != null)
        {
            var reader = new PsdByteReader(descriptorBlock);
            reader.UInt32();
            var desc = PsdDescriptor.Read(reader);
            if (desc.Number("Brgh") is { } b) brightness = (float)b;
            if (desc.Number("Cntr") is { } c) contrast = (float)c;
        }
        return new BrightnessContrastAdjustment(Math.Clamp(brightness / 100f, -1f, 1f), Math.Clamp(contrast / 100f, -1f, 1f));
    }

    /// <summary>色相／飽和度 v2：版本、上色旗標、上色的三值、然後主調整的色相／飽和度／明度（各 int16）。</summary>
    private static IAdjustment HueSaturation(byte[] d, List<string> notes)
    {
        var colorize = d.Length > 2 && d[2] != 0;
        if (colorize) notes.Add("色相／飽和度的「上色」沒有對應，只套主調整。");
        // 版本(2) 上色(1) 補位(1) 上色三值(6) → 主調整從 10 開始
        var hue = Math.Clamp(I16(d, 10), -180, 180);
        var saturation = Math.Clamp(I16(d, 12) / 100f, -1f, 1f);
        var lightness = Math.Clamp(I16(d, 14) / 100f, -1f, 1f);
        var rangesDiffer = false;
        for (var r = 0; r < 6 && 16 + r * 14 + 14 <= d.Length; r++)
        {
            var baseOffset = 16 + r * 14 + 8;
            if (I16(d, baseOffset) != 0 || I16(d, baseOffset + 2) != 0 || I16(d, baseOffset + 4) != 0) rangesDiffer = true;
        }
        if (rangesDiffer) notes.Add("色相／飽和度的個別色域調整沒有對應，只套主調整。");
        return new HueSaturationAdjustment(hue, saturation, lightness);
    }

    /// <summary>色彩平衡：9 個 int16（陰影、中間調、亮部各 青紅／洋紅綠／黃藍）+ 保留明度。</summary>
    private static ColorBalanceAdjustment ColorBalance(byte[] d) => new()
    {
        ShadowsRed = I16(d, 0), ShadowsGreen = I16(d, 2), ShadowsBlue = I16(d, 4),
        MidtonesRed = I16(d, 6), MidtonesGreen = I16(d, 8), MidtonesBlue = I16(d, 10),
        HighlightsRed = I16(d, 12), HighlightsGreen = I16(d, 14), HighlightsBlue = I16(d, 16),
        PreserveLuminosity = d.Length <= 18 || d[18] != 0,
    };

    /// <summary>曝光度：版本 + 三個 float（曝光、偏移、gamma）。</summary>
    private static ExposureAdjustment Exposure(byte[] d) =>
        new(Math.Clamp(F32(d, 2), -20f, 20f), Math.Clamp(F32(d, 6), -0.5f, 0.5f), Math.Clamp(F32(d, 10), 0.01f, 10f));

    /// <summary>通道混合器：版本、單色旗標，接著紅／綠／藍輸出各 4 個 int16（紅、綠、藍、常數，%），單色時再一組灰。</summary>
    private static ChannelMixerAdjustment ChannelMixer(byte[] d)
    {
        var mono = I16(d, 2) != 0;
        var rows = new int[12];
        if (mono && d.Length >= 4 + 4 * 8)
        {
            // 單色：灰那一組在三組 RGB 之後（第 4 組）
            for (var c = 0; c < 4; c++) rows[c] = I16(d, 4 + 3 * 8 + c * 2);
        }
        else
        {
            for (var row = 0; row < 3; row++)
            for (var c = 0; c < 4; c++)
                rows[row * 4 + c] = I16(d, 4 + row * 8 + c * 2);
        }
        return new ChannelMixerAdjustment { Rows = rows, Monochrome = mono };
    }

    /// <summary>
    /// 相片濾鏡：版本 2 存 XYZ、版本 3 存 Lab（各三個 int32，×100），接著濃度 int32、保留明度 byte。
    /// 顏色換算不確定時退回 PS 的預設暖色濾鏡並提示。
    /// </summary>
    private static PhotoFilterAdjustment PhotoFilter(byte[] d, List<string> notes)
    {
        var version = I16(d, 0);
        var c0 = I32(d, 2) / 100.0;
        var c1 = I32(d, 6) / 100.0;
        var c2 = I32(d, 10) / 100.0;
        var density = Math.Clamp(I32(d, 14), 0, 100);
        var preserve = d.Length <= 18 || d[18] != 0;

        SKColor? color = version == 3 ? LabToRgb(c0, c1, c2) : version == 2 ? XyzToRgb(c0, c1, c2) : null;
        if (color == null) notes.Add("相片濾鏡的顏色無法換算，用預設的暖色濾鏡。");
        return new PhotoFilterAdjustment { Color = color ?? new SKColor(0xEC, 0x8A, 0x00), Density = density, PreserveLuminosity = preserve };
    }

    /// <summary>自然飽和度（描述子 Vrb／Strt）：我們沒有這個演算法，以飽和度近似（自然飽和度算一半）。</summary>
    private static IAdjustment Vibrance(byte[] d, List<string> notes)
    {
        var reader = new PsdByteReader(d);
        reader.UInt32();
        var desc = PsdDescriptor.Read(reader);
        var vibrance = desc.Number("vibrance") ?? desc.Number("Vrb ") ?? 0;
        var saturation = desc.Number("Strt") ?? 0;
        notes.Add("自然飽和度以飽和度近似。");
        return new HueSaturationAdjustment(0, (float)Math.Clamp((saturation + vibrance * 0.5) / 100.0, -1, 1), 0);
    }

    private static SKColor? LabToRgb(double l, double a, double b)
    {
        if (l is < 0 or > 100 || Math.Abs(a) > 128 || Math.Abs(b) > 128) return null;
        var fy = (l + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - b / 200;
        static double Inv(double t) => t > 6.0 / 29 ? t * t * t : 3.0 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        return XyzToRgb(0.95047 * Inv(fx), 1.0 * Inv(fy), 1.08883 * Inv(fz));
    }

    private static SKColor? XyzToRgb(double x, double y, double z)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return null;
        var r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        var g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        var b = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        static byte Gamma(double c)
        {
            c = Math.Clamp(c, 0, 1);
            c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
        }
        return new SKColor(Gamma(r), Gamma(g), Gamma(b));
    }

    // ---- 填色圖層（沒有像素、只靠參數）----

    /// <summary>純色填色（SoCo）：整張畫布填 Clr。</summary>
    public static SKColor? SolidFillColor(byte[] block)
    {
        var reader = new PsdByteReader(block);
        reader.UInt32();
        var desc = PsdDescriptor.Read(reader);
        return desc.Color("Clr ");
    }

    /// <summary>漸層填色（GdFl）：Grad 節點、Angl、Type（Lnr／Rdl）、Rvrs；回傳畫整張畫布用的著色器參數。</summary>
    public static (GradientStops Stops, float AngleCcw, bool Radial)? GradientFill(byte[] block)
    {
        var reader = new PsdByteReader(block);
        reader.UInt32();
        var desc = PsdDescriptor.Read(reader);
        if (desc.Child("Grad") is not { } grad || grad.List("Clrs") is not { } colors) return null;
        var reverse = desc.Bool("Rvrs") == true;
        var stops = new List<GradientStop>();
        foreach (var item in colors)
        {
            if (item is not PsdDescriptor stop || stop.Color("Clr ") is not { } color) continue;
            var t = (float)Math.Clamp((stop.Number("Lctn") ?? 0) / 4096.0, 0, 1);
            stops.Add(new GradientStop(reverse ? 1 - t : t, color));
        }
        if (stops.Count < 2) return null;
        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        return (new GradientStops(stops), (float)(desc.Number("Angl") ?? 90), desc.Enum("Type") == "Rdl");
    }
}
