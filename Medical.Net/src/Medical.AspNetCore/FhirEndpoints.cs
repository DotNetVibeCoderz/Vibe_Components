using System.Text;
using Hl7.Fhir.Model;
using Medical.AspNetCore.Smart;
using Medical.Core;
using Medical.Fhir;
using Medical.Fhir.Search;
using Medical.Fhir.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using AuditEvent = Medical.Core.AuditEvent;
using Task = System.Threading.Tasks.Task;

namespace Medical.AspNetCore;

/// <summary>
/// FHIR R4 RESTful API over <see cref="IFhirRepository"/> (JSON): CRUD with versioning and <c>If-Match</c>, vread,
/// instance/type/system <c>_history</c>, search with <c>_include</c>/<c>_revinclude</c>/<c>_sort</c>/paging,
/// <c>Patient/$everything</c>, <c>$validate</c> against profiles, transaction/batch, CapabilityStatement — and SMART on FHIR
/// scope enforcement when <see cref="SmartServerExtensions.AddSmartOnFhir"/> is registered.
/// </summary>
public static class FhirEndpoints
{
    public const string FhirJsonType = "application/fhir+json";

    private static readonly string[] SupportedTypes =
        ["Patient", "Encounter", "Observation", "Condition", "MedicationRequest", "AllergyIntolerance", "DiagnosticReport", "ImagingStudy",
         "Composition", "ServiceRequest", "Appointment", "DocumentReference", "Medication", "Bundle"];

    public static RouteGroupBuilder MapMedicalFhir(this IEndpointRouteBuilder app, string prefix = "/fhir")
    {
        var g = app.MapGroup(prefix).WithTags("FHIR R4");

        g.MapGet("/metadata", (HttpContext ctx) => Fhir(Capability(BaseUrl(ctx, prefix), ctx.RequestServices.GetService<SmartServerOptions>() is not null)));

        g.MapGet("/_history", (HttpContext ctx, IFhirRepository repo) =>
            Guard(ctx, "*", 'r', out _) ?? Fhir(Filter(ctx, repo.History(null, null, Since(ctx), Count(ctx)))));

        g.MapGet("/{type}/_history", (string type, HttpContext ctx, IFhirRepository repo) =>
            Guard(ctx, type, 'r', out _) ?? Fhir(Filter(ctx, repo.History(type, null, Since(ctx), Count(ctx)))));

        g.MapGet("/{type}/{id}/_history", (string type, string id, HttpContext ctx, IFhirRepository repo) =>
            Guard(ctx, type, 'r', out _) ?? Fhir(Filter(ctx, repo.History(type, id, Since(ctx), Count(ctx)))));

        g.MapGet("/{type}/{id}/_history/{vid}", (string type, string id, string vid, HttpContext ctx, IFhirRepository repo) =>
        {
            if (Guard(ctx, type, 'r', out _) is { } denied) return denied;
            var r = repo.VRead(type, id, vid);
            if (r is null) return Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"{type}/{id}/_history/{vid} not found");
            return Visible(ctx, r) ? Fhir(r, etag: vid) : Forbidden("Resource is outside the patient compartment of this token");
        });

        g.MapGet("/{type}/{id}", (string type, string id, IFhirRepository repo, IAuditLog audit, HttpContext ctx) =>
        {
            if (Guard(ctx, type, 'r', out _) is { } denied) return denied;
            var r = repo.Read(type, id);
            audit.Record(new AuditEvent(DateTimeOffset.UtcNow, User(ctx), AuditAction.Read, $"{type}/{id}", type == "Patient" ? id : null, Success: r is not null));
            if (r is null)
                return repo.IsDeleted(type, id)
                    ? Outcome(StatusCodes.Status410Gone, OperationOutcome.IssueType.Deleted, $"{type}/{id} has been deleted")
                    : Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found");
            return Visible(ctx, r) ? Fhir(r, etag: r.Meta?.VersionId) : Forbidden("Resource is outside the patient compartment of this token");
        });

