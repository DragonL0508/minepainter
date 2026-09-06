using SkiaSharp;

namespace MinePainter.Core.Vectors;

/// <summary>
/// 逐字字面後備的排版器：Skia 的 DrawText 不做字型後備，選到不含中文（或任何缺字）
/// 的字型時會畫出 .notdef 豆腐框 —— 這裡把每行拆成「同字面的連續段」，缺字的段用
/// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> 找系統後備字型。
/// 量測與繪製共用同一套分段，寬度/對齊/底線因此一致。
/// 家族缺粗體/斜體字面時（CJK 字型常見）以合成效果（Embolden/SkewX）後備。
/// </summary>
internal sealed class TextShaper : IDisposable
{
    private readonly SKTypeface _primary;
    private readonly SKFontStyle _style;
    private readonly float _size;
    private readonly bool _bold;
    private readonly bool _italic;
    private readonly float _letterSpacing;
    private readonly Dictionary<int, SKTypeface?> _fallbackByCodepoint = new();
    private readonly List<SKTypeface> _owned = new();
    private readonly Dictionary<SKTypeface, SKFont> _fonts = new();
    private readonly Dictionary<SKTypeface, SKPaint> _measurePaints = new();
    private SKFont? _primaryFont;

    public TextShaper(string family, SKFontStyle style, float size, bool bold, bool italic,
        float letterSpacing = 0f)
    {
        _style = style;
        _size = size;
        _bold = bold;
        _italic = italic;
        _letterSpacing = letterSpacing;
        // 系統有這支就用系統的（字重才選得到），沒有才用內嵌的保底字型；
        // 內嵌那份全程共用一份，不進 _owned（見 BundledFont.Resolve）
        var primary = BundledFont.Resolve(family, style) ?? SKTypeface.FromFamilyName(family, style);
        if (primary != null && primary != BundledFont.Typeface) _owned.Add(primary);
        _primary = primary ?? BundledFont.Typeface ?? SKTypeface.Default;
    }

    /// <summary>主字面的 font（基線/底線位置一律以它為準，後備段落畫在同一條基線上）。</summary>
    public SKFont PrimaryFont => _primaryFont ??= CreateFont(_primary);

    public float MeasureLine(string line)
    {
        var width = 0f;
        foreach (var (typeface, segment) in Runs(line))
            width += Measure(typeface, segment);
        // 字距是「每個字之後」多留的距離（含最後一個，與 CSS letter-spacing 同義），
        // 量測與繪製必須用同一條規則，對齊與底線長度才對得上
        if (Math.Abs(_letterSpacing) > 0.0001f && line.Length > 0)
            width += _letterSpacing * CountRunes(line);
        return width;
    }

    /// <summary>
    /// 把一行排好、產出可重複使用的 <see cref="SKTextBlob"/>（字面分段、glyph 對應與位置都已定案）。
    /// 一個文字物件最多要畫八趟（光暈的描粗＋實心、陰影的兩趟、每層外框、字身），
    /// 每趟重跑一次分段與量測是白工 —— 排一次、八趟共用同一批 blob。
    /// </summary>
    public void BuildLine(string line, float x, float baseline, List<SKTextBlob> into)
    {
        var spaced = Math.Abs(_letterSpacing) > 0.0001f;
        foreach (var (typeface, segment) in Runs(line))
        {
            var font = FontFor(typeface);
            if (!spaced)
            {
                if (SKTextBlob.Create(segment, font, new SKPoint(x, baseline)) is { } blob) into.Add(blob);
                x += Measure(typeface, segment);
                continue;
            }
            // 有字距就得逐字擺（放棄字間 kerning —— 手動調字距本來就是要蓋掉它）
            foreach (var rune in segment.EnumerateRunes())
            {
                var glyph = rune.ToString();
                if (SKTextBlob.Create(glyph, font, new SKPoint(x, baseline)) is { } blob) into.Add(blob);
                x += Measure(typeface, glyph) + _letterSpacing;
            }
        }
    }

