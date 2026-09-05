using System.Text;
using System.Text.RegularExpressions;
using MinePainter.Core.Vectors;
using SkiaSharp;

namespace MinePainter.Core.IO;

/// <summary>
/// Photoshop 文字圖層（<c>TySh</c>）→ 可編輯的 <see cref="TextElement"/>。
///
/// TySh = 版本、6 個 double 的變換矩陣（文字空間 → 文件）、文字描述子（含 EngineData）、彎曲描述子、外框。
/// 排版參數全在 EngineData：字型是 <c>ResourceDict/FontSet</c> 裡的 PostScript 名稱、字級是文件像素、
/// 字距 Tracking 是千分之一 em、行距 AutoLeading 是字級倍率。我們一個物件只有一套樣式，
/// 多段不同樣式時取最長的那段，其餘差異提示。
///
/// 位置：矩陣的平移是「第一行基線的錨點」（左／中／右對齊各自的錨），<c>bounds</c> 是相對錨點的排版框。
/// 我們的 <see cref="TextElement.Position"/> 是第一行左上角，所以要用「我們選到的字型」的 ascent 往上推
/// —— 基線對齊 PS 的基線，換了字型也不會整段往下掉。
///
/// 做不到而退回點陣的：直式文字、彎曲文字、多行不同對齊。
/// </summary>
internal static class PsdTextLayer
{
    /// <summary>
    /// 解出可編輯文字；<paramref name="failure"/> 說明為什麼不行（呼叫端退回點陣並提示）。
    /// <paramref name="notes"/> 收有損但仍可編輯的地方。
    /// </summary>
    public static TextElement? TryBuild(byte[] block, List<string> notes, out string? failure)
    {
        failure = null;
        var reader = new PsdByteReader(block);
        if (reader.UInt16() != 1)
        {
            failure = "文字資料版本不認得";
            return null;
        }

        var xx = reader.Double();
        var xy = reader.Double();
        var yx = reader.Double();
        var yy = reader.Double();
        var tx = reader.Double();
        var ty = reader.Double();

        reader.UInt16();    // 文字版本 50
        reader.UInt32();    // 描述子版本 16
        var text = PsdDescriptor.Read(reader);

        reader.UInt16();    // 彎曲版本
        reader.UInt32();
        var warp = PsdDescriptor.Read(reader);
        if (warp.Enum("warpStyle") is { } warpStyle && warpStyle != "warpNone")
        {
            failure = "彎曲文字";
            return null;
        }
        if (text.Enum("Ornt") == "Vrtc")
        {
            failure = "直式文字";
            return null;
        }

        var engineBytes = text.Raw("EngineData");
        if (engineBytes == null)
        {
            failure = "缺少排版資料";
            return null;
        }
        var engine = PsdEngineData.Parse(engineBytes);

        var content = (PsdEngineData.Text(engine, "EngineDict", "Editor", "Text") ?? text.Text("Txt ") ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n');
        if (content.EndsWith('\n')) content = content[..^1];
        if (content.Length == 0)
        {
            failure = "沒有文字內容";
            return null;
        }

        var style = DominantStyle(engine, notes);
        var paragraph = PsdEngineData.Dict(engine, "EngineDict", "ParagraphRun", "RunArray", 0, "ParagraphSheet", "Properties")
            ?? PsdEngineData.Dict(engine, "ResourceDict", "ParagraphSheetSet", 0, "Properties");

        // 矩陣：x' = xx·x + yx·y + tx，y' = xy·x + yy·y + ty。旋轉取第一欄，縮放各取欄長。
        var scaleX = Math.Sqrt(xx * xx + xy * xy);
        var scaleY = Math.Sqrt(yx * yx + yy * yy);
        if (scaleX < 1e-6 || scaleY < 1e-6)
        {
            failure = "變換矩陣退化";
            return null;
        }
        var rotation = (float)(Math.Atan2(xy, xx) * 180 / Math.PI);

        var fontSizePt = style.Number("FontSize") ?? 12;
        var fontSize = (float)(fontSizePt * scaleY);
        var horizontalScale = style.Number("HorizontalScale") ?? 1;
        var verticalScale = style.Number("VerticalScale") ?? 1;
        fontSize = (float)(fontSize * verticalScale);

        var fontIndex = (int)(style.Number("Font") ?? 0);
        var postScriptName = PsdEngineData.Text(engine, "ResourceDict", "FontSet", fontIndex, "Name") ?? "";
        var font = PsdFontName.Resolve(postScriptName);

        var tracking = style.Number("Tracking") ?? 0;
        var leadingScale = TextElement.DefaultLineHeightScale;
        if (style.Bool("AutoLeading") != false)
            leadingScale = (float)(PsdEngineData.Number(paragraph, "AutoLeading") ?? 1.2);
        else if (style.Number("Leading") is { } leading && leading > 0 && fontSizePt > 0)
            leadingScale = (float)(leading / fontSizePt);

        var justification = (int)(PsdEngineData.Number(paragraph, "Justification") ?? 0);
        var alignment = justification switch
        {
            1 or 4 => TextAlign.Right,
            2 or 5 => TextAlign.Center,
            _ => TextAlign.Left,
        };

        var element = new TextElement
        {
            Text = content,
            FontFamily = font.Family,
            FontWeight = font.Weight,
            Bold = style.Bool("FauxBold") == true,
            Italic = font.Italic || style.Bool("FauxItalic") == true,
            Underline = style.Bool("Underline") == true,
            Strikethrough = style.Bool("Strikethrough") == true,
            Color = ReadColor(style, "FillColor") ?? SKColors.Black,
            FontSize = Math.Max(1f, fontSize),
            ScaleX = (float)Math.Max(0.05, horizontalScale * scaleX / scaleY),
            Rotation = rotation,
            LetterSpacing = (float)(tracking / 1000.0 * fontSize),
            LineHeightScale = Math.Clamp(leadingScale, 0.3f, 5f),
            Alignment = alignment,
        };

        element = element with { Position = Anchor(element, text, tx, ty, scaleX, scaleY, rotation) };
        return element;
    }

    /// <summary>
    /// 樣式段落（StyleRun）裡最長的那段當代表；沒填的鍵回退到「正常」樣式表。
    /// 其他段落若字型／字級／顏色不同，提示一次。
    /// </summary>
    private static StyleView DominantStyle(Dictionary<string, object?> engine, List<string> notes)
    {
        var normalIndex = (int)(PsdEngineData.Number(engine, "ResourceDict", "TheNormalStyleSheet") ?? 0);
        var normal = PsdEngineData.Dict(engine, "ResourceDict", "StyleSheetSet", normalIndex, "StyleSheetData");
        var runs = PsdEngineData.List(engine, "EngineDict", "StyleRun", "RunArray") ?? [];
        var lengths = PsdEngineData.List(engine, "EngineDict", "StyleRun", "RunLengthArray") ?? [];

        Dictionary<string, object?>? best = null;
        var bestLength = -1.0;
        var all = new List<Dictionary<string, object?>>();
        for (var i = 0; i < runs.Count; i++)
        {
            var data = PsdEngineData.Dict(runs[i], "StyleSheet", "StyleSheetData");
            if (data == null) continue;
            all.Add(data);
            var length = i < lengths.Count ? lengths[i] as double? ?? 0 : 0;
            if (length > bestLength)
            {
                bestLength = length;
                best = data;
            }
        }

        var view = new StyleView(best, normal);
        foreach (var other in all)
        {
            var o = new StyleView(other, normal);
            if (o.Number("Font") != view.Number("Font") || Math.Abs((o.Number("FontSize") ?? 0) - (view.Number("FontSize") ?? 0)) > 0.01
                || ReadColor(o, "FillColor") != ReadColor(view, "FillColor"))
            {
                notes.Add("文字含多種字型或顏色，只保留最長那段的樣式。");
                break;
            }
        }
        return view;
    }

    /// <summary>FillColor：{/Type 1 /Values [a r g b]}（0..1）。</summary>
    private static SKColor? ReadColor(StyleView style, string key)
    {
        if (style.Get(key) is not Dictionary<string, object?> color) return null;
        if (PsdEngineData.List(color, "Values") is not { Count: >= 4 } v) return null;
        byte C(object? x) => (byte)Math.Clamp(Math.Round((x as double? ?? 0) * 255), 0, 255);
        return new SKColor(C(v[1]), C(v[2]), C(v[3]), C(v[0]));
    }

    /// <summary>
    /// 從 PS 的基線錨點推我們的左上角。bounds 是文字空間（未縮放）相對錨點的排版框；
    /// 對齊決定錨在框的哪一側。算出的本地偏移再照旋轉轉回文件座標（PS 以錨點為軸旋轉）。
    /// </summary>
    private static SKPoint Anchor(TextElement element, PsdDescriptor text, double tx, double ty, double scaleX, double scaleY, float rotation)
    {
        var bounds = text.Child("bounds");
        var left = (bounds?.Number("Left") ?? 0) * scaleX;
        var right = (bounds?.Number("Rght") ?? 0) * scaleX;
        var width = element.UnscaledWidth * element.ScaleX;

        var dx = element.Alignment switch
        {
            TextAlign.Center => (left + right) / 2 - width / 2,
            TextAlign.Right => right - width,
            _ => left,
        };
        var dy = -Ascent(element);

        var rad = rotation * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return new SKPoint(
            (float)(tx + dx * cos - dy * sin),
            (float)(ty + dx * sin + dy * cos));
    }

    /// <summary>與 <see cref="TextElement"/> 排版時同一套字型解析，ascent 才會一致。</summary>
    private static float Ascent(TextElement element)
    {
        var style = new SKFontStyle(
            element.Bold ? Math.Max((int)SKFontStyleWeight.Bold, element.FontWeight) : element.FontWeight,
            (int)SKFontStyleWidth.Normal,
            element.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var bundled = BundledFont.Resolve(element.FontFamily, style);
        var typeface = bundled ?? SKTypeface.FromFamilyName(element.FontFamily, style) ?? BundledFont.Typeface ?? SKTypeface.Default;
        try
        {
            using var font = new SKFont(typeface, element.FontSize);
            return -font.Metrics.Ascent;
        }
        finally
        {
            if (bundled == null && typeface != BundledFont.Typeface && typeface != SKTypeface.Default) typeface.Dispose();
        }
    }

    /// <summary>某一段的樣式 + 「正常」樣式表的預設值。</summary>
    private sealed class StyleView(Dictionary<string, object?>? run, Dictionary<string, object?>? normal)
    {
        public object? Get(string key) =>
            run != null && run.TryGetValue(key, out var v) && v != null ? v
            : normal != null && normal.TryGetValue(key, out var d) ? d : null;

        public double? Number(string key) => Get(key) as double?;
        public bool? Bool(string key) => Get(key) as bool?;
    }
}

/// <summary>
/// PostScript 字型名稱（<c>NotoSansTC-Black</c>、<c>MicrosoftJhengHeiBold</c>、<c>Arial-BoldItalicMT</c>）
/// → 這台機器認得的家族名 + 字重 + 斜體。PS 名稱沒有空格、樣式接在後面（多半用「-」隔開），
/// 所以拿「去掉空格與符號」後的名字去比對系統字型清單；比不到就把樣式字尾一段段剝掉再試。
/// 完全比不到時回傳照大小寫拆開的可讀名稱，讓「缺少字型」對話框接手。
/// </summary>
internal static partial class PsdFontName
{
    public readonly record struct Resolved(string Family, int Weight, bool Italic);

    private static readonly (string Token, int Weight)[] Weights =
    [
        ("Thin", 100), ("Hairline", 100),
        ("ExtraLight", 200), ("UltraLight", 200),
        ("Light", 300),
        ("Regular", 400), ("Normal", 400), ("Book", 400), ("Roman", 400), ("Medium", 500),
        ("SemiBold", 600), ("DemiBold", 600), ("Demi", 600),
        ("ExtraBold", 800), ("UltraBold", 800), ("Heavy", 800),
        ("Black", 900), ("ExtraBlack", 950),
        ("Bold", 700),
    ];

    private static readonly string[] Noise = ["MT", "PS", "Std", "Pro", "LT", "OT", "TT"];

    private static Dictionary<string, string>? _installed;

    public static Resolved Resolve(string postScriptName)
    {
        if (string.IsNullOrWhiteSpace(postScriptName)) return new Resolved("Microsoft JhengHei", 400, false);

        var name = postScriptName.Trim();
        var dash = name.IndexOf('-');
        var baseName = dash > 0 ? name[..dash] : name;
        var suffix = dash > 0 ? name[(dash + 1)..] : "";

        var (weight, italic) = ParseStyle(suffix);

        // 沒有「-」的（Windows 的 CJK 字型常這樣）：樣式黏在家族名後面，一段段剝
        if (dash < 0)
        {
            var (w2, i2, stripped) = StripTrailingStyle(baseName);
            baseName = stripped;
            weight = w2 ?? weight;
            italic |= i2;
        }

        var family = MatchInstalled(baseName) ?? MatchInstalled(StripNoise(baseName)) ?? Humanize(baseName);
        return new Resolved(family, weight ?? 400, italic);
    }

    private static (int? Weight, bool Italic) ParseStyle(string suffix)
    {
        int? weight = null;
        var italic = false;
        var rest = suffix;
        if (rest.Contains("Italic", StringComparison.OrdinalIgnoreCase) || rest.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
        {
            italic = true;
            rest = rest.Replace("Italic", "", StringComparison.OrdinalIgnoreCase).Replace("Oblique", "", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (token, w) in Weights)
        {
            if (rest.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                weight = w;
                rest = rest.Replace(token, "", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
        return (weight, italic);
    }

    private static (int? Weight, bool Italic, string Stripped) StripTrailingStyle(string name)
    {
        int? weight = null;
        var italic = false;
        var changed = true;
        while (changed)
        {
            changed = false;
            if (name.EndsWith("Italic", StringComparison.Ordinal) || name.EndsWith("Oblique", StringComparison.Ordinal))
            {
                italic = true;
                name = name[..name.LastIndexOf(name.EndsWith("Italic", StringComparison.Ordinal) ? "Italic" : "Oblique", StringComparison.Ordinal)];
                changed = true;
            }
            foreach (var (token, w) in Weights)
            {
                if (name.Length > token.Length && name.EndsWith(token, StringComparison.Ordinal))
                {
                    weight ??= w;
                    name = name[..^token.Length];
                    changed = true;
                    break;
                }
            }
        }
        return (weight, italic, name);
    }

    private static string StripNoise(string name)
    {
        foreach (var noise in Noise)
            if (name.Length > noise.Length + 2 && name.EndsWith(noise, StringComparison.Ordinal))
                return name[..^noise.Length];
        return name;
    }

    private static string? MatchInstalled(string baseName)
    {
        var key = Normalize(baseName);
        if (key.Length == 0) return null;
        return Installed().GetValueOrDefault(key);
    }

    private static Dictionary<string, string> Installed()
    {
        if (_installed != null) return _installed;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var family in SKFontManager.Default.FontFamilies)
                map.TryAdd(Normalize(family), family);
        }
        catch
        {
            // 沒有字型管理器（例如極簡的 CI 環境）就只靠名稱推
        }
        return _installed = map;
    }

    private static string Normalize(string s) => NonAlphanumeric().Replace(s, "").ToLowerInvariant();

    /// <summary>「NotoSansTC」→「Noto Sans TC」：只是給人看，缺字型對話框顯示用。</summary>
    private static string Humanize(string name) => CamelBoundary().Replace(name, " ").Trim();

    [GeneratedRegex("[^A-Za-z0-9一-鿿]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();
}
