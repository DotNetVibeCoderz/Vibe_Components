using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GDAL.Net.Gallery.Avalonia.ViewModels;
using GDAL.Net.Gallery.Avalonia.Views;

namespace GDAL.Net.Gallery.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(new SampleContext());
            desktop.MainWindow = new MainWindow { DataContext = vm };
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
