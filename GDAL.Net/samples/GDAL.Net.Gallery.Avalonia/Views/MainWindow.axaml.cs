using Avalonia.Controls;
using Avalonia.Interactivity;
using GDAL.Net.Gallery.Avalonia.ViewModels;

namespace GDAL.Net.Gallery.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SampleMap.FeatureClicked += (_, e) => (DataContext as MainViewModel)?.Current?.OnFeatureClicked(e);
        ComponentsMap.FeatureClicked += (_, e) => (DataContext as MainViewModel)?.Components?.OnFeatureClicked(e);
    }

    private void OnEnglish(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.IsIndonesian = false;
    }

    private void OnIndonesian(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.IsIndonesian = true;
    }
}
