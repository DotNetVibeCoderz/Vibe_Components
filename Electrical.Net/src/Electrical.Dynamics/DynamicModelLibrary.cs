namespace Electrical.Dynamics;

/// <summary>Description of a library model.</summary>
/// <param name="Name">PSS/E model name.</param>
/// <param name="Category">Role in the unit.</param>
/// <param name="Parameters">Parameter names in .dyr order (ICONs first, then CONs).</param>
/// <param name="MinimumValues">Minimum number of numeric values in a .dyr record.</param>
/// <param name="Description">Short description.</param>
public sealed record DynamicModelInfo(string Name, ModelCategory Category, IReadOnlyList<string> Parameters, int MinimumValues, string Description);

/// <summary>
/// Registry of dynamic models by PSS/E name. Factories receive the numeric record values (ICONs then CONs; quoted string ICONs such as
/// circuit ids are passed as NaN). Custom models can be registered with <see cref="Register"/>.
/// </summary>
public static class DynamicModelLibrary
{
    private delegate DynamicComponent Factory(double[] values);
    private static readonly Dictionary<string, (DynamicModelInfo Info, Factory Create)> Models = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    static DynamicModelLibrary()
    {
        void R(string name, ModelCategory cat, string[] pars, int min, string desc, Factory f) => Models[name] = (new DynamicModelInfo(name, cat, pars, min, desc), f);
        R("GENCLS", ModelCategory.Machine, Gencls.ParameterNames, 1, "Classical machine (E' behind X'd)", v => Gencls.FromDyr(v));
        R("GENROU", ModelCategory.Machine, Genrou.ParameterNames, 12, "Round-rotor machine, quadratic saturation", v => Genrou.FromDyr(v));
        R("GENSAL", ModelCategory.Machine, Gensal.ParameterNames, 10, "Salient-pole machine", v => Gensal.FromDyr(v));
        R("SEXS", ModelCategory.Exciter, Sexs.ParameterNames, 6, "Simplified excitation system", v => Sexs.FromDyr(v));
        R("IEEET1", ModelCategory.Exciter, Ieeet1.ParameterNames, 9, "IEEE type 1 DC exciter", v => Ieeet1.FromDyr(v));
        R("ESST1A", ModelCategory.Exciter, Esst1a.ParameterNames, 20, "IEEE 421.5 ST1A static exciter", v => Esst1a.FromDyr(v));
        R("ESAC4A", ModelCategory.Exciter, Esac4a.ParameterNames, 10, "IEEE 421.5 AC4A exciter", v => Esac4a.FromDyr(v));
        R("TGOV1", ModelCategory.Governor, Tgov1.ParameterNames, 7, "Steam turbine-governor", v => Tgov1.FromDyr(v));
        R("HYGOV", ModelCategory.Governor, Hygov.ParameterNames, 12, "Hydro turbine-governor", v => Hygov.FromDyr(v));
        R("GAST", ModelCategory.Governor, Gast.ParameterNames, 9, "Gas turbine-governor", v => Gast.FromDyr(v));
        R("IEEEG1", ModelCategory.Governor, Ieeeg1.ParameterNames, 22, "IEEE type 1 steam turbine (single shaft)", v => Ieeeg1.FromDyr(v));
        R("IEEEST", ModelCategory.Stabilizer, Ieeest.ParameterNames, 17, "IEEE standard stabilizer", v => Ieeest.FromDyr(v));
        R("PSS2B", ModelCategory.Stabilizer, Pss2b.ParameterNames, 29, "IEEE 421.5 PSS2B dual-input stabilizer", v => Pss2b.FromDyr(v));
        R("PSS2A", ModelCategory.Stabilizer, Pss2b.ParameterNames[..21].Concat(["VSTMAX", "VSTMIN"]).ToArray(), 23, "IEEE 421.5 PSS2A dual-input stabilizer", v => Pss2b.FromDyr(v));
        R("REGCA1", ModelCategory.Converter, Regca.ParameterNames, 15, "WECC REGC_A generator/converter", v => Regca.FromDyr(v));
        R("REECA1", ModelCategory.ElectricalControl, Reeca.ParameterNames, 51, "WECC REEC_A electrical control", v => Reeca.FromDyr(v));
        R("REPCA1", ModelCategory.PlantControl, Repca.ParameterNames, 33, "WECC REPC_A plant controller", v => Repca.FromDyr(v));
        R("GFMDRP", ModelCategory.GridForming, GridFormingInverter.ParameterNames, 5, "Grid-forming inverter, droop (library-specific)", v => GridFormingInverter.FromDyr(GridFormingMode.Droop, v));
        R("GFMVSM", ModelCategory.GridForming, GridFormingInverter.ParameterNames, 5, "Grid-forming inverter, virtual synchronous machine (library-specific)", v => GridFormingInverter.FromDyr(GridFormingMode.VirtualSynchronousMachine, v));
        Alias("REGCAU1", "REGCA1"); Alias("REGC_A", "REGCA1");
        Alias("REECAU1", "REECA1"); Alias("REEC_A", "REECA1");
        Alias("REPCAU1", "REPCA1"); Alias("REPC_A", "REPCA1");
        Alias("ST1A", "ESST1A"); Alias("AC4A", "ESAC4A");
    }

    private static void Alias(string alias, string name) => Models[alias] = Models[name];

    /// <summary>Registered model names (including aliases).</summary>
    public static IReadOnlyCollection<string> Names { get { lock (Gate) return Models.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(); } }

    /// <summary>Distinct model descriptions.</summary>
    public static IReadOnlyList<DynamicModelInfo> All { get { lock (Gate) return Models.Values.Select(m => m.Info).Distinct().ToList(); } }

    public static bool TryGetInfo(string name, out DynamicModelInfo? info)
    {
        lock (Gate)
        {
            bool ok = Models.TryGetValue(name, out var m);
            info = ok ? m.Info : null;
            return ok;
        }
    }

    /// <summary>Registers (or replaces) a model factory.</summary>
    public static void Register(DynamicModelInfo info, Func<double[], DynamicComponent> factory)
    {
        lock (Gate) Models[info.Name] = (info, v => factory(v));
    }

    /// <summary>Creates a model from .dyr values. Returns false with an error message for unknown names or bad data.</summary>
    public static bool TryCreate(string name, double[] values, out DynamicComponent? component, out string? error)
    {
        (DynamicModelInfo Info, Factory Create) m;
        lock (Gate)
            if (!Models.TryGetValue(name, out m)) { component = null; error = $"unknown model '{name}'"; return false; }
        if (values.Length < m.Info.MinimumValues)
        {
            component = null; error = $"{name}: expected at least {m.Info.MinimumValues} values, got {values.Length}"; return false;
        }
        try { component = m.Create(values); error = null; return true; }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            component = null; error = $"{name}: {e.Message}"; return false;
        }
    }
}
