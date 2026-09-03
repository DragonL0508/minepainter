using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using MinePainter.App.Services;
using MinePainter.Core.Vectors;
using SkiaSharp;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 內嵌保底字型：英文版 Windows 可能一支中日韓字型都沒有（Features on Demand），
/// 這支字型是那種機器上中文不變豆腐框的唯一保障，所以路徑、家族名、Skia 註冊都要守住。
/// </summary>
public class EmbeddedFontTests
{
    private static void EnsureRegistered() => EmbeddedFonts.Register();

    [AvaloniaFact]
    public void 字型資源存在於_avares()
    {
        Assert.True(AssetLoader.Exists(new Uri("avares://MinePainter.App/Assets/Fonts/NotoSansTC-Regular.otf")));
    }

    [AvaloniaFact]
    public void 系統裝了內嵌字型的同名家族時_字重列得出來()
    {
        EnsureRegistered();
        using var set = SKFontManager.Default.GetFontStyles(EmbeddedFonts.FamilyName);
        if (set.Count == 0) return; // 這台機器沒安裝 Noto Sans TC，只有內嵌的 Regular

        var styles = FontCatalog.StylesFor(EmbeddedFonts.FamilyName);
        Assert.Contains(styles, s => s.Weight >= 700); // 內嵌那份只有 Regular，不能讓它接走整個家族
    }

    [AvaloniaFact]
    public void 掛了內嵌字型之後_同名家族仍選得到粗體()
    {
        // 這是回歸測試的重點場景：內嵌的 Noto Sans TC 只有 Regular，
        // 一旦它以家族名接走整個家族，選 Bold／Black 也只會畫 Regular。
        EnsureRegistered();
        Assert.NotNull(BundledFont.Typeface);

        using var set = SKFontManager.Default.GetFontStyles(EmbeddedFonts.FamilyName);
        if (set.Count == 0) return; // 這台機器沒安裝，內嵌的 Regular 就是唯一選擇

        using var bold = BundledFont.Resolve(EmbeddedFonts.FamilyName,
            new SKFontStyle(900, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        Assert.NotNull(bold);
        Assert.True(bold!.FontWeight >= 600, $"要 Black 卻拿到字重 {bold.FontWeight}（被內嵌字型接走了）");

        var regular = new TextElement
        {
            Text = "字重測試", FontFamily = EmbeddedFonts.FamilyName, FontSize = 64, FontWeight = 400,
        };
        var black = regular with { FontWeight = 900 };
        Assert.True(black.FrameBounds.Width > regular.FrameBounds.Width, "字重對算繪沒有作用");
    }

    [AvaloniaFact]
    public void 家族位址的名稱與家族名一致()
    {
        // FamilyUri 的 # 後半打錯，Avalonia 端會靜默退回預設字型（照樣豆腐框）
        Assert.Equal(EmbeddedFonts.FamilyName, FontFamily.Parse(EmbeddedFonts.FamilyUri).Name);
    }

    [AvaloniaFact]
    public void 註冊後_Core_拿得到保底字型且含中文()
    {
        EnsureRegistered();
        Assert.NotNull(BundledFont.Typeface);
        Assert.Equal(EmbeddedFonts.FamilyName, BundledFont.FamilyName);
        Assert.NotNull(BundledFont.Match('中'));
        Assert.NotNull(BundledFont.Match('繁'));
    }

    [AvaloniaFact]
    public void 選內嵌字型畫中文畫得出來()
    {
        EnsureRegistered();
        // 系統沒安裝這支家族，只能從 BundledFont 取；取不到就會畫成空白或豆腐
        var element = new TextElement
        {
            Text = "中文",
            FontFamily = EmbeddedFonts.FamilyName,
            FontSize = 48,
            Color = SKColors.Red,
            Position = new SKPoint(10, 10),
        };

        var bitmap = new SKBitmap(new SKImageInfo(300, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            element.Render(canvas);
            canvas.Flush();
        }

        var visible = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 32) visible++;

        Assert.True(visible > 200, $"畫出來的像素只有 {visible} 個");
    }

    [AvaloniaFact]
    public void 字型清單含內嵌字型()
    {
        Assert.Contains(EmbeddedFonts.FamilyName, FontCatalog.Families);
    }
}
