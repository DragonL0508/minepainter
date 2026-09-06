using MinePainter.Core.Adjustments;
using MinePainter.Core.Documents;
using MinePainter.Core.Effects;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// .psd 匯出（<see cref="PsdFormat.Save(Document, Stream, IProgress{double}?, out IReadOnlyList{string}, bool)"/>）：
/// 寫出去再用自己的讀取器讀回來，圖層樹、文字、圖層樣式、調整圖層都要對得上；
/// 對不上的效果要烙成像素並提示，不能悄悄少一層效果。
/// </summary>
public class PsdSaveTests
{
    private static Document RoundTrip(Document doc, out IReadOnlyList<string> saveWarnings, out IReadOnlyList<string> loadWarnings)
    {
        var stream = new MemoryStream();
        PsdFormat.Save(doc, stream, null, out saveWarnings);
        stream.Position = 0;
        return PsdFormat.Load(stream, out loadWarnings);
    }

    private static Document RoundTrip(Document doc, out IReadOnlyList<string> saveWarnings) => RoundTrip(doc, out saveWarnings, out _);

    private static RasterLayer FilledLayer(Document doc, string name, SKRectI rect, SKColor color)
    {
        var layer = new RasterLayer { Name = name };
        lock (doc.SyncRoot) layer.Surface.Fill(rect, color);
        return layer;
    }

    private static SKColor Pixel(RasterLayer layer, int x, int y)
    {
        var px = LayerEffectRenderer.ReadPixels(layer.Surface, new SKRectI(x - layer.Offset.X, y - layer.Offset.Y, x - layer.Offset.X + 1, y - layer.Offset.Y + 1))[0];
        var a = (byte)(px >> 24);
        var r = (byte)(px >> 16);
        var g = (byte)(px >> 8);
        var b = (byte)px;
        if (a == 0) return SKColors.Transparent;
        return new SKColor((byte)(r * 255 / a), (byte)(g * 255 / a), (byte)(b * 255 / a), a);
    }

    [Fact]
    public void 圖層樹_屬性_像素_寫出讀回都一樣()
    {
        using var doc = new Document(64, 48) { Dpi = 144 };
        doc.Root.Add(FilledLayer(doc, "底", new SKRectI(0, 0, 64, 48), SKColors.Red));
        var group = new GroupLayer { Name = "群組甲", Opacity = 0.5f, BlendMode = BlendMode.Multiply };
        group.Add(FilledLayer(doc, "藍方塊", new SKRectI(10, 5, 20, 15), new SKColor(0, 0, 255, 128)));
        group.Add(new RasterLayer { Name = "藏起來", IsVisible = false });
        doc.Root.Add(group);
        var top = FilledLayer(doc, "偏移", new SKRectI(0, 0, 4, 4), SKColors.Lime);
        top.Offset = new SKPointI(30, 20);
        top.BlendMode = BlendMode.Screen;
        doc.Root.Add(top);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(64, loaded.Width);
        Assert.Equal(48, loaded.Height);
        Assert.Equal(144, loaded.Dpi, 1);
        Assert.Equal(3, loaded.Root.Children.Count);

        var bottom = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
        Assert.Equal("底", bottom.Name);
        Assert.Equal(SKColors.Red, Pixel(bottom, 5, 5));

        var g = Assert.IsType<GroupLayer>(loaded.Root.Children[1]);
        Assert.Equal("群組甲", g.Name);
        Assert.Equal(0.5f, g.Opacity, 2);
        Assert.Equal(BlendMode.Multiply, g.BlendMode);
        Assert.Equal(2, g.Children.Count);
        var blue = Assert.IsType<RasterLayer>(g.Children[0]);
        Assert.Equal("藍方塊", blue.Name);
        var p = Pixel(blue, 12, 7);
        Assert.Equal(255, p.Blue);
        Assert.InRange(p.Alpha, 126, 130);
        Assert.Equal(SKColors.Transparent, Pixel(blue, 2, 2));
        Assert.False(g.Children[1].IsVisible);
        Assert.Equal("藏起來", g.Children[1].Name);

        var offset = Assert.IsType<RasterLayer>(loaded.Root.Children[2]);
        Assert.Equal(BlendMode.Screen, offset.BlendMode);
        Assert.Equal(SKColors.Lime, Pixel(offset, 31, 21));   // 圖層座標 (1,1) + 偏移 (30,20)
        Assert.Equal(SKColors.Transparent, Pixel(offset, 5, 5));
    }

