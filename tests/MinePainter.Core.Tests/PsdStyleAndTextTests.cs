using System.Buffers.Binary;
using System.Text;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .psd 的圖層樣式（lfx2）與文字圖層（TySh）匯入。描述子與 EngineData 都由 <see cref="Desc"/> 照規格現寫。
/// 數值對照見 PsdLayerStyle 的註解：長度是像素、展開是百分比、光源角度是逆時針且陰影落在對面。
/// </summary>
public class PsdStyleAndTextTests
{
    private static byte[] Solid(int width, int height, byte value)
    {
        var data = new byte[width * height];
        Array.Fill(data, value);
        return data;
    }

    private static PsdFormatTests.PsdWriter.Layer Raster(string name, int size, Dictionary<string, byte[]> blocks)
    {
        var layer = new PsdFormatTests.PsdWriter.Layer(name, new SKRectI(0, 0, size, size))
        {
            Channels = { [0] = Solid(size, size, 200), [1] = Solid(size, size, 0), [2] = Solid(size, size, 0), [-1] = Solid(size, size, 255) },
        };
        foreach (var (key, payload) in blocks) layer.Blocks[key] = payload;
        return layer;
    }

    [Fact]
    public void Load_MapsLayerStyleToEffectStack()
    {
        // 預設整體光源 120°（光從左上來）→ 陰影往右下：距離 10 → 位移 (+5, +9)
        var lfx2 = Desc.Lfx2(
            ("DrSh", Desc.Fx(("Clr ", Desc.Rgb(255, 0, 0)), ("Opct", Desc.Prc(75)), ("uglg", true), ("lagl", Desc.Ang(120)),
                ("Dstn", Desc.Px(10)), ("Ckmt", Desc.Px(0)), ("blur", Desc.Px(6)))),
            ("OrGl", Desc.Fx(("Clr ", Desc.Rgb(0, 255, 0)), ("Opct", Desc.Prc(50)), ("Ckmt", Desc.Px(50)), ("blur", Desc.Px(8)))),
            ("FrFX", Desc.Fx(("Styl", Desc.Enum("FStl", "OutF")), ("PntT", Desc.Enum("FrFl", "SClr")), ("Opct", Desc.Prc(100)),
                ("Sz  ", Desc.Px(3)), ("Clr ", Desc.Rgb(0, 0, 255)))),
            ("SoFi", Desc.Fx(("Clr ", Desc.Rgb(10, 20, 30)), ("Opct", Desc.Prc(40)))),
            ("IrSh", Desc.Fx(("Clr ", Desc.Rgb(0, 0, 0)), ("Opct", Desc.Prc(35)))));

        var file = PsdFormatTests.PsdWriter.Build(16, 16, [Raster("styled", 16, new() { ["lfx2"] = lfx2 })]);
        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        var effects = layer.Effects.Select(e => e.Effect).ToList();
        Assert.Equal(5, effects.Count);   // 塗色、內陰影、外框、光暈、陰影（PS 由下往上的順序）

        var fill = Assert.IsType<ObjectFillEffect>(effects[0]);
        Assert.Equal(new SKColor(10, 20, 30), fill.Color);
        Assert.Equal(40, fill.Opacity);

        var innerShadow = Assert.IsType<InnerShadowEffect>(effects[1]);
        Assert.Equal(35, innerShadow.Opacity);

        var outline = Assert.IsType<ObjectOutlineEffect>(effects[2]);
        Assert.Equal(3, outline.Width);
        Assert.Equal(new SKColor(0, 0, 255, 255), outline.Color);

        var glow = Assert.IsType<ObjectGlowEffect>(effects[3]);
        Assert.Equal(8, glow.Size);
        Assert.Equal(4, glow.Spread);   // 50% 的 8
        Assert.Equal(50, glow.Opacity);

        var shadow = Assert.IsType<ObjectShadowEffect>(effects[4]);
        Assert.Equal(5, shadow.OffsetX);
        Assert.Equal(9, shadow.OffsetY);
        Assert.Equal(6, shadow.Blur);
        Assert.Equal(75, shadow.Opacity);
        Assert.Equal(new SKColor(255, 0, 0), shadow.Color);

        Assert.DoesNotContain(warnings, w => w.Contains("內陰影"));
    }

