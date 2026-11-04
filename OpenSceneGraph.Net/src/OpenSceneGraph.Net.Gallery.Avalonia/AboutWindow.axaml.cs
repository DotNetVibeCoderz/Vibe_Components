using Avalonia.Controls;

namespace OpenSceneGraph.Net.Gallery.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var v = typeof(Viewer).Assembly.GetName().Version;
        VersionText.Text = $"{Loc.Instance["Version"]} {v?.ToString(3)} · .NET {Environment.Version.ToString(2)} · Avalonia 11.3";
        CloseButton.Click += (_, _) => Close();
    }
}
