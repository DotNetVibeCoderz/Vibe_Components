using GnuRadio.Gallery;

namespace GnuRadio.Tests;

// Demos run real-time flowgraphs; keep them away from the timing-sensitive tests.
[Collection(nameof(GalleryDemoTests))]
[CollectionDefinition(nameof(GalleryDemoTests), DisableParallelization = true)]
public class GalleryDemoTests
{
    public static TheoryData<string> DemoIds => [.. DemoCatalog.All.Select(d => d.Id)];

    [Theory]
    [MemberData(nameof(DemoIds))]
    public void DemoStartsRunsAndStops(string id)
    {
        using var demo = DemoCatalog.Find(id)!.Create();
        demo.Start();
        Thread.Sleep(1500);
        Assert.Null(demo.Error);
        Assert.NotEmpty(demo.Views);
        foreach (var r in demo.Readouts) Assert.NotNull(r.Value());
        Assert.False(string.IsNullOrWhiteSpace(demo.Code));
        Assert.False(string.IsNullOrWhiteSpace(demo.Title.Id));
        demo.Stop();
        Assert.False(demo.IsRunning);
    }

    [Fact]
    public void CatalogIdsAreUnique() =>
        Assert.Equal(DemoCatalog.All.Count, DemoCatalog.All.Select(d => d.Id).Distinct().Count());
}
