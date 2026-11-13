using System.Text;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;
using AuditEvent = Medical.Core.AuditEvent;
using Medical.Core;
using Medical.Fhir;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.AspNetCore;

/// <summary>Minimal FHIR R4 RESTful API over <see cref="InMemoryFhirStore"/> (JSON only).</summary>
public static class FhirEndpoints
{
    public const string FhirJsonType = "application/fhir+json";

    private static readonly string[] SupportedTypes =
        ["Patient", "Encounter", "Observation", "Condition", "MedicationRequest", "AllergyIntolerance", "DiagnosticReport", "ImagingStudy", "Composition", "Bundle"];

    /// <summary>Maps <c>{prefix}/metadata</c>, CRUD, search, <c>$everything</c> and transaction/batch endpoints.</summary>
    public static RouteGroupBuilder MapMedicalFhir(this IEndpointRouteBuilder app, string prefix = "/fhir")
    {
        var g = app.MapGroup(prefix).WithTags("FHIR R4");

        g.MapGet("/metadata", (HttpContext ctx) => Fhir(Capability(BaseUrl(ctx, prefix))));

        g.MapGet("/{type}/{id}", (string type, string id, InMemoryFhirStore store, IAuditLog audit, HttpContext ctx) =>
        {
            var r = store.Read(type, id);
            audit.Record(new AuditEvent(DateTimeOffset.UtcNow, User(ctx), AuditAction.Read, $"{type}/{id}", type == "Patient" ? id : null, Success: r is not null));
            return r is null ? Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found") : Fhir(r);
        });

        g.MapGet("/{type}", (string type, InMemoryFhirStore store, HttpContext ctx) =>
        {
            if (!SupportedTypes.Contains(type)) return Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotSupported, $"Resource type '{type}' not supported");
            var ps = ctx.Request.Query.SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? "")));
            return Fhir(store.Search(type, ps, BaseUrl(ctx, prefix)));
        });

        g.MapGet("/Patient/{id}/$everything", (string id, InMemoryFhirStore store, IAuditLog audit, HttpContext ctx) =>
        {
            audit.Record(new AuditEvent(DateTimeOffset.UtcNow, User(ctx), AuditAction.Export, "Patient/$everything", id));
            return store.Read("Patient", id) is null ? Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"Patient/{id} not found") : Fhir(store.Everything(id));
        });

        g.MapPost("/{type}", async (string type, HttpRequest req, InMemoryFhirStore store) =>
        {
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            if (res!.TypeName != type) return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Invalid, $"Body is a {res.TypeName}, URL says {type}");
            var issues = FhirJson.Validate(res);
            if (issues.Issue.Count > 0) return Fhir(issues, StatusCodes.Status422UnprocessableEntity);
            var created = store.Create(res);
            return Fhir(created, StatusCodes.Status201Created, $"{type}/{created.Id}/_history/{created.Meta?.VersionId}");
        });

        g.MapPut("/{type}/{id}", async (string type, string id, HttpRequest req, InMemoryFhirStore store) =>
        {
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            res!.Id = id;
            bool existed = store.Read(type, id) is not null;
            var updated = store.Update(res);
            return Fhir(updated, existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        g.MapDelete("/{type}/{id}", (string type, string id, InMemoryFhirStore store) =>
            store.Delete(type, id) ? Results.NoContent() : Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found"));

        g.MapPost("/", async (HttpRequest req, InMemoryFhirStore store) =>
        {
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            if (res is not Bundle b || b.Type is not (Bundle.BundleType.Transaction or Bundle.BundleType.Batch))
                return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Invalid, "Expected a transaction or batch Bundle");
            try { return Fhir(store.Process(b)); }
            catch (InvalidOperationException e) { return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Processing, e.Message); }
        });

        return g;
    }

    public static CapabilityStatement Capability(string baseUrl) => new()
    {
        Status = PublicationStatus.Active,
        Date = "2026-11-13",
        Kind = CapabilityStatementKind.Instance,
        Software = new CapabilityStatement.SoftwareComponent { Name = MedicalInfo.Name, Version = MedicalInfo.Version },
        Implementation = new CapabilityStatement.ImplementationComponent { Description = $"{MedicalInfo.Name} FHIR facade — {MedicalInfo.Credit}", Url = baseUrl },
        FhirVersion = FHIRVersion.N4_0_1,
        Format = ["json"],
        Rest =
        [
            new CapabilityStatement.RestComponent
            {
                Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                Interaction = [new() { Code = CapabilityStatement.SystemRestfulInteraction.Transaction }, new() { Code = CapabilityStatement.SystemRestfulInteraction.Batch }],
                Resource = SupportedTypes.Where(t => t != "Bundle").Select(t => new CapabilityStatement.ResourceComponent
                {
                    Type = t,
                    Versioning = CapabilityStatement.ResourceVersionPolicy.Versioned,
                    Interaction = new[] { CapabilityStatement.TypeRestfulInteraction.Read, CapabilityStatement.TypeRestfulInteraction.SearchType,
                        CapabilityStatement.TypeRestfulInteraction.Create, CapabilityStatement.TypeRestfulInteraction.Update, CapabilityStatement.TypeRestfulInteraction.Delete }
                        .Select(c => new CapabilityStatement.ResourceInteractionComponent { Code = c }).ToList(),
                    SearchParam = new[] { "_id", "patient", "subject", "identifier", "code", "name", "category", "status" }
                        .Select(n => new CapabilityStatement.SearchParamComponent { Name = n, Type = n is "patient" or "subject" ? SearchParamType.Reference : n is "name" ? SearchParamType.String : SearchParamType.Token })
                        .ToList(),
                }).ToList(),
            },
        ],
    };

    private static async Task<(Resource?, IResult?)> ReadResource(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        string json = await reader.ReadToEndAsync();
        if (!FhirJson.TryParse(json, out var res, out var issues) || res is null)
            return (null, Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Structure, string.Join("; ", issues.DefaultIfEmpty("Invalid FHIR JSON"))));
        return (res, null);
    }

    internal static IResult Fhir(Base resource, int status = StatusCodes.Status200OK, string? location = null) =>
        new FhirResult(FhirJson.Serialize(resource, false), status, location);

    internal static IResult Outcome(int status, OperationOutcome.IssueType type, string message) =>
        Fhir(new OperationOutcome { Issue = [new() { Severity = OperationOutcome.IssueSeverity.Error, Code = type, Diagnostics = message }] }, status);

    private static string BaseUrl(HttpContext ctx, string prefix) => $"{ctx.Request.Scheme}://{ctx.Request.Host}{prefix}";

    internal static string User(HttpContext ctx) => ctx.User.Identity?.Name ?? ctx.Request.Headers["X-User"].FirstOrDefault() ?? "anonymous";

    private sealed class FhirResult(string body, int status, string? location) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = FhirJsonType + "; charset=utf-8";
            if (location is not null) ctx.Response.Headers.Location = location;
            return ctx.Response.WriteAsync(body);
        }
    }
}
