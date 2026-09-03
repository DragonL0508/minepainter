using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MinePainter.App.Tests.TestAppBuilder))]

namespace MinePainter.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

public class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new Material.Icons.Avalonia.MaterialIconStyles(null));
        // 測試要驗的是「出貨的那份樣式」，不是另外寫一份
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MinePainter.App/"))
        {
            Source = new Uri("avares://MinePainter.App/Styles/Popups.axaml"),
        });
    }
}
