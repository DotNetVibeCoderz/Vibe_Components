using Medical.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Terminology;

/// <summary>Result of <see cref="TerminologyService.Validate"/>.</summary>
public sealed record CodeValidation(bool IsValid, bool Known, Concept? Concept, string Message);

/// <summary>An equivalence between two codes (FHIR ConceptMap element).</summary>
public sealed record ConceptMapping(Concept Source, Concept Target, string Equivalence = "equivalent");

/// <summary>
/// Multi-system terminology service: lookup, bilingual search, validation, subsumption, value-set expansion and
/// SNOMED CT → ICD-10 translation. <see cref="Default"/> holds the curated embedded subsets; register full releases with
/// <see cref="Register"/> (see <see cref="ReleaseLoaders"/>).
/// </summary>
public sealed class TerminologyService
{
    private readonly Dictionary<string, CodeSystem> _systems = new(StringComparer.Ordinal);
    private static readonly Lazy<TerminologyService> _default = new(EmbeddedTerminology.Create);

    /// <summary>Shared instance pre-loaded with the embedded ICD-10, LOINC, SNOMED CT and RxNorm subsets.</summary>
    public static TerminologyService Default => _default.Value;

    public IReadOnlyCollection<CodeSystem> Systems => _systems.Values;

    public void Register(CodeSystem system) => _systems[system.Uri] = system;

    public CodeSystem? System(string uri) => _systems.GetValueOrDefault(uri);

    public CodeSystem Icd10 => _systems[CodeSystems.Icd10];
    public CodeSystem Loinc => _systems[CodeSystems.Loinc];
    public CodeSystem Snomed => _systems[CodeSystems.Snomed];
    public CodeSystem RxNorm => _systems[CodeSystems.RxNorm];

    public Concept? Lookup(string system, string code) => System(system)?.Lookup(code);

    public Concept? Lookup(ClinicalCode code) => Lookup(code.System, code.Code);

    /// <summary>Searches one system, or all systems when <paramref name="system"/> is null.</summary>
    public IReadOnlyList<SearchHit> Search(string query, string? system = null, int limit = 20)
    {
        if (system is not null) return System(system)?.Search(query, limit) ?? [];
        return _systems.Values.SelectMany(s => s.Search(query, limit)).OrderByDescending(h => h.Score).Take(limit).ToArray();
    }

    /// <summary>Checks the code's format/check digit and whether the loaded release knows it.</summary>
    public CodeValidation Validate(string system, string code)
    {
        bool formatOk = system switch
        {
            CodeSystems.Icd10 or CodeSystems.Icd10Cm => CodeValidators.IsIcd10Format(code),
            CodeSystems.Loinc => CodeValidators.IsValidLoinc(code),
            CodeSystems.Snomed => CodeValidators.IsValidSctid(code),
            CodeSystems.RxNorm => code.Length > 0 && code.All(char.IsAsciiDigit),
            _ => true,
        };
        if (!formatOk) return new(false, false, null, $"'{code}' is not a well-formed {CodeSystems.ShortName(system)} code.");
        var c = Lookup(system, code);
        return c is null
            ? new(true, false, null, $"Well-formed, but not present in the loaded {CodeSystems.ShortName(system)} release.")
            : new(true, true, c, c.Display);
    }

    /// <summary>SNOMED CT subsumption test (is <paramref name="code"/> a kind of <paramref name="ancestor"/>?).</summary>
    public bool Subsumes(string ancestor, string code, string system = CodeSystems.Snomed) => System(system)?.Subsumes(ancestor, code) ?? false;

    /// <summary>Intensional value-set expansion: the concept and all its descendants ("&lt;&lt; code").</summary>
    public IReadOnlyList<Concept> Expand(string code, string system = CodeSystems.Snomed)
    {
        var cs = System(system);
        if (cs?.Lookup(code) is not { } root) return [];
        return [root, .. cs.Descendants(code)];
    }

    /// <summary>Translates SNOMED CT → ICD-10 using the embedded map, inheriting the nearest mapped ancestor when needed.</summary>
    public ConceptMapping? SnomedToIcd10(string sctid)
    {
        if (Snomed.Lookup(sctid) is not { } src) return null;
        if (src.MapTarget is { } t && Icd10.Lookup(t) is { } target) return new(src, target);
        foreach (var a in Snomed.Ancestors(sctid))
            if (a.MapTarget is { } at && Icd10.Lookup(at) is { } broader) return new(src, broader, "wider");
        return null;
    }

    /// <summary>Reverse map: SNOMED CT concepts whose default ICD-10 target is <paramref name="icd10"/>.</summary>
    public IReadOnlyList<ConceptMapping> Icd10ToSnomed(string icd10)
    {
        if (Icd10.Lookup(icd10) is not { } target) return [];
        return Snomed.Concepts.Where(c => string.Equals(c.MapTarget, target.Code, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ConceptMapping(target, c)).ToArray();
    }

    /// <summary>RxNorm ingredient by (case-insensitive) generic name, also matching Indonesian names (parasetamol, amoksisilin…).</summary>
    public Concept? Drug(string generic)
    {
        generic = generic.Trim();
        var direct = RxNorm.Concepts.FirstOrDefault(c => c.Display.Equals(generic, StringComparison.OrdinalIgnoreCase) ||
                                                         (c.DisplayId?.Equals(generic, StringComparison.OrdinalIgnoreCase) ?? false));
        if (direct is not null) return direct;
        return Aliases.TryGetValue(generic.ToLowerInvariant(), out var alias) ? RxNorm.Concepts.FirstOrDefault(c => c.Display == alias) : null;
    }

    /// <summary>International (INN) ↔ US (USAN) names that differ.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["paracetamol"] = "acetaminophen", ["salbutamol"] = "albuterol", ["rifampicin"] = "rifampin", ["glibenclamide"] = "glyburide",
        ["glyceryl trinitrate"] = "nitroglycerin", ["dabigatran"] = "dabigatran etexilate", ["valproate"] = "valproic acid",
    };
}

/// <summary>DI registration.</summary>
public static class TerminologyBuilderExtensions
{
    /// <summary>Registers <see cref="TerminologyService"/> (embedded subsets, or a custom instance).</summary>
    public static MedicalBuilder AddTerminology(this MedicalBuilder builder, TerminologyService? service = null) =>
        builder.TryAddModule("terminology", s => s.TryAddSingleton(service ?? TerminologyService.Default));
}
