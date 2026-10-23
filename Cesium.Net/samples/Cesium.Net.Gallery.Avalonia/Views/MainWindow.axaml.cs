using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using CesiumNet.Avalonia;
using CesiumNet.Gallery.Avalonia.ViewModels;

namespace CesiumNet.Gallery.Avalonia.Views;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<TokenKind, IBrush> TokenBrushes = new()
    {
        [TokenKind.Keyword] = new SolidColorBrush(Color.Parse("#8EA9FF")),
        [TokenKind.Type] = new SolidColorBrush(Color.Parse("#7FD6C2")),
        [TokenKind.String] = new SolidColorBrush(Color.Parse("#F5C518")),
        [TokenKind.Comment] = new SolidColorBrush(Color.Parse("#6F7D88")),
        [TokenKind.Number] = new SolidColorBrush(Color.Parse("#F79E6B")),
        [TokenKind.Method] = new SolidColorBrush(Color.Parse("#E8EDF1")),
    };

    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowViewer();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.ViewerRequested -= ShowViewer;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.ViewerRequested += ShowViewer;
    }

    /// <summary>Creates a fresh viewer for the current sample (or the home globe).</summary>
    private void ShowViewer()
    {
        if (_vm is null || !IsLoaded) return;
        HomeViewerHost.Content = null;
        ViewerHost.Content = null;

        if (_vm.Current is { } sample)
        {
            var view = new CesiumView { Options = sample.Options };
            view.Ready += (_, e) => sample.Start(e.Controller);
            view.LoadFailed += (_, e) =>
            {
                sample.LoadFailed = true;
                sample.Log("⚠ " + _vm.L.Get(GalleryText.LoadFailed) + " " + e.Message, true);
            };
            ViewerHost.Content = view;
            RenderCode(sample.Code);
        }
        else
        {
            var view = new CesiumView
            {
                Options = new CesiumViewerOptions
                {
                    BaseLayer = ImageryLayerOptions.Satellite(),
                    InitialView = CameraView.At(112, -4, 17_000_000),
                    InfoBox = false,
                    SelectionIndicator = false,
                    Events = new CesiumEventOptions { Click = false, CameraChanged = false },
                },
            };
            view.Ready += async (_, e) =>
            {
                await e.Controller.Scene.ConfigureAsync(new SceneSettings { FogEnabled = true });                await e.Controller.Camera.StartSpinAsync(3);
            };
            HomeViewerHost.Content = view;
        }
    }

    private void RenderCode(string code)
    {
        var inlines = new InlineCollection();
        foreach (var token in CodeTokenizer.Tokenize(code))
        {
            var run = new Run(token.Text);
            if (TokenBrushes.TryGetValue(token.Kind, out var brush)) run.Foreground = brush;
            if (token.Kind == TokenKind.Comment) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
        }
        CodeBlock.Inlines = inlines;
    }

    private async void OnCopyCode(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Current is not { } sample || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(sample.Code);
        CopyButton.Content = _vm.L.Get(GalleryText.Copied);
    }
}
