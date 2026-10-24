using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GnuRadio.Avalonia;

namespace GnuRadio.Gallery.Desktop;

public sealed class App : Application
{
    public const string FontRoot = "avares://GnuRadio.Net.Gallery.Desktop/Assets/Fonts";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        GrTheme.DataFont = new FontFamily($"{FontRoot}#IBM Plex Mono");
        GrTheme.DisplayFont = new FontFamily($"{FontRoot}#Chakra Petch");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
