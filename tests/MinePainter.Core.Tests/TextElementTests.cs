using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

public class TextElementTests
{
    private static SKBitmap Render(TextElement element, int width = 500, int height = 300)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        element.Render(canvas);
        canvas.Flush();
        return bitmap;
    }

    private static int CountVisible(SKBitmap bmp, int left = 0, int top = 0, int? right = null, int? bottom = null)
    {
        var count = 0;
        for (var y = top; y < (bottom ?? bmp.Height); y++)
            for (var x = left; x < (right ?? bmp.Width); x++)
                if (bmp.GetPixel(x, y).Alpha > 32) count++;
        return count;
    }

    private static int LeftmostVisible(SKBitmap bmp, int top, int bottom)
    {
        for (var x = 0; x < bmp.Width; x++)
            for (var y = top; y < bottom; y++)
                if (bmp.GetPixel(x, y).Alpha > 32) return x;
        return -1;
    }

    private static readonly TextElement Base = new()
    {
        Text = "AB",
        FontFamily = "Arial",
        FontSize = 40,
        Color = SKColors.Red,
        Position = new SKPoint(20, 20),
    };

    [Fact]
    public void Underline_AddsInkBelowText()
    {
        using var plain = Render(Base);
        using var underlined = Render(Base with { Underline = true });
        Assert.True(CountVisible(underlined) > CountVisible(plain),
            "底線應該畫出額外的墨水");
    }

    [Fact]
    public void Strikethrough_AddsInk()
    {
        using var plain = Render(Base);
        using var struck = Render(Base with { Strikethrough = true });
        Assert.True(CountVisible(struck) > CountVisible(plain),
            "刪除線應該畫出額外的墨水");
    }

    [Fact]
    public void Alignment_ShiftsShorterLines()
    {
        // 第二行比第一行短：靠右對齊時第二行的起點應該比靠左時更靠右
        var multiline = Base with { Text = "WWWW\nW" };
        using var left = Render(multiline);
        using var right = Render(multiline with { Alignment = TextAlign.Right });
        using var center = Render(multiline with { Alignment = TextAlign.Center });

        var lineTop = (int)(multiline.Position.Y + multiline.LineHeight);
        var lineBottom = (int)(multiline.Position.Y + multiline.LineHeight * 2);
        var leftX = LeftmostVisible(left, lineTop, lineBottom);
        var centerX = LeftmostVisible(center, lineTop, lineBottom);
        var rightX = LeftmostVisible(right, lineTop, lineBottom);

        Assert.True(leftX >= 0 && centerX >= 0 && rightX >= 0, "三種對齊都應該畫得出第二行");
        Assert.True(centerX > leftX, $"置中應比靠左更靠右（{centerX} vs {leftX}）");
        Assert.True(rightX > centerX, $"靠右應比置中更靠右（{rightX} vs {centerX}）");
    }

    [Fact]
    public void Bounds_ContainsAllRenderedInk_WithStyles()
    {
        // 粗體 + 斜體（含合成後備）+ 底線 + 拉寬：所有墨水都要落在 Bounds 內（Bounds 是失效範圍的依據）
        var styled = Base with
        {
            Text = "測試 Wj",
            FontFamily = "Microsoft JhengHei",
            Bold = true,
            Italic = true,
            Underline = true,
            ScaleX = 1.4f,
        };
        using var bmp = Render(styled, 600, 300);
        var bounds = styled.Bounds;

        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Alpha <= 32) continue;
                Assert.True(bounds.Contains(x, y),
                    $"({x},{y}) 有墨水但落在 Bounds {bounds} 外");
            }
        }
    }

    [Fact]
    public void CjkFallback_WhenFamilyLacksGlyphs()
    {
        // Skia 的 DrawText 不做字型後備：Arial 沒有中文字面，沒有後備機制就會畫豆腐框。
        // 後備字型的 CJK 全形字寬 ≈ 1em，.notdef 框窄得多 —— 用寬度驗證走到了真字面。
        var el = Base with { Text = "中文字", FontFamily = "Arial" };
        Assert.True(el.UnscaledWidth > el.FontSize * 3 * 0.8f,
            $"寬度 {el.UnscaledWidth} 應接近 3 個全形字（後備字面），太窄表示畫的是豆腐框");

        using var bmp = Render(el);
        Assert.True(CountVisible(bmp) > 0, "後備字面應該畫得出字");

        // 中英混排：分段量測，總寬 ≈ 英文段 + 中文段
        var mixed = Base with { Text = "A中B", FontFamily = "Arial" };
        var latinOnly = Base with { Text = "AB", FontFamily = "Arial" };
        Assert.True(mixed.UnscaledWidth > latinOnly.UnscaledWidth + el.FontSize * 0.8f,
            $"混排寬 {mixed.UnscaledWidth} 應比純英文 {latinOnly.UnscaledWidth} 多約一個全形字");
    }

    [Fact]
    public void Rotation_InkStaysWithinBounds_AndHitTestFollows()
    {
        var rotated = Base with { Text = "ROT", Position = new SKPoint(250, 150), Rotation = 35f };
        using var bmp = Render(rotated, 500, 300);
        var bounds = rotated.Bounds;

        var inkCount = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Alpha <= 32) continue;
                inkCount++;
                Assert.True(bounds.Contains(x, y), $"({x},{y}) 有墨水但落在 Bounds {bounds} 外");
            }
        }
        Assert.True(inkCount > 0, "旋轉後應該畫得出東西");

        // HitTest 跟著旋轉：把未旋轉時必定在框內的點旋過去 → 命中；原本位置的遠角 → 不命中
        var m = SKMatrix.CreateRotationDegrees(35f);
        var inside = m.MapPoint(new SKPoint(20, 20));
        Assert.True(rotated.HitTest(new SKPoint(250 + inside.X, 150 + inside.Y)));
        Assert.False(rotated.HitTest(new SKPoint(250 + 60, 150 - 30))); // 未旋轉框的右上方向，旋轉後已離開
    }

    [Fact]
    public void SyntheticBold_WidensInk_WhenFamilyLacksBoldFace()
    {
        // 即使字型家族沒有粗體字面（Embolden 後備），粗體也要看得出差異
        using var plain = Render(Base with { Text = "HHH" });
        using var bold = Render(Base with { Text = "HHH", Bold = true });
        Assert.True(CountVisible(bold) > CountVisible(plain),
            "粗體應該畫出更多墨水");
    }
}
