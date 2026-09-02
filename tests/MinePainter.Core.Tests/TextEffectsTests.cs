using MinePainter.Core.Documents;
using MinePainter.Core.IO;
using MinePainter.Core.Layers;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>文字的外框／陰影與樣式（進階文字設定）。</summary>
public class TextEffectsTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"mpp_fx_{Guid.NewGuid():N}.mpp");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private static readonly TextElement Base = new()
    {
        Text = "AB",
        FontFamily = "Arial",
        FontSize = 40,
        Color = SKColors.Red,
        Position = new SKPoint(60, 60),
    };

    private static SKBitmap Render(TextElement element, int width = 400, int height = 240)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        element.Render(canvas);
        canvas.Flush();
        return bitmap;
    }

    private static int CountOf(SKBitmap bmp, SKColor color)
    {
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha > 200 && p.Red == color.Red && p.Green == color.Green && p.Blue == color.Blue)
                    count++;
            }
        }
        return count;
    }

    private static int CountVisible(SKBitmap bmp)
    {
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 32) count++;
        return count;
    }

    [Fact]
    public void Stroke_AddsOutlineWithoutEatingTheFill()
    {
        using var plain = Render(Base);
        using var outlined = Render(Base with
        {
            Stroke = new TextStroke { Color = SKColors.Blue, Width = 4 },
        });

        Assert.True(CountOf(outlined, SKColors.Blue) > 0, "外框應該畫出藍色的墨水");

        // 外框是「兩倍寬描邊畫在字身之下」→ 字身本身不該被吃掉
        var fillBefore = CountOf(plain, SKColors.Red);
        var fillAfter = CountOf(outlined, SKColors.Red);
        Assert.True(fillAfter >= fillBefore * 0.9,
            $"字身不該被外框蓋掉（{fillBefore} → {fillAfter}）");
    }

    [Fact]
    public void Shadow_LandsOnTheGivenDirection()
    {
        // 角度 0 = 正右方；距離拉開到不會和字身重疊
        using var shadowed = Render(Base with
        {
            Shadow = new TextShadow
            {
                Color = SKColors.Lime, Angle = 0, Distance = 40, Blur = 0,
            },
        });

        var leftOfText = 0;
        var rightOfText = 0;
        for (var y = 0; y < shadowed.Height; y++)
        {
            for (var x = 0; x < shadowed.Width; x++)
            {
                var p = shadowed.GetPixel(x, y);
                if (p.Alpha < 200 || p.Green < 200 || p.Red > 60) continue;
                if (x < 100) leftOfText++;
                else rightOfText++;
            }
        }
        Assert.True(rightOfText > 0, "角度 0 的陰影應該落在字的右邊");
        Assert.Equal(0, leftOfText);
    }

    [Fact]
    public void Bounds_GrowWithEffects()
    {
        var plain = Base.Bounds;
        var withStroke = (Base with { Stroke = new TextStroke { Width = 6 } }).Bounds;
        var withShadow = (Base with
        {
            Shadow = new TextShadow { Distance = 20, Blur = 10 },
        }).Bounds;

        Assert.True(withStroke.Width > plain.Width && withStroke.Height > plain.Height,
            "外框會長在字身外面，失效區必須跟著長大");
        Assert.True(withShadow.Width > plain.Width && withShadow.Height > plain.Height,
            "陰影會跑到字身外面，失效區必須跟著長大");
    }

    [Fact]
    public void NoEffects_RenderIsUnchanged()
    {
        // 加了效果通道之後，沒有效果的文字必須畫得和以前一模一樣
        using var bmp = Render(Base);
        using var bmp2 = Render(Base with { Stroke = null, Shadow = null });
        Assert.Equal(CountVisible(bmp), CountVisible(bmp2));
        Assert.True(CountOf(bmp, SKColors.Red) > 0);
    }

    [Fact]
    public void TextStyle_RoundTripsThroughElement()
    {
        var style = new TextStyle
        {
            FontFamily = "Arial",
            FontSize = 33,
            FontWeight = 700,
            Bold = true,
            Italic = true,
            Underline = true,
            Strikethrough = true,
            Alignment = TextAlign.Right,
            Color = SKColors.Magenta,
            Stroke = new TextStroke { Color = SKColors.Cyan, Width = 7 },
            Shadow = new TextShadow
            {
                Color = SKColors.Navy, Angle = 200, Distance = 9, Blur = 3,
            },
        };

        var element = new TextElement
        {
            Text = "內容",
            Position = new SKPoint(12, 34),
            ScaleX = 1.5f,
            Rotation = 20f,
        };
        var styled = style.ApplyTo(element);

        // 套樣式不能動到內容與擺放
        Assert.Equal("內容", styled.Text);
        Assert.Equal(new SKPoint(12, 34), styled.Position);
        Assert.Equal(1.5f, styled.ScaleX);
        Assert.Equal(20f, styled.Rotation);
        Assert.Equal(element.Id, styled.Id);

        Assert.Equal(style, TextStyle.From(styled));
    }

    [Fact]
    public void Effects_RoundTripThroughMpp()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 120, SKColors.White);
        var layer = (RasterLayer)doc.Root.Children[0];
        layer.AddElement(Base with
        {
            Stroke = new TextStroke { Color = new SKColor(1, 2, 3, 200), Width = 5.5f },
            Shadow = new TextShadow
            {
                Color = new SKColor(4, 5, 6, 128), Angle = 123f, Distance = 8f, Blur = 2.5f,
            },
        });

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        // 舊檔遷移：外框／陰影變成圖層效果
        var textLayer = Assert.IsType<RasterLayer>(loaded.Root.Children[1]);
        var text = Assert.IsType<TextElement>(Assert.Single(textLayer.Elements));
        Assert.Null(text.Stroke);
        Assert.Null(text.Shadow);
        var outline = Assert.IsType<Effects.ObjectOutlineEffect>(textLayer.Effects[0].Effect);
        Assert.Equal(new SKColor(1, 2, 3, 200), outline.Color);
        Assert.Equal(6, outline.Width);
        var shadow = Assert.IsType<Effects.ObjectShadowEffect>(textLayer.Effects[1].Effect);
        Assert.Equal(new SKColor(4, 5, 6), shadow.Color);
        Assert.Equal(50, shadow.Opacity);
        Assert.Equal(-4, shadow.OffsetX);
        Assert.Equal(7, shadow.OffsetY);
        Assert.InRange(shadow.Blur, 2, 3);
    }

    [Fact]
    public void PlainText_RoundTripsWithoutEffects()
    {
        // 舊檔沒有效果欄位 → 讀回來必須是 null，不能變成「寬度 0 的外框」
        using var doc = ImageCodec.CreateBlankDocument(200, 120, SKColors.White);
        ((RasterLayer)doc.Root.Children[0]).AddElement(Base);

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        var textLayer = Assert.IsType<RasterLayer>(loaded.Root.Children[1]);
        var text = Assert.IsType<TextElement>(Assert.Single(textLayer.Elements));
        Assert.Null(text.Stroke);
        Assert.Null(text.Shadow);
        Assert.Empty(textLayer.Effects);
    }

    [Fact]
    public void Gradient_PaintsFromStartToEndColor()
    {
        using var bmp = Render(Base with
        {
            Text = "ABAB",
            FontSize = 80,
            Gradient = new TextGradient
            {
                Start = SKColors.Red, End = SKColors.Blue, Angle = 90, // 上→下
            },
        });

        // 掃出字身的垂直範圍，上緣附近應該偏紅、下緣附近偏藍
        int top = bmp.Height, bottom = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha > 200) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        Assert.True(bottom > top, "應該有畫出字");

        int reddish = 0, bluish = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var pIx = bmp.GetPixel(x, y);
                if (pIx.Alpha < 200) continue;
                var upper = y < top + (bottom - top) / 4;
                var lower = y > bottom - (bottom - top) / 4;
                if (upper && pIx.Red > 180 && pIx.Blue < 80) reddish++;
                if (lower && pIx.Blue > 180 && pIx.Red < 80) bluish++;
            }
        }
        Assert.True(reddish > 0, "漸層起點（上緣）應該偏紅");
        Assert.True(bluish > 0, "漸層終點（下緣）應該偏藍");
    }

    [Fact]
    public void Glow_PaintsOutsideTheGlyphs()
    {
        using var plain = Render(Base);
        using var glowing = Render(Base with
        {
            Glow = new TextGlow { Color = SKColors.Lime, Size = 12, Spread = 3 },
        });

        Assert.True(CountVisible(glowing) > CountVisible(plain) * 1.2,
            "光暈會暈到字身外面，覆蓋面積必須明顯變大");

        // 字身仍是紅色（光暈畫在最底層，不能吃掉字身）
        Assert.True(CountOf(glowing, SKColors.Red) > CountOf(plain, SKColors.Red) * 0.9);
    }

    [Fact]
    public void LetterSpacing_WidensMeasuredLayout()
    {
        var normal = Base with { Text = "ABCD" };
        var spaced = normal with { LetterSpacing = 10f };

        // 每個字之後加 10px（含最後一個）→ 4 個字寬 40px
        Assert.True(spaced.UnscaledWidth >= normal.UnscaledWidth + 35f,
            $"字距 10px×4 字應該拉寬約 40px（{normal.UnscaledWidth} → {spaced.UnscaledWidth}）");
        Assert.True(spaced.Bounds.Width > normal.Bounds.Width);
    }

    [Fact]
    public void LineHeightScale_StretchesMultilineBounds()
    {
        var normal = Base with { Text = "A\nB" };
        var tall = normal with { LineHeightScale = 2.5f };
        Assert.True(tall.Bounds.Height > normal.Bounds.Height + Base.FontSize,
            "行高倍率拉大，多行文字的外框要跟著長高");
    }

    [Fact]
    public void NewEffects_RoundTripThroughMpp()
    {
        using var doc = ImageCodec.CreateBlankDocument(200, 120, SKColors.White);
        var layer = (RasterLayer)doc.Root.Children[0];
        layer.AddElement(Base with
        {
            Gradient = new TextGradient
            {
                Start = new SKColor(10, 20, 30), End = new SKColor(40, 50, 60),
                Angle = 135f, Radial = true,
            },
            Stroke = new TextStroke
            {
                Color = SKColors.White, Width = 4f,
                Gradient = new TextGradient { Start = SKColors.Red, End = SKColors.Blue, Angle = 45f },
            },
            Shadow = new TextShadow { Spread = 7f },
            Glow = new TextGlow { Color = new SKColor(7, 8, 9, 210), Size = 13f, Spread = 2.5f },
            LetterSpacing = 6f,
            LineHeightScale = 1.6f,
        });

        MppFormat.Save(doc, _tempPath);
        using var loaded = MppFormat.Load(_tempPath);

        // 舊檔遷移：漸層／外框／陰影／光暈 → 圖層效果堆疊；排版屬性（字距、行高）留在元素上
        var textLayer = Assert.IsType<RasterLayer>(loaded.Root.Children[1]);
        var text = Assert.IsType<TextElement>(Assert.Single(textLayer.Elements));
        Assert.Null(text.Gradient);
        Assert.Null(text.Stroke);
        Assert.Null(text.Shadow);
        Assert.Null(text.Glow);
        var gradient = Assert.IsType<Effects.ObjectGradientEffect>(textLayer.Effects[0].Effect);
        Assert.Equal(new SKColor(10, 20, 30), gradient.Start);
        Assert.Equal(new SKColor(40, 50, 60), gradient.End);
        Assert.Equal(135f, gradient.Angle);
        Assert.True(gradient.Radial);
        var outline = Assert.IsType<Effects.ObjectOutlineEffect>(textLayer.Effects[1].Effect);
        Assert.Equal(SKColors.White, outline.Color);
        Assert.IsType<Effects.ObjectShadowEffect>(textLayer.Effects[2].Effect);
        var glow = Assert.IsType<Effects.ObjectGlowEffect>(textLayer.Effects[3].Effect);
        Assert.Equal(new SKColor(7, 8, 9), glow.Color);
        Assert.Equal(13, glow.Size);
        Assert.InRange(glow.Spread, 2, 3);
        Assert.Equal(82, glow.Opacity);
        Assert.Equal(6f, text.LetterSpacing);
        Assert.Equal(1.6f, text.LineHeightScale);
    }

    [Fact]
    public void ApplyEffectsTo_KeepsTheFontHalf()
    {
        // 進階視窗調效果只送外觀半 —— 字型/字級/粗斜體/對齊絕不能被蓋掉（「字級被 reset」的回歸測試）
        var style = new TextStyle
        {
            FontFamily = "Times New Roman",
            FontSize = 99f,
            Bold = true,
            Alignment = TextAlign.Right,
            Color = SKColors.Magenta,
            Stroke = new TextStroke { Width = 5 },
            Glow = new TextGlow(),
            LetterSpacing = 4f,
        };

        var styled = style.ApplyEffectsTo(Base);

        Assert.Equal(Base.FontFamily, styled.FontFamily);
        Assert.Equal(Base.FontSize, styled.FontSize);
        Assert.Equal(Base.Bold, styled.Bold);
        Assert.Equal(Base.Alignment, styled.Alignment);

        Assert.Equal(SKColors.Magenta, styled.Color);
        Assert.Equal(5f, styled.Stroke!.Width);
        Assert.NotNull(styled.Glow);
        Assert.Equal(4f, styled.LetterSpacing);
    }
}
