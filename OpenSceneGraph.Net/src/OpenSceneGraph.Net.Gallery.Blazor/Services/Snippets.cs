namespace OpenSceneGraph.Net.Gallery.Web.Services;

public static class Snippets
{
    public const string UsingOsgCanvas = """
        // dotnet add package OpenSceneGraph.Net.Blazor
        @using OpenSceneGraph.Net.Blazor

        <OsgCanvas Scene="_scene" OnFrame="s => _fps = s.FramesPerSecond" />

        @code {
            private readonly Node _scene = new Group()
                .AddChild(Shapes.Cube(2, Color.Orange));
            private double _fps;
        }
        """;
}