    [Fact]
    public void Load_MapsBevelInnerShadowAndStrokePosition()
    {
        var lfx2 = Desc.Lfx2(
            ("ebbl", Desc.Fx(("bvlS", Desc.Enum("BESl", "OtrB")), ("bvlD", Desc.Enum("BESs", "Out")), ("Sz  ", Desc.Px(7)),
                ("srgR", Desc.Prc(250)), ("Sftn", Desc.Px(2)), ("lagl", Desc.Ang(45)), ("Lald", Desc.Ang(60)),
                ("hglC", Desc.Rgb(255, 255, 0)), ("hglO", Desc.Prc(60)), ("sdwC", Desc.Rgb(0, 0, 40)), ("sdwO", Desc.Prc(90)))),
            ("IrSh", Desc.Fx(("Clr ", Desc.Rgb(10, 0, 0)), ("Opct", Desc.Prc(40)), ("lagl", Desc.Ang(90)), ("Dstn", Desc.Px(6)),
                ("Ckmt", Desc.Px(20)), ("blur", Desc.Px(9)))),
            ("FrFX", Desc.Fx(("Styl", Desc.Enum("FStl", "InsF")), ("Opct", Desc.Prc(100)), ("Sz  ", Desc.Px(5)), ("Clr ", Desc.Rgb(1, 2, 3)))));

        var file = PsdFormatTests.PsdWriter.Build(16, 16, [Raster("styled", 16, new() { ["lfx2"] = lfx2 })]);
        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        var effects = layer.Effects.Select(e => e.Effect).ToList();
        Assert.Equal(3, effects.Count);

        var innerShadow = Assert.IsType<InnerShadowEffect>(effects[0]);
        Assert.Equal(90f, innerShadow.Angle);
        Assert.Equal(6, innerShadow.Distance);
        Assert.Equal(9, innerShadow.Size);
        Assert.Equal(20, innerShadow.Choke);
        Assert.Equal(40, innerShadow.Opacity);

        var bevel = Assert.IsType<BevelEmbossEffect>(effects[1]);
        Assert.Equal(1, bevel.Style);
        Assert.False(bevel.Up);
        Assert.Equal(7, bevel.Size);
        Assert.Equal(250, bevel.Depth);
        Assert.Equal(2, bevel.Soften);
        Assert.Equal(45f, bevel.Angle);
        Assert.Equal(60f, bevel.Altitude);
        Assert.Equal(new SKColor(255, 255, 0), bevel.HighlightColor);
        Assert.Equal(60, bevel.HighlightOpacity);
        Assert.Equal(90, bevel.ShadowOpacity);

        var stroke = Assert.IsType<ObjectOutlineEffect>(effects[2]);
        Assert.Equal(2, stroke.Position);
        Assert.Equal(5, stroke.Width);

        Assert.DoesNotContain(warnings, w => w.Contains("沒有對應"));
    }

    [Fact]
    public void Load_SkipsDisabledStylesAndRespectsGlobalAngleResource()
    {
        var lfx2 = Desc.Lfx2(
            ("DrSh", Desc.Fx(("enab", false), ("Dstn", Desc.Px(10)), ("blur", Desc.Px(6)))),
            ("dropShadowMulti", new List<object>
            {
                Desc.Fx(("Clr ", Desc.Rgb(0, 0, 0)), ("Opct", Desc.Prc(100)), ("uglg", true), ("Dstn", Desc.Px(10)), ("blur", Desc.Px(2))),
            }));

        // 影像資源 1037：整體光源 0°（光從右邊來）→ 陰影往左
        var file = PsdFormatTests.PsdWriter.Build(8, 8, [Raster("s", 8, new() { ["lfx2"] = lfx2 })], globalAngle: 0);
        using var doc = PsdFormat.Load(new MemoryStream(file), out _);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        var shadow = Assert.IsType<ObjectShadowEffect>(Assert.Single(layer.Effects).Effect);
        Assert.Equal(-10, shadow.OffsetX);
        Assert.Equal(0, shadow.OffsetY);
        Assert.Equal(2, shadow.Blur);
    }

