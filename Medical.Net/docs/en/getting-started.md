# Getting started

## Requirements

- .NET 10 SDK
- Optional: Rust (stable) to rebuild the native kernels — prebuilt binaries ship inside `Medical.Native`, and a managed fallback runs everywhere
- Optional: Python 3.10+ for the MONAI imaging service, SQL Server and Redis for production EMR

## Install

```bash
dotnet add package Medical.Rules        # pulls Medical.Clinical, Medical.Terminology, Medical.Core
dotnet add package Medical.Fhir
```

## A patient record

Everything in Medical.Net works on the immutable `PatientRecord` (patient, encounters, vitals, observations, conditions, medications, allergies). For experiments use the deterministic generator — it contains no real PHI and gives the same data on every machine:

```csharp
using Medical.Core;

PatientRecord record = SyntheticData.Record(index: 3, deterioration: 0.8);
Patient p = record.Patient;                     // name, MRN, NIK (structurally valid, province 99), birth date…
VitalSigns? v = record.LatestVitals;
ClinicalObservation? lactate = record.Latest(Loinc.Lactate);
```

## Scores and decision support

```csharp
using Medical.Clinical;
using Medical.Rules;

ScoreResult news2 = ClinicalScores.News2(v!);
Console.WriteLine($"{news2.Score} {news2.Risk}: {news2.Interpretation.Id}");   // bilingual

foreach (ClinicalAlert a in new ClinicalDecisionSupport().Evaluate(record))
    Console.WriteLine($"[{a.Severity}] {a.Title.En} — {a.Detail.En}");
```

## Export to FHIR

```csharp
using Medical.Fhir;

var mapper = new FhirMapper();
var ips = mapper.ToPatientSummary(record);                // FHIR R4 document bundle (IPS-style)
Console.WriteLine(FhirJson.Validate(ips).Issue.Count);    // 0
File.WriteAllText("ips.json", FhirJson.Serialize(ips));
var tx = mapper.ToTransaction(record);                    // PUT-by-id transaction for any FHIR server
```

## Dependency injection

```csharp
services.AddMedical(m => m
    .AddTerminology()
    .AddRules()
    .AddFhir()
    .AddPrivacy(pseudonymSecret: config["Privacy:Secret"])
    .AddEmr(o => { o.Provider = EmrProvider.SqlServer; o.ConnectionString = config["ConnectionStrings:Emr"]!; o.Redis = "localhost:6379"; })
    .AddAI());
```

Next: [Architecture](architecture.md) · [Modules](modules.md) · [Tools](tools.md)
