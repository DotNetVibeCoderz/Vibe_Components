using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Validation;

namespace Medical.Fhir;

/// <summary>FHIR R4 JSON serialization, structural validation and FHIRPath helpers (Firely SDK).</summary>
public static class FhirJson
{
    private static readonly FhirJsonSerializer Serializer = new();

    /// <summary>Serializes a resource to FHIR JSON.</summary>
    public static string Serialize(Base resource, bool pretty = true) => Serializer.SerializeToString(resource, pretty);

    /// <summary>Parses FHIR JSON into a resource (strict: unknown elements and invalid values throw).</summary>
    public static Resource Parse(string json) => new FhirJsonDeserializer(new DeserializerSettings()).DeserializeResource(json);

    public static T Parse<T>(string json) where T : Resource =>
        Parse(json) as T ?? throw new InvalidCastException($"JSON does not contain a {typeof(T).Name} resource.");

    /// <summary>Parses tolerantly, returning the issues instead of throwing.</summary>
    public static bool TryParse(string json, out Resource? resource, out IReadOnlyList<string> issues)
    {
        bool ok = new FhirJsonDeserializer(new DeserializerSettings()).TryDeserializeResource(json, out var r, out var raw);
        resource = r;
        issues = raw?.Select(i => i.Message).ToArray() ?? [];
        return ok;
    }

    /// <summary>
    /// Validates cardinality, required elements, primitive formats and invariants declared on the POCO model,
    /// returning a FHIR <see cref="OperationOutcome"/> (empty issue list = valid).
    /// </summary>
    public static OperationOutcome Validate(Base resource)
    {
        var outcome = new OperationOutcome();
        foreach (var r in resource.Validate(ModelInfo.ModelInspector, NarrativeValidationKind.FhirXhtml, null, true))
        {
            outcome.Issue.Add(r.ToIssue());
        }
        return outcome;
    }

    public static bool IsValid(Base resource) => Validate(resource).Issue.Count == 0;

    /// <summary>Evaluates a FHIRPath expression and returns the results as strings.</summary>
    public static IReadOnlyList<string> Select(Base resource, string fhirPath) =>
        resource.Select(fhirPath).Select(b => b switch
        {
            PrimitiveType p => p.ToString() ?? "",
            _ => b is null ? "" : Serialize(b, false),
        }).ToArray();

    /// <summary>Evaluates a FHIRPath boolean expression (e.g. an invariant).</summary>
    public static bool IsTrue(Base resource, string fhirPath) => resource.IsTrue(fhirPath);
}
