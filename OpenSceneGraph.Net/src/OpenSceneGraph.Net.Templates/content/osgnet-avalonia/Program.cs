// OpenSceneGraph.Net Avalonia app - Created by Gravicode Studios, led by Kang Fadhil.
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Avalonia;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Particles;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;
using Color = OpenSceneGraph.Net.State.Color;

namespace OsgNetAvaloniaApp;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "OsgNetAvaloniaApp - OpenSceneGraph.Net";
        Width = 1280;
        Height = 760;

        var view = new OsgView { Scene = BuildScene() };
        view.Viewer.Environment.Shadows.Enabled = true;
        view.Viewer.Environment.Shadows.Radius = 20;

        var status = new TextBlock { Margin = new Thickness(12, 6), Foreground = Brushes.LightGray };
        view.FrameRendered += (_, stats) => status.Text = stats.ToString();

        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        dock.Children.Add(view);
        Content = dock;
    }

    private static Node BuildScene()
    {
        var root = new Group();
        root.AddChild(Shapes.Plane(40, 40, 1, 1, Color.FromHex("#2b2d42")));
        root.AddChild(new LightSource(Light.Sun(new Vector3(-0.4f, 0.5f, -0.8f))));
        for (int i = 0; i < 6; i++)
        {
            var pat = new PositionAttitudeTransform { Position = new Vector3(MathF.Cos(i) * 8, MathF.Sin(i) * 8, 1.5f) };
            pat.AddChild(Shapes.Cube(3, Color.FromHsv(i * 60, 0.6f, 0.95f)));
            pat.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 20 + i * 10));
            root.AddChild(pat);
        }
        root.AddChild(ParticleEffects.Fire(Vector3.Zero, 1.5f));
        return root;
    }
}
