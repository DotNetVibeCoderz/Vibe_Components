using System.Net;
using System.Text;
using Medical.Clinical;
using Medical.Core;
using Medical.Fhir;
using Medical.Hl7;
using Medical.Privacy;
using Medical.Rules;
using Medical.Terminology;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Medical.AspNetCore;

/// <summary>JSON clinical API: scores, decision support, terminology, HL7 conversion, de-identification.</summary>
public static class ClinicalEndpoints
{
    public static RouteGroupBuilder MapMedicalApi(this IEndpointRouteBuilder app, string prefix = "/api")
    {
        var g = app.MapGroup(prefix).WithTags("Medical.Net");

        g.MapGet("/info", () => Results.Ok(new { MedicalInfo.Name, MedicalInfo.Version, MedicalInfo.Tagline, MedicalInfo.Credit, MedicalInfo.CreditId, MedicalInfo.Disclaimer }));

        g.MapPost("/scores/news2", (VitalSigns v) => Results.Ok(ClinicalScores.News2(v)));
        g.MapPost("/scores/qsofa", (VitalSigns v) => Results.Ok(ClinicalScores.QSofa(v)));
        g.MapGet("/calc/egfr", (double creatinine, int age, Gender sex) => Results.Ok(Calculators.EgfrCkdEpi2021(creatinine, age, sex)));
        g.MapGet("/calc/bmi", (double weight, double height, BmiStandard? standard) => Results.Ok(Calculators.Bmi(weight, height, standard ?? BmiStandard.Who)));
        g.MapGet("/calc/dose", (string drug, double weight, double? crcl) =>
        {
            try { return Results.Ok(DoseCalculator.Calculate(drug, weight, crcl)); }
            catch (KeyNotFoundException e) { return Results.NotFound(new { error = e.Message }); }
        });

        g.MapPost("/cds/evaluate", (PatientRecord record, ClinicalDecisionSupport cds) => Results.Ok(cds.Evaluate(record)));
        g.MapGet("/cds/synthetic/{index:int}", (int index, double? deterioration, ClinicalDecisionSupport cds) =>
        {
            var r = SyntheticData.Record(index, deterioration ?? 0.8);
            return Results.Ok(new { patient = r.Patient, alerts = cds.Evaluate(r) });
        });

        g.MapGet("/terminology/search", (string q, string? system, int? limit, TerminologyService t) =>
            Results.Ok(t.Search(q, system, limit ?? 20).Select(h => new { system = h.Concept.System, h.Concept.Code, h.Concept.Display, h.Concept.DisplayId, h.Score })));
        g.MapGet("/terminology/validate", (string system, string code, TerminologyService t) => Results.Ok(t.Validate(system, code)));
        g.MapGet("/terminology/snomed/{id}/icd10", (string id, TerminologyService t) =>
            t.SnomedToIcd10(id) is { } m ? Results.Ok(new { source = m.Source.Code, target = m.Target.Code, m.Target.Display, m.Equivalence }) : Results.NotFound());

        g.MapPost("/hl7/to-fhir", async (HttpRequest req, FhirMapper mapper) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            try
            {
                var msg = Hl7V2.Parse(await reader.ReadToEndAsync());
                return FhirEndpoints.Fhir(Hl7Mapping.ToFhirTransaction(msg, mapper));
            }
            catch (Exception e) when (e is NHapi.Base.HL7Exception or InvalidDataException)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

        g.MapPost("/privacy/deidentify", async (HttpRequest req, TextDeidentifier deid, IAuditLog audit) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var result = deid.Deidentify(await reader.ReadToEndAsync());
            audit.Record(new AuditEvent(DateTimeOffset.UtcNow, FhirEndpoints.User(req.HttpContext), AuditAction.Deidentify, "text", Purpose: $"{result.Findings.Count} identifiers"));
            return Results.Ok(new { text = result.Text, counts = result.Counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });
        });

        g.MapGet("/audit", (string? patient, IAuditLog audit) => Results.Ok(audit.Query(patient).TakeLast(200)));
        return g;
    }
}

/// <summary>Options for <see cref="MllpListenerService"/>.</summary>
public sealed class MllpListenerOptions
{
    public int Port { get; set; } = 2575;
    public IPAddress Address { get; set; } = IPAddress.Any;
    /// <summary>Write received ADT/ORU content into the FHIR store.</summary>
    public bool StoreInFhir { get; set; } = true;
}

/// <summary>Hosted HL7 v2 MLLP listener: ACKs every message, converts it to FHIR and runs decision support on ORU results.</summary>
public sealed class MllpListenerService(MllpListenerOptions options, InMemoryFhirStore store, FhirMapper mapper, IMedicalEventBus bus, ILogger<MllpListenerService> log) : BackgroundService
{
    private MllpServer? _server;

    public int MessagesReceived => (int)(_server?.MessagesReceived ?? 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _server = new MllpServer(new IPEndPoint(options.Address, options.Port), Handle, bus);
        _server.Start();
        log.LogInformation("HL7 MLLP listener on {Endpoint}", _server.Endpoint);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        await _server.DisposeAsync();
    }

    private ValueTask<string> Handle(Hl7Message m, CancellationToken ct)
    {
        log.LogInformation("HL7 {Type} #{Control} from {App}", m.Type, m.ControlId, m.SendingApplication);
        if (options.StoreInFhir && m.Segments("PID").Count > 0)
            store.Process(Hl7Mapping.ToFhirTransaction(m, mapper));
        return ValueTask.FromResult(Hl7V2.BuildAck(m));
    }
}

public static class MedicalAspNetCoreExtensions
{
    /// <summary>Registers what the endpoints need (FHIR, terminology, rules, privacy) on top of <c>AddMedical()</c>.</summary>
    public static MedicalBuilder AddMedicalApi(this MedicalBuilder builder) =>
        builder.AddFhir().AddTerminology().AddRules().AddPrivacy().AddHl7();

    /// <summary>Adds the hosted MLLP listener.</summary>
    public static MedicalBuilder AddMllpListener(this MedicalBuilder builder, Action<MllpListenerOptions>? configure = null) =>
        builder.TryAddModule("mllp-listener", s =>
        {
            var o = new MllpListenerOptions();
            configure?.Invoke(o);
            s.TryAddSingleton(o);
            s.AddHostedService<MllpListenerService>();
        });
}
