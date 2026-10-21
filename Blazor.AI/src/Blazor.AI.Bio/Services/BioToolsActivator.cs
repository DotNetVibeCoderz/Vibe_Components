using Blazor.AI.Agents;

namespace Blazor.AI.Bio;

/// <summary>Registers the Biotech.Net tools into the scoped tool registry the first time a bio component needs them.</summary>
public sealed class BioToolsActivator(ToolRegistry registry)
{
    private bool _done;

    public ToolRegistry Registry => registry;

    public void EnsureRegistered()
    {
        if (!_done && registry.Get("dna_statistics") is null)
        {
            BioTools.RegisterAll(registry);
        }

        _done = true;
    }
}
