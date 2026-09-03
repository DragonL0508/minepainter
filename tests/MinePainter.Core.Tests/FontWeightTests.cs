using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.Core.Tests;

/// <summary>
/// 字重解析：系統裝了那支家族就用系統的（才有 Bold／Black），沒有才退回內嵌的保底字型。
/// </summary>
public class FontWeightTests
{
    /// <summary>系統裡同時有 Regular 與 Bold 的家族；找不到就回 null（該機器上不測）。</summary>
    private static string? MultiWeightFamily()
    {
        foreach (var family in SKFontManager.Default.FontFamilies)
        {
            using var set = SKFontManager.Default.GetFontStyles(family);
            var hasRegular = false;
            var hasBold = false;
            for (var i = 0; i < set.Count; i++)
            {
                if (set[i].Slant != SKFontStyleSlant.Upright) continue;
                if (set[i].Weight == 400) hasRegular = true;
                if (set[i].Weight >= 700) hasBold = true;
            }
            if (hasRegular && hasBold) return family;
        }
        return null;
    }

    [Fact]
    public void Resolve_HonoursRequestedWeight()
    {
        if (MultiWeightFamily() is not { } family) return; // 這台機器沒有多字重家族

        using var bold = BundledFont.Resolve(family,
            new SKFontStyle(700, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        Assert.NotNull(bold);
        Assert.True(bold!.FontWeight >= 600,
            $"{family} 要 Bold 卻拿到字重 {bold.FontWeight}（家族名被保底字型接走了？）");
    }

    [Fact]
    public void Resolve_ReturnsNull_ForFamilyNobodyHas()
    {
        // 沒註冊保底字型（Core 測試不載入 App 的內嵌字型），系統也不會有這個名字：
        // Skia 的 FromFamilyName 不會回 null，會悄悄給一支預設字面 —— Resolve 必須擋掉
        using var missing = BundledFont.Resolve("MinePainter No Such Family 12345",
            new SKFontStyle(400, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        Assert.Null(missing);
    }

    [Fact]
    public void TextElement_RendersDifferentWeights()
    {
        if (MultiWeightFamily() is not { } family) return;

        var regular = new TextElement
        {
            Text = "Weight", FontFamily = family, FontSize = 64, FontWeight = 400, Position = new SKPoint(0, 0),
        };
        var black = regular with { FontWeight = 900 };

        // 粗體的著墨範圍應該比 Regular 寬（同一段字、同一個字級）
        Assert.True(black.FrameBounds.Width > regular.FrameBounds.Width,
            $"字重沒有作用：Regular {regular.FrameBounds.Width} vs 900 {black.FrameBounds.Width}");
    }
}
