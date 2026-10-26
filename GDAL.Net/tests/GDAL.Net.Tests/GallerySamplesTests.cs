using GDAL.Net.Gallery;

namespace GDAL.Net.Tests;

/// <summary>Runs every gallery use case end-to-end against the generated sample data.</summary>
public class GallerySamplesTests
{
    private static readonly SampleContext Context = new(Path.Combine(Path.GetTempPath(), "gdalnet-gallery-tests"));

    public static TheoryData<string> SampleIds()
    {
        var data = new TheoryData<string>();
        foreach (var s in Catalog.All.Where(s => !s.RequiresNetwork)) data.Add(s.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(SampleIds))]
    public void SampleRunsWithDefaults(string id)
    {
        var sample = Catalog.Find(id)!;
        using var result = sample.Execute(Context);
        Assert.True(result.HasMap || result.Table is not null || result.Output is not null, "Sample produced nothing to show.");
        Assert.False(string.IsNullOrWhiteSpace(sample.Code));
        Assert.False(string.IsNullOrWhiteSpace(sample.Title.Id));
        foreach (var r in result.Rasters)
        {
            var img = Rendering.RasterRenderer.Render(r.Dataset, r.Options, result.MapExtent, 160, 120);
            Assert.Equal(160 * 120 * 4, img.Pixels.Length);
        }
    }

    [Theory]
    [MemberData(nameof(SampleIds))]
    public void SampleRunsWithEveryChoice(string id)
    {
        var sample = Catalog.Find(id)!;
        foreach (var p in sample.Parameters.Where(p => p.Kind == ParameterKind.Choice))
        {
            foreach (var option in p.Options)
            {
                using var result = sample.Execute(Context, new Dictionary<string, string> { [p.Key] = option });
                Assert.NotNull(result);
            }
        }
    }

    [Fact]
    public void SignatureContoursAreNormalized()
    {
        var lines = Branding.SignatureContours(Context);
        Assert.NotEmpty(lines);
        Assert.All(lines.SelectMany(l => l.Points), v => Assert.InRange(v, -0.01, 1.01));
    }
}