    [Fact]
    public void 文字圖層_寫成可編輯的TySh_讀回排版參數與位置()
    {
        using var doc = new Document(400, 200);
        var layer = new RasterLayer { Name = "標題" };
        var text = new TextElement
        {
            Text = "Hello\nWorld",
            FontFamily = "Arial",
            FontSize = 36,
            FontWeight = 700,
            Bold = true,
            Italic = true,
            Underline = true,
            Color = new SKColor(10, 200, 30),
            Alignment = TextAlign.Center,
            Position = new SKPoint(120, 40),
            Rotation = 15,
            ScaleX = 1.5f,
            LetterSpacing = 3.6f,
            LineHeightScale = 1.4f,
        };
        layer.AddElement(text);
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings, out var loadWarnings);

        Assert.Empty(warnings);
        Assert.DoesNotContain(loadWarnings, w => w.Contains("轉成像素"));
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        Assert.True(back.IsTextLayer, "文字沒有寫成 TySh，讀回來變成像素圖層了");
        var t = Assert.IsType<TextElement>(Assert.Single(back.Elements));
        Assert.Equal("Hello\nWorld", t.Text);
        Assert.Equal("Arial", t.FontFamily);
        Assert.Equal(700, t.FontWeight);
        Assert.True(t.Bold);
        Assert.True(t.Italic);
        Assert.True(t.Underline);
        Assert.False(t.Strikethrough);
        Assert.Equal(new SKColor(10, 200, 30), t.Color);
        Assert.Equal(36f, t.FontSize, 1);
        Assert.Equal(TextAlign.Center, t.Alignment);
        Assert.Equal(15f, t.Rotation, 1);
        Assert.Equal(1.5f, t.ScaleX, 2);
        Assert.Equal(3.6f, t.LetterSpacing, 1);
        Assert.Equal(1.4f, t.LineHeightScale, 2);
        Assert.InRange(t.Position.X, 118.5f, 121.5f);
        Assert.InRange(t.Position.Y, 38.5f, 41.5f);
    }

    [Fact]
    public void 文字自己的外框陰影光暈_寫成圖層樣式_讀回變成效果堆疊()
    {
        using var doc = new Document(300, 100);
        var layer = new RasterLayer { Name = "花字" };
        layer.AddElement(new TextElement
        {
            Text = "Fx",
            FontSize = 40,
            Position = new SKPoint(20, 20),
            Stroke = new TextStroke { Color = SKColors.Blue, Width = 3 },
            Shadow = new TextShadow { Color = new SKColor(255, 0, 0, 191), Angle = 60, Distance = 10, Blur = 6 },
            Glow = new TextGlow { Color = new SKColor(0, 255, 0, 128), Size = 8, Spread = 4 },
        });
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Empty(warnings);
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        var t = Assert.IsType<TextElement>(Assert.Single(back.Elements));
        Assert.Null(t.Stroke);   // 讀取端把樣式統一掛成效果堆疊
        var effects = back.Effects.Select(e => e.Effect).ToList();
        var outline = Assert.Single(effects.OfType<ObjectOutlineEffect>());
        Assert.Equal(3, outline.Width);
        Assert.Equal(SKColors.Blue, outline.Color);
        var glow = Assert.Single(effects.OfType<ObjectGlowEffect>());
        Assert.Equal(8, glow.Size);
        Assert.Equal(4, glow.Spread);
        Assert.Equal(50, glow.Opacity);
        var shadow = Assert.Single(effects.OfType<ObjectShadowEffect>());
        Assert.Equal(5, shadow.OffsetX);   // 10 × cos 60°
        Assert.Equal(9, shadow.OffsetY);   // 10 × sin 60°
        Assert.Equal(6, shadow.Blur);
        Assert.Equal(75, shadow.Opacity);
        Assert.Equal(SKColors.Red, shadow.Color);
    }

    [Fact]
    public void 效果堆疊_對得上的寫成圖層樣式_讀回參數一致()
    {
        using var doc = new Document(100, 100);
        var layer = FilledLayer(doc, "方塊", new SKRectI(30, 30, 70, 70), SKColors.Orange);
        layer.SetEffects(
        [
            LayerEffect.Create(new ObjectFillEffect { Color = new SKColor(10, 20, 30), Opacity = 40 }),
            LayerEffect.Create(new InnerShadowEffect { Color = SKColors.Black, Opacity = 35, Angle = 120, Distance = 4, Size = 7, Choke = 20 }),
            LayerEffect.Create(new BevelEmbossEffect { Style = 1, Up = false, Size = 9, Depth = 150, Soften = 2, Angle = 45, Altitude = 60 }),
            LayerEffect.Create(new ObjectOutlineEffect { Width = 5, Color = SKColors.Blue, Position = 2 }),
            LayerEffect.Create(new ObjectGlowEffect { Color = SKColors.Yellow, Opacity = 85, Size = 12, Spread = 3 }),
            LayerEffect.Create(new ObjectShadowEffect { OffsetX = -6, OffsetY = 8, Blur = 5, Opacity = 60, Color = SKColors.Purple }) with { Enabled = false },
        ]);
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Empty(warnings);
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        Assert.Equal(SKColors.Orange, Pixel(back, 50, 50));
        var effects = back.Effects.ToList();

        var fill = Assert.Single(effects.Select(e => e.Effect).OfType<ObjectFillEffect>());
        Assert.Equal(new SKColor(10, 20, 30), fill.Color);
        Assert.Equal(40, fill.Opacity);

        var inner = Assert.Single(effects.Select(e => e.Effect).OfType<InnerShadowEffect>());
        Assert.Equal(35, inner.Opacity);
        Assert.Equal(120f, inner.Angle, 1);
        Assert.Equal(4, inner.Distance);
        Assert.Equal(7, inner.Size);
        Assert.Equal(20, inner.Choke);

        var bevel = Assert.Single(effects.Select(e => e.Effect).OfType<BevelEmbossEffect>());
        Assert.Equal(1, bevel.Style);
        Assert.False(bevel.Up);
        Assert.Equal(9, bevel.Size);
        Assert.Equal(150, bevel.Depth);
        Assert.Equal(2, bevel.Soften);
        Assert.Equal(45f, bevel.Angle, 1);
        Assert.Equal(60f, bevel.Altitude, 1);

        var outline = Assert.Single(effects.Select(e => e.Effect).OfType<ObjectOutlineEffect>());
        Assert.Equal(5, outline.Width);
        Assert.Equal(2, outline.Position);
        Assert.Equal(SKColors.Blue, outline.Color);

        var glow = Assert.Single(effects.Select(e => e.Effect).OfType<ObjectGlowEffect>());
        Assert.Equal(12, glow.Size);
        Assert.Equal(3, glow.Spread);

        // 關掉的效果：讀取端只收有開啟的，所以陰影不會出現 —— 但檔案裡要有它（enab false）
        Assert.DoesNotContain(effects.Select(e => e.Effect), e => e is ObjectShadowEffect);
    }

    [Fact]
    public void 對不上的效果_整層烙成像素並提示()
    {
        using var doc = new Document(100, 100);
        var layer = FilledLayer(doc, "糊掉", new SKRectI(40, 40, 60, 60), SKColors.Black);
        layer.SetEffects([LayerEffect.Create(new GaussianBlurEffect { Radius = 6 })]);
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("糊掉", warning);
        Assert.Contains("轉成像素", warning);
        var back = Assert.IsType<RasterLayer>(Assert.Single(loaded.Root.Children));
        Assert.Empty(back.Effects);
        // 模糊後邊緣外面 3px 處要有半透明像素（效果烙進去了），中心仍是黑的
        Assert.True(Pixel(back, 37, 50).Alpha > 0, "模糊效果沒有烙進像素");
        Assert.True(Pixel(back, 50, 50).Alpha > 200);
    }

    [Fact]
    public void 透視文字與形狀_烙成像素並提示()
    {
        using var doc = new Document(200, 100);
        var warped = new RasterLayer { Name = "歪字" };
        warped.AddElement(new TextElement { Text = "Skew", FontSize = 30, Position = new SKPoint(10, 10) }
            .Deformed(SKMatrix.CreateSkew(0.3f, 0)));
        doc.Root.Add(warped);
        var shape = new RasterLayer { Name = "圓" };
        shape.AddElement(new ShapeElement { Kind = ShapeKind.Ellipse, Rect = SKRect.Create(100, 20, 60, 40), FillColor = SKColors.Red });
        doc.Root.Add(shape);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("轉成像素", w));
        Assert.All(loaded.Root.Children, n => Assert.False(((RasterLayer)n).IsTextLayer));
        Assert.True(Pixel((RasterLayer)loaded.Root.Children[1], 130, 40).Red > 200);
    }

    [Fact]
    public void 調整圖層_寫成Photoshop調整圖層_讀回參數一致()
    {
        using var doc = new Document(50, 50);
        doc.Root.Add(FilledLayer(doc, "底", new SKRectI(0, 0, 50, 50), SKColors.Gray));
        doc.Root.Add(new AdjustmentLayer(new LevelsAdjustment(10, 200, 1.5f, 5, 250)) { Name = "色階" });
        doc.Root.Add(new AdjustmentLayer(new CurvesAdjustment
        {
            Mode = CurvesAdjustment.ModeRgb,
            Curves = [[(0f, 0f), (0.5f, 0.7f), (1f, 1f)], CurvesAdjustment.Identity, [(0f, 0.2f), (1f, 1f)]],
        }) { Name = "曲線" });
        doc.Root.Add(new AdjustmentLayer(new BrightnessContrastAdjustment(0.3f, -0.2f)) { Name = "亮對" });
        doc.Root.Add(new AdjustmentLayer(new HueSaturationAdjustment(40, 0.25f, -0.1f)) { Name = "色相", IsVisible = false });
        doc.Root.Add(new AdjustmentLayer(new ColorBalanceAdjustment { ShadowsRed = 10, MidtonesGreen = -20, HighlightsBlue = 30, PreserveLuminosity = false }) { Name = "平衡" });
        doc.Root.Add(new AdjustmentLayer(new ExposureAdjustment(1.5f, -0.1f, 0.8f)) { Name = "曝光" });
        doc.Root.Add(new AdjustmentLayer(new ThresholdAdjustment(90)) { Name = "臨界" });
        doc.Root.Add(new AdjustmentLayer(new InvertAdjustment()) { Name = "負片" });
        doc.Root.Add(new AdjustmentLayer(new PosterizeAdjustment(6, 6, 6)) { Name = "分離" });
        doc.Root.Add(new AdjustmentLayer(new PhotoFilterAdjustment { Color = new SKColor(0xEC, 0x8A, 0x00), Density = 40, PreserveLuminosity = true }) { Name = "濾鏡" });
        doc.Root.Add(new AdjustmentLayer(new SepiaAdjustment()) { Name = "懷舊" });

        using var loaded = RoundTrip(doc, out var warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("懷舊", warning);
        var nodes = loaded.Root.Children.Skip(1).ToList();
        Assert.Equal(10, nodes.Count);

        var levels = Assert.IsType<LevelsAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[0]).Adjustment);
        Assert.Equal("色階", nodes[0].Name);
        Assert.Equal((10, 200, 5, 250), (levels.InputLow, levels.InputHigh, levels.OutputLow, levels.OutputHigh));
        Assert.Equal(1.5f, levels.Gamma, 2);

        var curves = Assert.IsType<CurvesAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[1]).Adjustment);
        Assert.Equal(CurvesAdjustment.ModeRgb, curves.Mode);
        Assert.Equal(3, curves.Curves.Count);
        Assert.Equal(0.7f, curves.Curves[0][1].Y, 2);
        Assert.Equal(0.2f, curves.Curves[2][0].Y, 2);

        var bc = Assert.IsType<BrightnessContrastAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[2]).Adjustment);
        Assert.Equal(0.3f, bc.Brightness, 2);
        Assert.Equal(-0.2f, bc.Contrast, 2);

        var hue = Assert.IsType<HueSaturationAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[3]).Adjustment);
        Assert.Equal(40f, hue.Hue, 1);
        Assert.Equal(0.25f, hue.Saturation, 2);
        Assert.Equal(-0.1f, hue.Lightness, 2);
        Assert.False(nodes[3].IsVisible);

        var balance = Assert.IsType<ColorBalanceAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[4]).Adjustment);
        Assert.Equal((10, -20, 30, false), (balance.ShadowsRed, balance.MidtonesGreen, balance.HighlightsBlue, balance.PreserveLuminosity));

        var exposure = Assert.IsType<ExposureAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[5]).Adjustment);
        Assert.Equal(1.5f, exposure.Exposure, 3);
        Assert.Equal(-0.1f, exposure.Offset, 3);
        Assert.Equal(0.8f, exposure.Gamma, 3);

        Assert.Equal(90, Assert.IsType<ThresholdAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[6]).Adjustment).Level);
        Assert.IsType<InvertAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[7]).Adjustment);
        Assert.Equal(6, Assert.IsType<PosterizeAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[8]).Adjustment).Red);

        var filter = Assert.IsType<PhotoFilterAdjustment>(Assert.IsType<AdjustmentLayer>(nodes[9]).Adjustment);
        Assert.Equal(40, filter.Density);
        Assert.True(filter.PreserveLuminosity);
        Assert.InRange(filter.Color.Red, 0xE8, 0xF0);   // Lab 來回會差一點
        Assert.InRange(filter.Color.Green, 0x86, 0x8E);
        Assert.InRange(filter.Color.Blue, 0x00, 0x08);
    }

    [Fact]
    public void 效果堆疊裡的調整_寫成剪裁到該層的調整圖層()
    {
        using var doc = new Document(50, 50);
        var layer = FilledLayer(doc, "方塊", new SKRectI(10, 10, 40, 40), SKColors.Gray);
        layer.SetEffects([LayerEffect.Create(new AdjustmentEffect(new ThresholdAdjustment(77)))]);
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, loaded.Root.Children.Count);
        var back = Assert.IsType<RasterLayer>(loaded.Root.Children[0]);
        Assert.Empty(back.Effects);
        var adjustment = Assert.IsType<AdjustmentLayer>(loaded.Root.Children[1]);
        Assert.Equal(77, Assert.IsType<ThresholdAdjustment>(adjustment.Adjustment).Level);
    }

    [Fact]
    public void 快速模式_以輸出解析度寫出_文字跟著放大()
    {
        using var doc = new Document(200, 100);
        doc.SetOutputSize(400, 200);
        Assert.True(doc.IsFastMode);
        doc.Root.Add(FilledLayer(doc, "底", new SKRectI(0, 0, 200, 100), SKColors.White));
        var layer = new RasterLayer { Name = "字" };
        layer.AddElement(new TextElement { Text = "Big", FontSize = 20, Position = new SKPoint(10, 10) });
        doc.Root.Add(layer);

        using var loaded = RoundTrip(doc, out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(400, loaded.Width);
        Assert.Equal(200, loaded.Height);
        var text = Assert.IsType<TextElement>(Assert.Single(Assert.IsType<RasterLayer>(loaded.Root.Children[1]).Elements));
        Assert.Equal(40f, text.FontSize, 1);
        Assert.InRange(text.Position.X, 18f, 22f);
    }

    [Fact]
    public void 合成影像有透明度_讀取器認得負數圖層數()
    {
        using var doc = new Document(20, 20);
        doc.Root.Add(FilledLayer(doc, "點", new SKRectI(5, 5, 6, 6), SKColors.Red));

        var stream = new MemoryStream();
        PsdFormat.Save(doc, stream, null, out _);
        var bytes = stream.ToArray();

        Assert.Equal("8BPS"u8.ToArray(), bytes[..4]);
        Assert.Equal(4, bytes[13]);   // 通道數：R/G/B/A
        Assert.Equal(8, bytes[23]);   // 位元深度
        Assert.Equal(3, bytes[25]);   // RGB

        stream.Position = 0;
        using var loaded = PsdFormat.Load(stream, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(SKColors.Red, Pixel((RasterLayer)loaded.Root.Children[0], 5, 5));
    }
}