    /// <summary>
    /// 一行的實際著墨範圍（相對「筆起點在 x=0、baseline 在 y=0」）；
    /// 分段規則與繪製完全相同，含字距的逐字位移。空白行回傳 null。
    /// </summary>
    public SKRect? MeasureLineInk(string line)
    {
        var spaced = Math.Abs(_letterSpacing) > 0.0001f;
        var x = 0f;
        SKRect? acc = null;
        void Add(SKRect r)
        {
            acc = acc is { } a
                ? new SKRect(Math.Min(a.Left, r.Left), Math.Min(a.Top, r.Top),
                    Math.Max(a.Right, r.Right), Math.Max(a.Bottom, r.Bottom))
                : r;
        }

        foreach (var (typeface, segment) in Runs(line))
        {
            var measure = MeasurePaintFor(typeface);
            if (!spaced)
            {
                var ink = SKRect.Empty;
                var advance = measure.MeasureText(segment, ref ink);
                if (ink.Width > 0 && ink.Height > 0)
                {
                    ink.Offset(x, 0);
                    Add(ink);
                }
                x += advance;
                continue;
            }
            foreach (var rune in segment.EnumerateRunes())
            {
                var glyph = rune.ToString();
                var ink = SKRect.Empty;
                var advance = measure.MeasureText(glyph, ref ink);
                if (ink.Width > 0 && ink.Height > 0)
                {
                    ink.Offset(x, 0);
                    Add(ink);
                }
                x += advance + _letterSpacing;
            }
        }
        return acc;
    }

    private static int CountRunes(string line)
    {
        var n = 0;
        foreach (var _ in line.EnumerateRunes()) n++;
        return n;
    }

    private IEnumerable<(SKTypeface Typeface, string Segment)> Runs(string line)
    {
        if (line.Length == 0) yield break;
        var segment = new System.Text.StringBuilder();
        SKTypeface? current = null;
        foreach (var rune in line.EnumerateRunes())
        {
            var typeface = TypefaceFor(rune.Value);
            if (current != null && !ReferenceEquals(typeface, current))
            {
                yield return (current, segment.ToString());
                segment.Clear();
            }
            current = typeface;
            segment.Append(rune.ToString());
        }
        if (current != null && segment.Length > 0)
            yield return (current, segment.ToString());
    }

    private SKTypeface TypefaceFor(int codepoint)
    {
        if (_primary.ContainsGlyph(codepoint)) return _primary;
        if (_fallbackByCodepoint.TryGetValue(codepoint, out var cached)) return cached ?? _primary;

        // 先問系統（中文版 Windows 就用得到 JhengHei 這些）；整台沒有 CJK 字型時才用內嵌的保底字型
        var match = SKFontManager.Default.MatchCharacter(_primary.FamilyName, _style, null, codepoint);
        if (match != null) _owned.Add(match);
        else match = BundledFont.Match(codepoint);
        _fallbackByCodepoint[codepoint] = match;
        return match ?? _primary; // 連保底字型都沒這個字 → 用主字面畫 .notdef
    }

    private SKFont CreateFont(SKTypeface typeface)
    {
        var font = new SKFont(typeface, _size);
        if (_bold && typeface.FontWeight < 600) font.Embolden = true;
        if (_italic && !typeface.IsItalic) font.SkewX = -0.25f;
        return font;
    }

    /// <summary>這個字面的 font／量測 paint（每段、每趟都要用，建一次就留著）。</summary>
    private SKFont FontFor(SKTypeface typeface)
    {
        if (_fonts.TryGetValue(typeface, out var font)) return font;
        font = CreateFont(typeface);
        _fonts[typeface] = font;
        return font;
    }

    private SKPaint MeasurePaintFor(SKTypeface typeface)
    {
        if (_measurePaints.TryGetValue(typeface, out var paint)) return paint;
        paint = CreateMeasurePaint(typeface);
        _measurePaints[typeface] = paint;
        return paint;
    }

    private float Measure(SKTypeface typeface, string segment) =>
        MeasurePaintFor(typeface).MeasureText(segment);

    /// <summary>與繪製同設定的量測 paint（2.88 的 SKFont.MeasureText 不吃 string）。</summary>
    private SKPaint CreateMeasurePaint(SKTypeface typeface) => new()
    {
        Typeface = typeface,
        TextSize = _size,
        FakeBoldText = _bold && typeface.FontWeight < 600,
        TextSkewX = _italic && !typeface.IsItalic ? -0.25f : 0f,
    };

    public void Dispose()
    {
        _primaryFont?.Dispose();
        foreach (var font in _fonts.Values) font.Dispose();
        foreach (var paint in _measurePaints.Values) paint.Dispose();
        foreach (var typeface in _owned) typeface.Dispose();
        _style.Dispose();
    }
}
