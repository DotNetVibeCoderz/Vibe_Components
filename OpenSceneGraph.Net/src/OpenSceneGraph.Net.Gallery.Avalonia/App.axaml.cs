using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenSceneGraph.Net.Gallery.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var opts = Program.Options;
            if (opts.Lang is { } lang) Loc.Instance.Lang = lang;
            if (opts.ScreenshotTarget is not null)
            {
                desktop.MainWindow = new ScreenshotWindow(opts);
            }
            else
            {
                var main = new MainWindow();
                desktop.MainWindow = main;
                if (opts.UiScreenshotPath is not null) _ = UiScreenshot.RunAsync(main, opts, desktop);
                else if (opts.OpenSample is { } id) main.Opened += (_, _) => main.OpenSample(id);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