    [Fact]
    public void Load_ImportsTextLayerAsEditableTextWithStyle()
    {
        var engine = Desc.EngineData(
            text: "Hello\rWorld\r", fontName: "Arial-BoldMT", fontSize: 40, tracking: 100,
            fill: [1.0, 1.0, 0.0, 0.0], justification: 2, autoLeading: 1.5);
        var tySh = Desc.TySh(engine, tx: 100, ty: 200, boundsLeft: -50, boundsRight: 50, text: "Hello\rWorld\r");
        var lfx2 = Desc.Lfx2(
            ("FrFX", Desc.Fx(("Styl", Desc.Enum("FStl", "OutF")), ("Opct", Desc.Prc(100)), ("Sz  ", Desc.Px(4)), ("Clr ", Desc.Rgb(0, 0, 255)))),
            ("OrGl", Desc.Fx(("Clr ", Desc.Rgb(255, 255, 0)), ("Opct", Desc.Prc(80)), ("Ckmt", Desc.Px(25)), ("blur", Desc.Px(12)))));

        var file = PsdFormatTests.PsdWriter.Build(400, 300, [Raster("title", 40, new() { ["TySh"] = tySh, ["lfx2"] = lfx2 })]);
        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);

        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        var text = Assert.IsType<TextElement>(Assert.Single(layer.Elements));
        Assert.True(layer.Surface.ExactContentBounds().IsEmpty, "文字圖層不該帶像素（文字圖層不變式）");

        Assert.Equal("Hello\nWorld", text.Text);
        Assert.Equal("Arial", text.FontFamily);
        Assert.Equal(700, text.FontWeight);
        Assert.Equal(40f, text.FontSize, 2);
        Assert.Equal(new SKColor(255, 0, 0, 255), text.Color);
        Assert.Equal(TextAlign.Center, text.Alignment);
        Assert.Equal(4f, text.LetterSpacing, 2);       // 100/1000 em × 40
        Assert.Equal(1.5f, text.LineHeightScale, 2);

        // 錨點 (100, 200) 是基線；左上角在基線上方一個 ascent、水平以框中心對齊
        Assert.InRange(text.Position.Y, 200 - 40 * 1.2f, 200 - 40 * 0.6f);
        var width = text.UnscaledWidth * text.ScaleX;
        Assert.InRange(text.Position.X + width / 2, 99, 101);

        // 圖層樣式掛在圖層的效果堆疊（文字也一樣，統一在效果面板編輯）
        var effects = layer.Effects.Select(e => e.Effect).ToList();
        var stroke = Assert.IsType<ObjectOutlineEffect>(Assert.Single(effects, e => e is ObjectOutlineEffect));
        Assert.Equal(4, stroke.Width);
        Assert.Equal(0, stroke.Position);
        Assert.Equal(new SKColor(0, 0, 255, 255), stroke.Color);
        var glow = Assert.IsType<ObjectGlowEffect>(Assert.Single(effects, e => e is ObjectGlowEffect));
        Assert.Equal(12, glow.Size);
        Assert.Equal(3, glow.Spread);
        Assert.Equal(80, glow.Opacity);
        Assert.Null(text.Stroke);

