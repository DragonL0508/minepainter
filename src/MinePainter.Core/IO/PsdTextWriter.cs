using System.Globalization;
using System.Text;
using MinePainter.Core.Vectors;

namespace MinePainter.Core.IO;

/// <summary>
/// <see cref="TextElement"/> → Photoshop 文字圖層（<c>TySh</c>），<see cref="PsdTextLayer"/> 的反向。
///
/// TySh = 版本 1、6 個 double 的變換矩陣、文字描述子（TxLr，含 EngineData）、彎曲描述子（warpNone）、外框 4 個 double。
/// 矩陣只放旋轉：字級直接是文件像素、水平拉伸走 HorizontalScale，讀回來才不會兩邊各乘一次。
/// 平移是「第一行基線的錨點」：左／中／右對齊各自錨在排版框的左／中／右，
/// 我們的 <see cref="TextElement.Position"/> 是第一行左上角，所以往下推一個 ascent、再照旋轉轉到文件座標。
///
/// EngineData 是 Photoshop 自己的排版資料，鍵一個都不能少（少了 Photoshop 會當檔案損毀），
/// 這裡照 Photoshop 存出來的樣子完整寫一份，只把字型、字級、顏色、對齊、行高、字距換成我們的值。
/// 字型寫 PostScript 名稱（家族去空格 + 「-」+ 字重），對方機器有那支字型就會對上，沒有也還是可編輯的文字。
///
/// 透視／彎曲過的文字不走這裡（Photoshop 的文字只吃仿射矩陣），呼叫端會把它烙成像素。
/// </summary>
internal static class PsdTextWriter
{
    public static byte[] Build(TextElement t)
    {
        var text = t.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var psText = text.Replace('\n', '\r') + "\r";   // PS 段落結尾帶一個 \r

        var ascent = PsdTextLayer.Ascent(t);
        var width = t.UnscaledWidth * t.ScaleX;
        var (left, right) = t.Alignment switch
        {
            TextAlign.Center => (-width / 2.0, width / 2.0),
            TextAlign.Right => (-width, 0.0),
            _ => (0.0, width),
        };
        // 讀取端從錨點推左上角：dx 是排版框左緣相對錨點的位置、dy 是往上一個 ascent；這裡反過來
        var dx = t.Alignment switch
        {
            TextAlign.Center => (left + right) / 2 - width / 2,
            TextAlign.Right => right - width,
            _ => left,
        };
        var dy = -(double)ascent;
        var rad = t.Rotation * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var tx = t.Position.X - (dx * cos - dy * sin);
        var ty = t.Position.Y - (dx * sin + dy * cos);
        var top = -(double)ascent;
        var bottom = lines.Length * (double)t.LineHeight - ascent;

        var bounds = new PsdDescriptorBuilder("bounds")
            .Add("Left", PsdDesc.Pnt(left))
            .Add("Top ", PsdDesc.Pnt(top))
            .Add("Rght", PsdDesc.Pnt(right))
            .Add("Btom", PsdDesc.Pnt(bottom));
        var boundingBox = new PsdDescriptorBuilder("boundingBox")
            .Add("Left", PsdDesc.Pnt(left))
            .Add("Top ", PsdDesc.Pnt(top))
            .Add("Rght", PsdDesc.Pnt(right))
            .Add("Btom", PsdDesc.Pnt(bottom));

        var textDescriptor = new PsdDescriptorBuilder("TxLr")
            .Add("Txt ", psText)
            .Add("textGridding", PsdDesc.Enum("textGridding", "None"))
            .Add("Ornt", PsdDesc.Enum("Ornt", "Hrzn"))
            .Add("AntA", PsdDesc.Enum("Annt", "antiAliasSharp"))
            .Add("bounds", bounds)
            .Add("boundingBox", boundingBox)
            .Add("TextIndex", 0)
            .Add("EngineData", EngineData(t, psText));

        var warp = new PsdDescriptorBuilder("warp")
            .Add("warpStyle", PsdDesc.Enum("warpStyle", "warpNone"))
            .Add("warpValue", 0.0)
            .Add("warpPerspective", 0.0)
            .Add("warpPerspectiveOther", 0.0)
            .Add("warpRotate", PsdDesc.Enum("Ornt", "Hrzn"));

        var w = new PsdByteWriter();
        w.U16(1);
        foreach (var v in new[] { cos, sin, -sin, cos, tx, ty }) w.F64(v);
        w.U16(50);
        w.U32(16);
        textDescriptor.WriteTo(w);
        w.U16(1);
        w.U32(16);
        warp.WriteTo(w);
        foreach (var v in new[] { left, top, right, bottom }) w.F64(v);
        return w.ToArray();
    }

