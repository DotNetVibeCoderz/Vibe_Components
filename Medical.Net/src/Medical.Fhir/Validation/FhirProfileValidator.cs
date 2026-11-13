using Firely.Fhir.Packages;
using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;

namespace Medical.Fhir.Validation;

/// <summary>Where profiles come from.</summary>
public sealed record FhirValidationOptions
{
    /// <summary>Path to the R4 core <c>specification.zip</c>; located automatically when null (app folder, then NuGet cache).</summary>
    public string? SpecificationZip { get; init; }
    /// <summary>Implementation-guide packages, e.g. <c>hl7.fhir.uv.ips@1.1.0</c>; downloaded once into the FHIR package cache.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];
    public string PackageServer { get; init; } = "https://packages.simplifier.net";
    /// <summary>Package cache folder (default: the standard <c>~/.fhir/packages</c>).</summary>
    public string? PackageCache { get; init; }
    /// <summary>Folder with additional StructureDefinition / ValueSet / CodeSystem JSON files (local or national profiles).</summary>
    public string? ProfileDirectory { get; init; }
}

/// <summary>
/// Profile (StructureDefinition) validation with the Firely validator: cardinality, slicing, fixed values, invariants
/// (FHIRPath) and bound value sets, against the R4 core profiles, IG packages and local profiles.
/// Building the first validation for a profile compiles it (seconds); later validations are fast. Thread-safe.
/// </summary>
public sealed class FhirProfileValidator
{
    private readonly Lazy<Validator> _validator;

    public FhirProfileValidator(FhirValidationOptions? options = null)
    {
        Options = options ?? new FhirValidationOptions();
        _validator = new Lazy<Validator>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public FhirValidationOptions Options { get; }

    /// <summary>Validates against <paramref name="profile"/> (canonical URL) or, when null, the core definition plus any <c>meta.profile</c>.</summary>
    public OperationOutcome Validate(Resource resource, string? profile = null)
    {
        var outcome = _validator.Value.Validate(resource, profile);
        // Firely puts the readable message in details.text and the element trace in diagnostics; surface both.
        foreach (var i in outcome.Issue)
            if (i.Details?.Text is { Length: > 0 } text && (i.Diagnostics is null || !i.Diagnostics.Contains(text, StringComparison.Ordinal)))
                i.Diagnostics = i.Diagnostics is null ? text : $"{text} ({i.Diagnostics})";
        return outcome;
    }

    /// <summary>True when there is no error or fatal issue.</summary>
    public static bool IsSuccess(OperationOutcome oo) =>
        !oo.Issue.Any(i => i.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal);

    private Validator Build()
    {
        string zip = Options.SpecificationZip ?? FindSpecificationZip()
            ?? throw new FileNotFoundException("FHIR R4 specification.zip not found. Reference Hl7.Fhir.Specification.Data.R4 or set FhirValidationOptions.SpecificationZip.");
#pragma warning disable CS0618 // MultiResolver's constructor still takes the legacy marker interface
        var sources = new List<ISyncOrAsyncResourceResolver> { ZipSource.CreateValidationSource(zip) };
        if (Options.ProfileDirectory is { } dir)
            sources.Insert(0, new SnapshotSource(new DirectorySource(dir, new DirectorySourceSettings { IncludeSubDirectories = true })));
        if (Options.Packages.Count > 0)
            sources.Insert(0, Options.PackageCache is { } cache
                ? new FhirPackageSource(ModelInfo.ModelInspector, Options.PackageServer, [.. Options.Packages], cache)
                : new FhirPackageSource(ModelInfo.ModelInspector, Options.PackageServer, [.. Options.Packages]));
        var resolver = new CachedResolver(new MultiResolver(sources));
#pragma warning restore CS0618
        return new Validator(resolver, LocalTerminologyService.CreateDefaultForCore(resolver));
    }

    /// <summary>Finds the R4 core <c>specification.zip</c>: next to the app, then in the NuGet package cache.</summary>
    public static string? FindSpecificationZip()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "specification.zip");
        if (File.Exists(local)) return local;
        string nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string pkg = Path.Combine(nuget, "hl7.fhir.specification.data.r4");
        if (!Directory.Exists(pkg)) return null;
        return Directory.GetDirectories(pkg).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "contentFiles", "any", "any", "specification.zip")).FirstOrDefault(File.Exists);
    }
}
