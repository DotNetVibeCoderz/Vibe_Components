namespace GDAL.Net.Gallery.Blazor.Services;

/// <summary>Generates the gallery sample data once at startup and exposes its readiness to the UI.</summary>
public sealed class GalleryData
{
    private readonly ILogger<GalleryData> _logger;

    public GalleryData(SampleContext context, ILogger<GalleryData> logger)
    {
        Context = context;
        _logger = logger;
        Ready = Task.Run(Prepare);
    }

    public SampleContext Context { get; }

    /// <summary>Completes when the sample data and the header contours are available.</summary>
    public Task Ready { get; }

    /// <summary>Current preparation step (for the loading screen).</summary>
    public string Status { get; private set; } = "Starting";

    public string? Error { get; private set; }

    /// <summary>Real contour lines of the sample DEM, normalized to 0..1 (the header signature).</summary>
    public IReadOnlyList<(double Elevation, double[] Points)> Contours { get; private set; } = [];

    public event Action? Changed;

    private void Prepare()
    {
        try
        {
            Context.EnsureData(new Progress(step =>
            {
                Status = step;
                Changed?.Invoke();
            }));
            Status = "Contours";
            Changed?.Invoke();
            Contours = Branding.SignatureContours(Context, 50, 2000);
            Status = "Ready";
        }
        catch (Exception e)
        {
            Error = e.Message;
            _logger.LogError(e, "Sample data generation failed");
            throw;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
