namespace Electrical.Dynamics;

/// <summary>Dynamic models attached to one generator of the network.</summary>
public sealed class GeneratorDynamics
{
    public MachineModel? Machine { get; set; }
    public ExciterModel? Exciter { get; set; }
    public GovernorModel? Governor { get; set; }
    public StabilizerModel? Stabilizer { get; set; }
    public ConverterModel? Converter { get; set; }
    public ElectricalControlModel? ElectricalControl { get; set; }
    public PlantControlModel? PlantControl { get; set; }
    public GridFormingInverter? GridForming { get; set; }

    /// <summary>All attached components.</summary>
    public IEnumerable<DynamicComponent> Components =>
        new DynamicComponent?[] { Machine, Exciter, Governor, Stabilizer, Converter, ElectricalControl, PlantControl, GridForming }.OfType<DynamicComponent>();
}

/// <summary>
/// Dynamic data of a study: models per generator index (<c>PowerNetwork.Generators</c>) and dynamic load units. Generators without a machine,
/// converter or grid-forming model are represented as GENCLS built from <c>Generator.H</c>, <c>D</c> and <c>XdPrime</c>.
/// </summary>
public sealed class DynamicModelSet
{
    private readonly SortedDictionary<int, GeneratorDynamics> _gens = [];

    public IReadOnlyDictionary<int, GeneratorDynamics> Generators => _gens;
    /// <summary>Dynamic load units.</summary>
    public List<ExponentialRecoveryLoad> Loads { get; } = [];
    /// <summary>Reader / builder warnings (unknown models, unmatched generators, …).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Models of generator <paramref name="generatorIndex"/> (created on demand).</summary>
    public GeneratorDynamics For(int generatorIndex)
    {
        if (!_gens.TryGetValue(generatorIndex, out var g)) _gens[generatorIndex] = g = new GeneratorDynamics();
        return g;
    }

    /// <summary>Attaches <paramref name="components"/> to a generator, each in the slot of its <see cref="ModelCategory"/>.</summary>
    public DynamicModelSet Add(int generatorIndex, params DynamicComponent[] components)
    {
        var g = For(generatorIndex);
        foreach (var c in components)
        {
            switch (c)
            {
                case MachineModel m: g.Machine = m; break;
                case ExciterModel e: g.Exciter = e; break;
                case GovernorModel gv: g.Governor = gv; break;
                case StabilizerModel s: g.Stabilizer = s; break;
                case ConverterModel cv: g.Converter = cv; break;
                case ElectricalControlModel ec: g.ElectricalControl = ec; break;
                case PlantControlModel pc: g.PlantControl = pc; break;
                case GridFormingInverter gf: g.GridForming = gf; break;
                default: throw new ArgumentException($"Model {c.ModelName} cannot be attached to a generator.");
            }
        }
        return this;
    }

    /// <summary>Removes exciters, stabilizers or governors from all generators (e.g. to study manual excitation).</summary>
    public DynamicModelSet Without(params ModelCategory[] categories)
    {
        foreach (var g in _gens.Values)
        {
            if (categories.Contains(ModelCategory.Exciter)) g.Exciter = null;
            if (categories.Contains(ModelCategory.Stabilizer)) g.Stabilizer = null;
            if (categories.Contains(ModelCategory.Governor)) g.Governor = null;
            if (categories.Contains(ModelCategory.PlantControl)) g.PlantControl = null;
        }
        return this;
    }

    public override string ToString() => $"{_gens.Count} generators, {_gens.Values.Sum(g => g.Components.Count())} models, {Loads.Count} dynamic loads";
}
