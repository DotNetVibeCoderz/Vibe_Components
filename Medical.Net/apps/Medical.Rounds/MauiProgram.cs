namespace Medical.Rounds;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().ConfigureFonts(fonts =>
        {
            fonts.AddFont("AtkinsonHyperlegible-Regular.ttf", "Body");
            fonts.AddFont("AtkinsonHyperlegible-Bold.ttf", "BodyBold");
            fonts.AddFont("IBMPlexMono-Medium.ttf", "Mono");
            fonts.AddFont("IBMPlexSansCondensed-Bold.ttf", "Display");
        });
        return builder.Build();
    }
}
