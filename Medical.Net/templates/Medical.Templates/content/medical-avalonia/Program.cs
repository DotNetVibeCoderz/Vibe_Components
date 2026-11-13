using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using FellowOakDicom;
using Medical.AI;
using Medical.Core;
using Medical.Dicom;

namespace MedicalDicomViewer;

// Medical.Net DICOM viewer — Made by Gravicode Studios, led by Kang Fadhil.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new ViewerWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ViewerWindow : Window
{
    private IReadOnlyList<DicomFile> _series = [];
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Slider _slice = new() { Minimum = 0, Maximum = 0, Width = 260, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly ComboBox _window = new() { ItemsSource = WindowPreset.Ct.Select(p => p.Name).ToArray(), SelectedIndex = 0, Width = 150 };
    private readonly TextBlock _info = new() { FontFamily = new FontFamily("Consolas, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _ai = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public ViewerWindow()
    {
        Title = "Medical.Net DICOM Viewer";
        Width = 1100; Height = 760;
        Background = new SolidColorBrush(Color.Parse("#0C1116"));
        var open = new Button { Content = "Open folder…" };
        var demo = new Button { Content = "Synthetic head CT" };
        var screen = new Button { Content = "Screen for haemorrhage" };
        open.Click += async (_, _) => await OpenFolder();
        demo.Click += (_, _) => Load(DicomPhantom.CreateSeries(PhantomKind.Head, 24, 256));
        screen.Click += (_, _) => _ai.Text = _series.Count == 0 ? "Load a series first." : HemorrhageScreener.Analyze(_series).Impression.En;
        _slice.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) Render(); };
        _window.SelectionChanged += (_, _) => Render();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12), Children = { open, demo, _window, _slice, screen } };
        var side = new StackPanel { Width = 300, Margin = new Thickness(12), Children = { _info, _ai, new TextBlock { Text = MedicalInfo.Credit, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 20, 0, 0) } } };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(bar);
        root.Children.Add(side);
        root.Children.Add(_image);
        Content = root;
        Load(DicomPhantom.CreateSeries(PhantomKind.Head, 24, 256));
    }

    private async Task OpenFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder with DICOM files" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        var files = Directory.EnumerateFiles(path).Where(f => DicomFile.HasValidHeader(f)).Select(DicomImaging.Open)
            .OrderBy(f => DicomImaging.Describe(f.Dataset).InstanceNumber ?? 0).ToArray();
        if (files.Length > 0) Load(files);
    }

    private void Load(IReadOnlyList<DicomFile> series)
    {
        _series = series;
        _slice.Maximum = series.Count - 1;
        _slice.Value = series.Count / 2;
        _ai.Text = "";
        Render();
    }

    private void Render()
    {
        if (_series.Count == 0) return;
        var ds = _series[(int)_slice.Value].Dataset;
        var preset = WindowPreset.Ct[Math.Max(0, _window.SelectedIndex)];
        var img = DicomImaging.Render(ds, preset);
        using var ms = new MemoryStream(img.ToPng());
        _image.Source = new Bitmap(ms);
        var i = DicomImaging.Describe(ds);
        var s = DicomImaging.Statistics(ds);
        _info.Text = $"{i.PatientName}\n{i.Modality} · {i.StudyDescription}\n{i.SeriesDescription}\nSlice {i.InstanceNumber}/{_series.Count} · {i.Columns}×{i.Rows}\n" +
                     $"Window {preset.Name} C{preset.Center} W{preset.Width}\nHU mean {s.Mean:0} · min {s.Min:0} · max {s.Max:0}";
    }
}
