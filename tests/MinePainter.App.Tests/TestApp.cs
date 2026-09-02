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
    }
}
