using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MinePainter.App.Views;
using MinePainter.Core.Documents;
using Xunit;

namespace MinePainter.App.Tests;

/// <summary>
/// 「新增影像」對話框：像素與實體單位（公分／英寸）＋解析度的換算，以及外部帶入的尺寸。
/// 文件內部永遠是像素 + dpi，其他單位只是換算著顯示。
/// </summary>
public class NewDocumentWindowTests
{
    private static NewDocumentWindow Open()
    {
        var window = new NewDocumentWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void 切到公分再輸入_換算回像素()
    {
        var window = Open();
        window.SelectPreset("Full HD");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((1920, 1080), (window.WidthPixels, window.HeightPixels));
        Assert.Equal(PhysicalUnits.ScreenDpi, window.CurrentDpi);

        window.SelectUnit(LengthUnit.Centimeter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1920 / 96.0 * 2.54, window.ShownWidth, 1);   // 50.8 公分

        window.EnterWidth(21);   // A4 的寬
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PhysicalUnits.ToPixels(21, LengthUnit.Centimeter, 96), window.WidthPixels);
        window.Close();
    }

    [AvaloniaFact]
    public void 用實體單位時改解析度_實體尺寸不變像素跟著變()
    {
        var window = Open();
        window.SelectPreset("A4");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((2480, 3508, 300.0), (window.WidthPixels, window.HeightPixels, window.CurrentDpi));
        Assert.Equal(LengthUnit.Millimeter, window.CurrentUnit);   // 印刷預設集自動切到公釐

        window.EnterResolution(150);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(150.0, window.CurrentDpi);
        Assert.InRange(window.WidthPixels, 1239, 1241);   // 210 mm @150 dpi
        Assert.InRange(window.HeightPixels, 1753, 1755);

        // 用像素在看的話，改解析度像素不動
        window.SelectUnit(LengthUnit.Pixel);
        window.EnterResolution(300);
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(window.WidthPixels, 1239, 1241);
        Assert.Contains("dpi", window.InfoText);
        window.Close();
    }

    [AvaloniaFact]
    public void 剪貼簿尺寸帶進來()
    {
        var window = Open();
        window.SuggestSize(640, 360, "剪貼簿的影像");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((640, 360), (window.WidthPixels, window.HeightPixels));
        Assert.Contains("640 × 360", window.InfoText);
        window.Close();
    }
}
