using Blazor.AI.Bio;
using Blazor.AI.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Blazor.AI.Tests;

public class ComponentTests : TestContext
{
    public ComponentTests()
    {
        Services.AddBlazorAI().AddSimulated();
        Services.AddBlazorAIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void AiCard_renders_title_and_content()
    {
        var cut = RenderComponent<AiCard>(p => p
            .Add(c => c.Title, "Hello")
            .Add(c => c.Icon, "bi-stars")
            .AddChildContent("<p class=\"body\">content</p>"));

        Assert.Contains("Hello", cut.Markup);
        cut.Find("p.body").MarkupMatches("<p class=\"body\">content</p>");
    }

    [Fact]
    public void Dna_analyzer_shows_length_and_orfs()
    {
        var cut = RenderComponent<DnaSequenceAnalyzer>(p => p.Add(c => c.Sequence, BioSamples.Egfp));
        cut.WaitForAssertion(() => Assert.Contains("720", cut.Find(".bai-stats").TextContent), TimeSpan.FromSeconds(10));
        Assert.NotEmpty(cut.FindAll("table.bai-table tbody tr"));
    }

    [Fact]
    public void Molecule_explorer_reports_invalid_smiles()
    {
        var cut = RenderComponent<MoleculeExplorer>(p => p.Add(c => c.Smiles, "C1CC(("));
        cut.WaitForAssertion(() => Assert.DoesNotContain("bai-bio-mol", cut.Markup), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Contains("role=\"alert\"", cut.Markup), TimeSpan.FromSeconds(10));
    }
}