    /// <summary>家族名去空格 + 「-」+ 字重（Noto／Adobe 字型的命名法；讀取端 <see cref="PsdFontName"/> 認得）。</summary>
    public static string PostScriptName(TextElement t)
    {
        var family = new string(t.FontFamily.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (family.Length == 0) family = "MicrosoftJhengHei";
        var weight = t.FontWeight switch
        {
            <= 150 => "Thin",
            <= 250 => "ExtraLight",
            <= 350 => "Light",
            <= 450 => "Regular",
            <= 550 => "Medium",
            <= 650 => "SemiBold",
            <= 750 => "Bold",
            <= 850 => "ExtraBold",
            _ => "Black",
        };
        return $"{family}-{weight}";
    }

    // ---- EngineData ----

    private static byte[] EngineData(TextElement t, string psText)
    {
        var justification = t.Alignment switch { TextAlign.Center => 2, TextAlign.Right => 1, _ => 0 };
        var tracking = t.FontSize > 0 ? t.LetterSpacing / t.FontSize * 1000.0 : 0.0;
        var fill = new object[] { t.Color.Alpha / 255.0, t.Color.Red / 255.0, t.Color.Green / 255.0, t.Color.Blue / 255.0 };
        var length = psText.Length;

        var paragraph = ParagraphProperties(justification, t.LineHeightScale);
        var style = StyleSheetData(
            fontSize: t.FontSize, fauxBold: t.Bold, fauxItalic: t.Italic, underline: t.Underline, strikethrough: t.Strikethrough,
            horizontalScale: t.ScaleX, tracking: tracking, fill: fill);
        var normalStyle = StyleSheetData(12.0, false, false, false, false, 1.0, 0.0, [1.0, 0.0, 0.0, 0.0]);

        var resources = D(
            ("KinsokuSet", L(
                D(("Name", "PhotoshopKinsokuHard"), ("NoStart", ""), ("NoEnd", ""), ("Keep", ""), ("Hanging", "")),
                D(("Name", "PhotoshopKinsokuSoft"), ("NoStart", ""), ("NoEnd", ""), ("Keep", ""), ("Hanging", "")))),
            ("MojiKumiSet", L(
                D(("InternalName", "Photoshop6MojiKumiSet1")),
                D(("InternalName", "Photoshop6MojiKumiSet2")),
                D(("InternalName", "Photoshop6MojiKumiSet3")),
                D(("InternalName", "Photoshop6MojiKumiSet4")))),
            ("TheNormalStyleSheet", 0),
            ("TheNormalParagraphSheet", 0),
            ("ParagraphSheetSet", L(D(("Name", "Normal RGB"), ("DefaultStyleSheet", 0), ("Properties", ParagraphProperties(0, 1.2f))))),
            ("StyleSheetSet", L(D(("Name", "Normal RGB"), ("StyleSheetData", normalStyle)))),
            ("FontSet", L(
                D(("Name", PostScriptName(t)), ("Script", 0), ("FontType", 1), ("Synthetic", 0)),
                D(("Name", "AdobeInvisFont"), ("Script", 0), ("FontType", 0), ("Synthetic", 0)))),
            ("SuperscriptSize", 0.583),
            ("SuperscriptPosition", 0.333),
            ("SubscriptSize", 0.583),
            ("SubscriptPosition", 0.333),
            ("SmallCapSize", 0.7));

        var shapeBase = D(
            ("ShapeType", 0),
            ("TransformPoint0", L(1.0, 0.0)),
            ("TransformPoint1", L(0.0, 1.0)),
            ("TransformPoint2", L(0.0, 0.0)));
        var shapeChild = D(
            ("ShapeType", 0),
            ("Procession", 0),
            ("Lines", D(("WritingDirection", 0), ("Children", L()))),
            ("Cookie", D(("Photoshop", D(("ShapeType", 0), ("PointBase", L(0.0, 0.0)), ("Base", shapeBase))))));
        var rendered = D(
            ("Version", 1),
            ("Shapes", D(("WritingDirection", 0), ("Children", L(shapeChild)))));

        var root = D(
            ("EngineDict", D(
                ("Editor", D(("Text", psText))),
                ("ParagraphRun", D(
                    ("DefaultRunData", D(
                        ("ParagraphSheet", D(("DefaultStyleSheet", 0), ("Properties", D()))),
                        ("Adjustments", D(("Axis", L(1.0, 0.0, 1.0)), ("XY", L(0.0, 0.0)))))),
                    ("RunArray", L(D(
                        ("ParagraphSheet", D(("DefaultStyleSheet", 0), ("Properties", paragraph))),
                        ("Adjustments", D(("Axis", L(1.0, 0.0, 1.0)), ("XY", L(0.0, 0.0))))))),
                    ("RunLengthArray", L(length)),
                    ("IsJoinable", 1))),
                ("StyleRun", D(
                    ("DefaultRunData", D(("StyleSheet", D(("StyleSheetData", D()))))),
                    ("RunArray", L(D(("StyleSheet", D(("StyleSheetData", style)))))),
                    ("RunLengthArray", L(length)),
                    ("IsJoinable", 2))),
                ("GridInfo", D(
                    ("GridIsOn", false), ("ShowGrid", false), ("GridSize", 18.0), ("GridLeading", 22.0),
                    ("GridColor", D(("Type", 1), ("Values", L(0.0, 0.0, 0.0, 1.0)))),
                    ("GridLeadingFillColor", D(("Type", 1), ("Values", L(0.0, 0.0, 0.0, 1.0)))),
                    ("AlignLineHeightToGridFlags", false))),
                ("AntiAlias", 4),
                ("UseFractionalGlyphWidths", true),
                ("Rendered", rendered))),
            ("ResourceDict", resources),
            ("DocumentResources", resources));

        var stream = new MemoryStream();
        Write(stream, root, 0);
        return stream.ToArray();
    }

    private static Dict ParagraphProperties(int justification, float autoLeading) => D(
        ("Justification", justification),
        ("FirstLineIndent", 0.0), ("StartIndent", 0.0), ("EndIndent", 0.0), ("SpaceBefore", 0.0), ("SpaceAfter", 0.0),
        ("AutoHyphenate", false), ("HyphenatedWordSize", 6), ("PreHyphen", 2), ("PostHyphen", 2), ("ConsecutiveHyphens", 8),
        ("Zone", 36.0),
        ("WordSpacing", L(0.8, 1.0, 1.33)), ("LetterSpacing", L(0.0, 0.0, 0.0)), ("GlyphSpacing", L(1.0, 1.0, 1.0)),
        ("AutoLeading", (double)autoLeading), ("LeadingType", 0), ("Hanging", false), ("Burasagari", false),
        ("KinsokuOrder", 0), ("EveryLineComposer", false));

    private static Dict StyleSheetData(double fontSize, bool fauxBold, bool fauxItalic, bool underline, bool strikethrough,
        double horizontalScale, double tracking, object[] fill) => D(
        ("Font", 0), ("FontSize", fontSize), ("FauxBold", fauxBold), ("FauxItalic", fauxItalic),
        ("AutoLeading", true), ("Leading", 0.0), ("HorizontalScale", horizontalScale), ("VerticalScale", 1.0),
        ("Tracking", tracking), ("AutoKerning", true), ("Kerning", 0), ("BaselineShift", 0.0),
        ("FontCaps", 0), ("FontBaseline", 0), ("Underline", underline), ("Strikethrough", strikethrough),
        ("Ligatures", true), ("DLigatures", false), ("BaselineDirection", 2), ("Tsume", 0.0), ("StyleRunAlignment", 2),
        ("Language", 0), ("NoBreak", false),
        ("FillColor", D(("Type", 1), ("Values", new List<object>(fill)))),
        ("StrokeColor", D(("Type", 1), ("Values", L(1.0, 0.0, 0.0, 0.0)))),
        ("FillFlag", true), ("StrokeFlag", false), ("FillFirst", true), ("YUnderline", 1), ("OutlineWidth", 1.0),
        ("CharacterDirection", 0), ("HindiNumbers", false), ("Kashida", 1), ("DiacriticPos", 2));

    // ---- PostScript 風格的序列化 ----

    private sealed class Dict : List<(string Key, object Value)>
    {
        public Dict(IEnumerable<(string Key, object Value)> items) : base(items) { }
    }

    private static Dict D(params (string Key, object Value)[] items) => new(items);
    private static List<object> L(params object[] items) => new(items);

    private static void Write(MemoryStream s, object value, int depth)
    {
        switch (value)
        {
            case Dict dict:
                Ascii(s, "<<");
                foreach (var (key, item) in dict)
                {
                    Ascii(s, "\n");
                    Ascii(s, new string('\t', depth + 1));
                    Ascii(s, "/" + key + " ");
                    Write(s, item, depth + 1);
                }
                Ascii(s, "\n");
                Ascii(s, new string('\t', depth));
                Ascii(s, ">>");
                break;
            case List<object> list:
                Ascii(s, "[");
                foreach (var item in list)
                {
                    Ascii(s, " ");
                    Write(s, item, depth);
                }
                Ascii(s, " ]");
                break;
            case bool b:
                Ascii(s, b ? "true" : "false");
                break;
            case int i:
                Ascii(s, i.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                Ascii(s, d.ToString("0.0####", CultureInfo.InvariantCulture));
                break;
            case float f:
                Ascii(s, ((double)f).ToString("0.0####", CultureInfo.InvariantCulture));
                break;
            case string str:
                WriteString(s, str);
                break;
            default:
                throw new ArgumentException($"EngineData 不能寫這種值：{value.GetType().Name}");
        }
    }

    /// <summary>( + FE FF + UTF-16BE + )，括號與反斜線逐位元組跳脫（高位元組也可能剛好是這些值）。</summary>
    private static void WriteString(MemoryStream s, string value)
    {
        s.WriteByte((byte)'(');
        s.WriteByte(0xFE);
        s.WriteByte(0xFF);
        foreach (var b in Encoding.BigEndianUnicode.GetBytes(value))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') s.WriteByte((byte)'\\');
            s.WriteByte(b);
        }
        s.WriteByte((byte)')');
    }

    private static void Ascii(MemoryStream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
}
