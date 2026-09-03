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
