using System.Net;
using System.Text;
using Medical.Core;
using Medical.Fhir;
using Medical.Hl7;
using Medical.Rules;

// Medical.Net HL7 gateway — Made by Gravicode Studios, led by Kang Fadhil.
// Receives HL7 v2 over MLLP, ACKs, converts to FHIR R4, evaluates decision support on results and optionally
// forwards each transaction bundle to a FHIR server (Gateway:FhirServer in appsettings.json).

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMedical(m => m.AddFhir().AddRules().AddHl7());
builder.Services.AddHostedService<Gateway>();
builder.Build().Run();

internal sealed class Gateway(IConfiguration config, FhirMapper mapper, InMemoryFhirStore store, ClinicalDecisionSupport cds, IMedicalEventBus bus, ILogger<Gateway> log) : BackgroundService
{
    private readonly HttpClient _http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int port = config.GetValue("Gateway:Port", 2575);
        string? fhirServer = config["Gateway:FhirServer"];
        await using var server = new MllpServer(new IPEndPoint(IPAddress.Any, port), async (msg, ct) =>
        {
            var bundle = Hl7Mapping.ToFhirTransaction(msg, mapper);
            store.Process(bundle);
            var patient = Hl7Mapping.ToPatient(msg);
            var record = new PatientRecord { Patient = patient, Observations = Hl7Mapping.ToObservations(msg, patient.Id) };
            foreach (var alert in cds.Evaluate(record)) log.LogWarning("{Severity} {Patient}: {Title}", alert.Severity, patient.Mrn, alert.Title.En);
            if (fhirServer is not null)
            {
                using var res = await _http.PostAsync(fhirServer, new StringContent(FhirJson.Serialize(bundle, false), Encoding.UTF8, "application/fhir+json"), ct);
                if (!res.IsSuccessStatusCode) return Hl7V2.BuildAck(msg, "AE", $"FHIR server returned {(int)res.StatusCode}");
            }
            log.LogInformation("{Type} #{Control} → {Entries} FHIR resources", msg.Type, msg.ControlId, bundle.Entry.Count);
            return Hl7V2.BuildAck(msg);
        }, bus);
        server.Start();
        log.LogInformation("HL7 MLLP gateway listening on {Port}{Forward}", port, fhirServer is null ? "" : $", forwarding to {fhirServer}");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
    }
}