        Assert.DoesNotContain(warnings, w => w.Contains("轉成像素"));
    }

    [Fact]
    public void Load_TextFallsBackToRasterWhenWarpedOrVertical()
    {
        var engine = Desc.EngineData("Arc\r", "Arial-BoldMT", 20, 0, [1.0, 0, 0, 0], 0, 1.2);
        var warped = Desc.TySh(engine, 10, 10, 0, 0, "Arc\r", warpStyle: "warpArc");
        var file = PsdFormatTests.PsdWriter.Build(40, 40, [Raster("arc", 40, new() { ["TySh"] = warped })]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out var warnings);
        var layer = Assert.IsType<RasterLayer>(Assert.Single(doc.Root.Children));
        Assert.Empty(layer.Elements);
        Assert.False(layer.Surface.ExactContentBounds().IsEmpty, "退回點陣時要保留 Photoshop 的點陣快照");
        Assert.Contains(warnings, w => w.Contains("彎曲文字"));
    }

    [Fact]
    public void Load_RotatedTextKeepsAnchorAtBaseline()
    {
        // 順時針轉 90°：矩陣 [cos sin -sin cos] = [0 1 -1 0]
        var engine = Desc.EngineData("Up\r", "Arial-BoldMT", 30, 0, [1.0, 0, 0, 1.0], 0, 1.2);
        var tySh = Desc.TySh(engine, 50, 60, 0, 0, "Up\r", xx: 0, xy: 1, yx: -1, yy: 0);
        var file = PsdFormatTests.PsdWriter.Build(200, 200, [Raster("up", 30, new() { ["TySh"] = tySh })]);

        using var doc = PsdFormat.Load(new MemoryStream(file), out _);
        var text = Assert.IsType<TextElement>(Assert.Single(((RasterLayer)doc.Root.Children[0]).Elements));
        Assert.Equal(90f, text.Rotation, 1);
        Assert.Equal(30f, text.FontSize, 2);
        // 左上角 = 錨點 + 旋轉後的 (0, −ascent)：轉 90° 後 −ascent 落在 +X
        Assert.InRange(text.Position.X, 50 + 30 * 0.6f, 50 + 30 * 1.2f);
        Assert.InRange(text.Position.Y, 59, 61);
    }

    [Theory]
    [InlineData("Arial-BoldItalicMT", "Arial", 700, true)]
    [InlineData("NotoSansTC-Black", "Noto Sans TC", 900, false)]
    [InlineData("FooBarSans-Light", "Foo Bar Sans", 300, false)]
    [InlineData("SomeUnknownFontBold", "Some Unknown Font", 700, false)]
    public void FontName_ResolvesFamilyWeightAndItalic(string postScript, string expectedFamily, int weight, bool italic)
    {
        var resolved = PsdFontName.Resolve(postScript);
        // 沒裝的字型回可讀名稱；裝了就回系統的家族名（兩者在 CI 上都可能出現）
        Assert.True(resolved.Family == expectedFamily || resolved.Family.Replace(" ", "") == expectedFamily.Replace(" ", ""),
            $"家族名應為「{expectedFamily}」，實際「{resolved.Family}」");
        Assert.Equal(weight, resolved.Weight);
        Assert.Equal(italic, resolved.Italic);
    }

    [Fact]
    public void EngineData_ParsesEscapedUtf16StringsAndNesting()
    {
        // 「)」的 UTF-16 高位元組是 0x00，低位元組 0x29 得跳脫；反斜線同理
        var payload = new MemoryStream();
        payload.Write("<< /A [ 1.5 -2 .5 true false ] /S "u8);
        payload.Write(Desc.EngineString("a)b\\c"));
        payload.Write(" /D << /X 3 >> >>"u8);

        var root = PsdEngineData.Parse(payload.ToArray());
        var list = Assert.IsType<List<object?>>(root["A"]);
        Assert.Equal(new object[] { 1.5, -2.0, 0.5, true, false }, list);
        Assert.Equal("a)b\\c", root["S"]);
        Assert.Equal(3.0, PsdEngineData.Number(root, "D", "X"));
    }

    // ---- 依規格現寫的描述子與 EngineData ----

    internal static class Desc
    {
        public readonly record struct Enm(string Type, string Value);
        public readonly record struct Unit(string Kind, double Value);
        public sealed record Obj(string ClassId, (string Key, object Value)[] Items);

        public static Enm Enum(string type, string value) => new(type, value);
        public static Unit Px(double v) => new("#Pxl", v);
        public static Unit Prc(double v) => new("#Prc", v);
        public static Unit Ang(double v) => new("#Ang", v);
        public static Unit Pnt(double v) => new("#Pnt", v);
        public static Obj Rgb(double r, double g, double b) => new("RGBC", [("Rd  ", r), ("Grn ", g), ("Bl  ", b)]);

        /// <summary>一個效果實例：預設開啟。</summary>
        public static Obj Fx(params (string Key, object Value)[] items)
        {
            var all = new List<(string, object)> { ("enab", true), ("present", true) };
            foreach (var item in items)
            {
                all.RemoveAll(x => x.Item1 == item.Key);
                all.Add(item);
            }
            return new Obj("DrSh", all.ToArray());
        }

        public static byte[] Lfx2(params (string Key, object Value)[] effects)
        {
            var s = new MemoryStream();
            U32(s, 0);
            U32(s, 16);
            var items = new List<(string, object)> { ("Scl ", Prc(486.11)), ("masterFXSwitch", true) };
            items.AddRange(effects);
            WriteDescriptor(s, new Obj("null", items.ToArray()));
            return s.ToArray();
        }

        public static byte[] TySh(byte[] engine, double tx, double ty, double boundsLeft, double boundsRight, string text,
            string warpStyle = "warpNone", double xx = 1, double xy = 0, double yx = 0, double yy = 1)
        {
            var s = new MemoryStream();
            U16(s, 1);
            foreach (var v in new[] { xx, xy, yx, yy, tx, ty }) F64(s, v);
            U16(s, 50);
            U32(s, 16);
            WriteDescriptor(s, new Obj("TxLr",
            [
                ("Txt ", text),
                ("Ornt", Enum("Ornt", "Hrzn")),
                ("bounds", new Obj("bounds", [("Left", Pnt(boundsLeft)), ("Top ", Pnt(-30)), ("Rght", Pnt(boundsRight)), ("Btom", Pnt(10))])),
                ("EngineData", engine),
            ]));
            U16(s, 1);
            U32(s, 16);
            WriteDescriptor(s, new Obj("warp", [("warpStyle", Enum("warpStyle", warpStyle)), ("warpValue", 0.0)]));
            foreach (var v in new[] { 0.0, 0.0, 0.0, 0.0 }) F64(s, v);
            return s.ToArray();
        }

        public static byte[] EngineData(string text, string fontName, double fontSize, double tracking, double[] fill,
            int justification, double autoLeading)
        {
            var s = new MemoryStream();
            void W(string t) => s.Write(Encoding.ASCII.GetBytes(t));
            W("<< /EngineDict << /Editor << /Text ");
            s.Write(EngineString(text));
            W(" >> /ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification ");
            W($"{justification} /AutoLeading {autoLeading:0.0###} >> >> >> ] /RunLengthArray [ {text.Length} ] >>");
            W(" /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /Font 0 /FontSize ");
            W($"{fontSize:0.0###} /Tracking {tracking:0.0###} /AutoLeading true /FillColor << /Type 1 /Values [ ");
            W(string.Join(' ', fill.Select(v => v.ToString("0.0###"))));
            W($" ] >> >> >> >> ] /RunLengthArray [ {text.Length} ] >> >>");
            W(" /ResourceDict << /TheNormalStyleSheet 0 /StyleSheetSet [ << /Name ");
            s.Write(EngineString("Normal"));
            W(" /StyleSheetData << /Font 1 /FontSize 12.0 /FauxBold false /FauxItalic false /Underline false /Strikethrough false /HorizontalScale 1.0 /VerticalScale 1.0 >> >> ]");
            W(" /FontSet [ << /Name ");
            s.Write(EngineString(fontName));
            W(" /Script 0 /FontType 1 >> << /Name ");
            s.Write(EngineString("AdobeInvisFont"));
            W(" /Script 0 /FontType 0 >> ] >> >>");
            return s.ToArray();
        }

        /// <summary>EngineData 的字串：( + FE FF + UTF-16BE + )，括號與反斜線逐位元組跳脫。</summary>
        public static byte[] EngineString(string value)
        {
            var s = new MemoryStream();
            s.WriteByte((byte)'(');
            s.WriteByte(0xFE);
            s.WriteByte(0xFF);
            foreach (var b in Encoding.BigEndianUnicode.GetBytes(value))
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\') s.WriteByte((byte)'\\');
                s.WriteByte(b);
            }
            s.WriteByte((byte)')');
            return s.ToArray();
        }

        private static void WriteDescriptor(Stream s, Obj obj)
        {
            UnicodeString(s, "");
            Key(s, obj.ClassId);
            U32(s, (uint)obj.Items.Length);
            foreach (var (key, value) in obj.Items)
            {
                Key(s, key);
                WriteItem(s, value);
            }
        }

        private static void WriteItem(Stream s, object value)
        {
            switch (value)
            {
                case Obj o: Ascii(s, "Objc"); WriteDescriptor(s, o); break;
                case List<object> list:
                    Ascii(s, "VlLs");
                    U32(s, (uint)list.Count);
                    foreach (var item in list) WriteItem(s, item);
                    break;
                case double d: Ascii(s, "doub"); F64(s, d); break;
                case Unit u: Ascii(s, "UntF"); Ascii(s, u.Kind); F64(s, u.Value); break;
                case string str: Ascii(s, "TEXT"); UnicodeString(s, str); break;
                case Enm e: Ascii(s, "enum"); Key(s, e.Type); Key(s, e.Value); break;
                case int i: Ascii(s, "long"); U32(s, (uint)i); break;
                case bool b: Ascii(s, "bool"); s.WriteByte((byte)(b ? 1 : 0)); break;
                case byte[] raw: Ascii(s, "tdta"); U32(s, (uint)raw.Length); s.Write(raw); break;
                default: throw new ArgumentException($"沒有這種描述子值：{value.GetType().Name}");
            }
        }

        private static void Key(Stream s, string key)
        {
            if (key.Length == 4)
            {
                U32(s, 0);
                Ascii(s, key);
            }
            else
            {
                U32(s, (uint)key.Length);
                Ascii(s, key);
            }
        }

        private static void UnicodeString(Stream s, string value)
        {
            U32(s, (uint)(value.Length + 1));
            s.Write(Encoding.BigEndianUnicode.GetBytes(value + "\0"));
        }

        private static void Ascii(Stream s, string v) => s.Write(Encoding.ASCII.GetBytes(v));
        private static void U16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
        private static void U32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        private static void F64(Stream s, double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); s.Write(b); }
    }
}
