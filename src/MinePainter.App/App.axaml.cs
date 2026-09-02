using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MinePainter.App.Views;

namespace MinePainter.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 主視窗建立前先套使用者設定（主題／畫布背景圖），開起來就是對的樣子
        var settings = Services.AppSettings.Instance;
        AppTheme.Apply(settings.Theme);
        CanvasBackdrop.Set(settings.BackdropPath, settings.BackdropOpacity);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
