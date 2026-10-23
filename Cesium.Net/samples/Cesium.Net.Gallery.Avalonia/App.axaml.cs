using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CesiumNet.Gallery.Avalonia.ViewModels;
using CesiumNet.Gallery.Avalonia.Views;

namespace CesiumNet.Gallery.Avalonia;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var start = args.SkipWhile(a => a != "--sample").Skip(1).FirstOrDefault();
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(start) };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