        g.MapGet("/{type}", (string type, IFhirRepository repo, HttpContext ctx) =>
        {
            if (!SupportedTypes.Contains(type)) return Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotSupported, $"Resource type '{type}' not supported");
            if (Guard(ctx, type, 's', out _) is { } denied) return denied;
            var ps = ctx.Request.Query.SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? ""))).ToList();
            if (Principal(ctx) is { PatientBound: true } p)
                ps.Add(type == "Patient" ? new("_id", p.Patient!) : new("patient", $"Patient/{p.Patient}"));
            return Fhir(Filter(ctx, repo.Search(type, ps, BaseUrl(ctx, prefix))));
        });

        g.MapGet("/Patient/{id}/$everything", (string id, IFhirRepository repo, IAuditLog audit, HttpContext ctx) =>
        {
            if (Guard(ctx, "Patient", 'r', out _) is { } denied) return denied;
            if (Principal(ctx) is { PatientBound: true } p && p.Patient != id) return Forbidden("Token is bound to another patient");
            audit.Record(new AuditEvent(DateTimeOffset.UtcNow, User(ctx), AuditAction.Export, "Patient/$everything", id));
            return repo.Read("Patient", id) is null
                ? Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"Patient/{id} not found")
                : Fhir(repo.Everything(id));
        });

        g.MapPost("/{type}/$validate", async (string type, HttpRequest req, FhirProfileValidator validator) =>
        {
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            if (res is Parameters prm && prm.GetSingle("resource")?.Resource is { } inner) res = inner;
            string? profile = req.Query["profile"].FirstOrDefault();
            var oo = FhirJson.Validate(res!);
            if (oo.Issue.Count == 0) oo = validator.Validate(res!, profile);
            if (oo.Issue.Count == 0) oo.Issue.Add(new OperationOutcome.IssueComponent { Severity = OperationOutcome.IssueSeverity.Information, Code = OperationOutcome.IssueType.Informational, Diagnostics = "Validation successful" });
            return Fhir(oo);
        });

        g.MapPost("/{type}", async (string type, HttpRequest req, IFhirRepository repo, FhirProfileValidator validator) =>
        {
            if (Guard(req.HttpContext, type, 'c', out _) is { } denied) return denied;
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            if (res!.TypeName != type) return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Invalid, $"Body is a {res.TypeName}, URL says {type}");
            if (!Visible(req.HttpContext, res)) return Forbidden("Resource must belong to the token's patient");
            var issues = FhirJson.Validate(res);
            if (issues.Issue.Count == 0 && res.Meta?.Profile.Any() == true) issues = validator.Validate(res);
            if (!FhirProfileValidator.IsSuccess(issues)) return Fhir(issues, StatusCodes.Status422UnprocessableEntity);
            var created = repo.Create(res);
            return Fhir(created, StatusCodes.Status201Created, $"{type}/{created.Id}/_history/{created.Meta?.VersionId}", created.Meta?.VersionId);
        });

        g.MapPut("/{type}/{id}", async (string type, string id, HttpRequest req, IFhirRepository repo) =>
        {
            if (Guard(req.HttpContext, type, 'u', out _) is { } denied) return denied;
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            res!.Id = id;
            if (!Visible(req.HttpContext, res)) return Forbidden("Resource must belong to the token's patient");
            bool existed = repo.Read(type, id) is not null;
            try
            {
                var updated = repo.Update(res, req.Headers.IfMatch.FirstOrDefault());
                return Fhir(updated, existed ? StatusCodes.Status200OK : StatusCodes.Status201Created, $"{type}/{id}/_history/{updated.Meta?.VersionId}", updated.Meta?.VersionId);
            }
            catch (FhirVersionConflictException e) { return Outcome(StatusCodes.Status412PreconditionFailed, OperationOutcome.IssueType.Conflict, e.Message); }
        });

        g.MapDelete("/{type}/{id}", (string type, string id, IFhirRepository repo, HttpContext ctx) =>
        {
            if (Guard(ctx, type, 'd', out _) is { } denied) return denied;
            if (repo.Read(type, id) is { } existing && !Visible(ctx, existing)) return Forbidden("Resource is outside the patient compartment of this token");
            return repo.Delete(type, id) ? Results.NoContent() : Outcome(StatusCodes.Status404NotFound, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found");
        });

        g.MapPost("/", async (HttpRequest req, IFhirRepository repo) =>
        {
            var (res, err) = await ReadResource(req);
            if (err is not null) return err;
            if (res is not Bundle b || b.Type is not (Bundle.BundleType.Transaction or Bundle.BundleType.Batch))
                return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Invalid, "Expected a transaction or batch Bundle");
            foreach (var e in b.Entry)
            {
                string type = e.Resource?.TypeName ?? (e.Request?.Url ?? "").Split('/', '?')[0];
                char perm = (e.Request?.Method ?? Bundle.HTTPVerb.POST) switch
                {
                    Bundle.HTTPVerb.POST => 'c', Bundle.HTTPVerb.PUT => 'u', Bundle.HTTPVerb.DELETE => 'd', _ => 'r',
                };
                if (Guard(req.HttpContext, type, perm, out _) is { } denied) return denied;
                if (e.Resource is { } r && !Visible(req.HttpContext, r)) return Forbidden("Bundle entry is outside the patient compartment of this token");
            }
            try { return Fhir(repo.Process(b)); }
            catch (InvalidOperationException e) { return Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Processing, e.Message); }
            catch (FhirVersionConflictException e) { return Outcome(StatusCodes.Status412PreconditionFailed, OperationOutcome.IssueType.Conflict, e.Message); }
        });

        return g;
    }

    // ------------------------------------------------------------- SMART enforcement

    private static SmartPrincipal? Principal(HttpContext ctx) => ctx.Items[SmartPrincipal.ItemKey] as SmartPrincipal;

    /// <summary>
    /// Null when the call may proceed. With SMART registered and <see cref="SmartServerOptions.RequireAuthorization"/> on,
    /// validates the bearer token (401) and the scope for the interaction (403); the principal is cached per request.
    /// </summary>
    private static IResult? Guard(HttpContext ctx, string type, char permission, out IResult? denied)
    {
        denied = null;
        var options = ctx.RequestServices.GetService<SmartServerOptions>();
        if (options is not { RequireAuthorization: true }) return null;
        if (Principal(ctx) is not { } p)
        {
            p = SmartServerExtensions.AuthenticateAsync(ctx, options).GetAwaiter().GetResult()!;
            if (p is null)
            {
                ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"fhir\"";
                return denied = Outcome(StatusCodes.Status401Unauthorized, OperationOutcome.IssueType.Login, "A valid SMART bearer token is required");
            }
            ctx.Items[SmartPrincipal.ItemKey] = p;
        }
        bool ok = type == "*" ? p.Scopes.Any(s => s.ResourceType == "*" && s.Permissions.Contains(permission)) : p.Allows(type, permission);
        return ok ? null : denied = Forbidden($"Scope does not allow '{permission}' on {type}");
    }

    private static bool Visible(HttpContext ctx, Resource r) => Principal(ctx) is not { } p || p.CanSee(r);

    /// <summary>Removes entries a patient-bound token may not see (includes, history).</summary>
    private static Bundle Filter(HttpContext ctx, Bundle b)
    {
        if (Principal(ctx) is not { PatientBound: true } p) return b;
        b.Entry.RemoveAll(e => e.Resource is { } r and not OperationOutcome && !p.CanSee(r));
        return b;
    }

    private static IResult Forbidden(string message) => Outcome(StatusCodes.Status403Forbidden, OperationOutcome.IssueType.Forbidden, message);

    // ------------------------------------------------------------- helpers

    public static CapabilityStatement Capability(string baseUrl, bool smart = false)
    {
        var rest = new CapabilityStatement.RestComponent
        {
            Mode = CapabilityStatement.RestfulCapabilityMode.Server,
            Interaction =
            [
                new() { Code = CapabilityStatement.SystemRestfulInteraction.Transaction },
                new() { Code = CapabilityStatement.SystemRestfulInteraction.Batch },
                new() { Code = CapabilityStatement.SystemRestfulInteraction.HistorySystem },
            ],
            Resource = SupportedTypes.Where(t => t != "Bundle").Select(t => new CapabilityStatement.ResourceComponent
            {
                Type = t,
                Versioning = CapabilityStatement.ResourceVersionPolicy.VersionedUpdate,
                ReadHistory = true,
                UpdateCreate = true,
                ConditionalCreate = true,
                SearchInclude = FhirSearchIndex.ParametersFor(t).Where(p => p.Value == FhirSearchKind.Reference).Select(p => $"{t}:{p.Key}").ToList(),
                SearchRevInclude = ["Observation:patient", "Condition:patient", "Encounter:patient", "MedicationRequest:patient"],
                Interaction = new[]
                {
                    CapabilityStatement.TypeRestfulInteraction.Read, CapabilityStatement.TypeRestfulInteraction.Vread, CapabilityStatement.TypeRestfulInteraction.SearchType,
                    CapabilityStatement.TypeRestfulInteraction.Create, CapabilityStatement.TypeRestfulInteraction.Update, CapabilityStatement.TypeRestfulInteraction.Delete,
                    CapabilityStatement.TypeRestfulInteraction.HistoryInstance, CapabilityStatement.TypeRestfulInteraction.HistoryType,
                }.Select(c => new CapabilityStatement.ResourceInteractionComponent { Code = c }).ToList(),
                SearchParam = FhirSearchIndex.ParametersFor(t).Select(p => new CapabilityStatement.SearchParamComponent
                {
                    Name = p.Key,
                    Type = p.Value switch
                    {
                        FhirSearchKind.Reference => SearchParamType.Reference, FhirSearchKind.String => SearchParamType.String,
                        FhirSearchKind.Date => SearchParamType.Date, _ => SearchParamType.Token,
                    },
                }).ToList(),
            }).ToList(),
        };
        if (smart)
            rest.Security = new CapabilityStatement.SecurityComponent
            {
                Service = [FhirMapper.Cc("http://terminology.hl7.org/CodeSystem/restful-security-service", "SMART-on-FHIR", "SMART-on-FHIR")],
                Description = "OAuth2 / SMART App Launch: see .well-known/smart-configuration",
            };
        return new CapabilityStatement
        {
            Status = PublicationStatus.Active,
            Date = "2026-11-14",
            Kind = CapabilityStatementKind.Instance,
            Software = new CapabilityStatement.SoftwareComponent { Name = MedicalInfo.Name, Version = MedicalInfo.Version },
            Implementation = new CapabilityStatement.ImplementationComponent { Description = $"{MedicalInfo.Name} FHIR server — {MedicalInfo.Credit}", Url = baseUrl },
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["json"],
            Rest = [rest],
        };
    }

    private static DateTimeOffset? Since(HttpContext ctx) =>
        ctx.Request.Query["_since"].FirstOrDefault() is { } s && FhirSearchIndex.Range(s) is { } r ? r.Start : null;

    private static int Count(HttpContext ctx) => int.TryParse(ctx.Request.Query["_count"].FirstOrDefault(), out var c) ? Math.Clamp(c, 1, 1000) : 100;

    private static async Task<(Resource?, IResult?)> ReadResource(HttpRequest req)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        string json = await reader.ReadToEndAsync();
        if (!FhirJson.TryParse(json, out var res, out var issues) || res is null)
            return (null, Outcome(StatusCodes.Status400BadRequest, OperationOutcome.IssueType.Structure, string.Join("; ", issues.DefaultIfEmpty("Invalid FHIR JSON"))));
        return (res, null);
    }

    internal static IResult Fhir(Base resource, int status = StatusCodes.Status200OK, string? location = null, string? etag = null) =>
        new FhirResult(FhirJson.Serialize(resource, false), status, location, etag);

    internal static IResult Outcome(int status, OperationOutcome.IssueType type, string message) =>
        Fhir(new OperationOutcome { Issue = [new() { Severity = OperationOutcome.IssueSeverity.Error, Code = type, Diagnostics = message }] }, status);

    private static string BaseUrl(HttpContext ctx, string prefix) => $"{ctx.Request.Scheme}://{ctx.Request.Host}{prefix}";

    internal static string User(HttpContext ctx) =>
        (ctx.Items[SmartPrincipal.ItemKey] as SmartPrincipal)?.ClientId ?? ctx.User.Identity?.Name ?? ctx.Request.Headers["X-User"].FirstOrDefault() ?? "anonymous";

    private sealed class FhirResult(string body, int status, string? location, string? etag) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = FhirJsonType + "; charset=utf-8";
            if (location is not null) ctx.Response.Headers.Location = location;
            if (etag is not null) ctx.Response.Headers.ETag = $"W/\"{etag}\"";
            return ctx.Response.WriteAsync(body);
        }
    }
}
