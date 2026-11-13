using Medical.Clinical;
using Medical.Core;
using Medical.Fhir;
using Medical.Rules;
using Medical.Terminology;

// Medical.Net clinical console — Made by Gravicode Studios, led by Kang Fadhil.
// Replace SyntheticData with your own PatientRecord source (EMR, FHIR server, HL7 feed).

var record = SyntheticData.Record(index: 3, deterioration: 0.8);
var p = record.Patient;
Console.WriteLine($"{p.Name} · {p.Mrn} · {p.AgeAt(DateOnly.FromDateTime(DateTime.Today))} y · {p.Gender}");

if (record.LatestVitals is { } v)
{
    var news2 = ClinicalScores.News2(v);
    Console.WriteLine($"\n{news2}");
    foreach (var c in news2.Components.Where(c => c.Points > 0)) Console.WriteLine($"  {c.Parameter,-22} {c.Value,-12} +{c.Points}");
}

Console.WriteLine("\nDecision support:");
foreach (var alert in new ClinicalDecisionSupport().Evaluate(record))
    Console.WriteLine($"  [{alert.Severity}] {alert.Title.En} — {alert.Detail.En}");

Console.WriteLine("\nProblem list (ICD-10 → bilingual display):");
foreach (var c in record.Conditions)
    Console.WriteLine($"  {c.Code.Code,-7} {TerminologyService.Default.Lookup(c.Code)?.DisplayFor("id") ?? c.Code.Display}");

var ips = new FhirMapper().ToPatientSummary(record);
File.WriteAllText("patient-summary.json", FhirJson.Serialize(ips));
Console.WriteLine($"\nWrote patient-summary.json (FHIR IPS document, {ips.Entry.Count} entries, {FhirJson.Validate(ips).Issue.Count} validation issues).");
Console.WriteLine($"\n{MedicalInfo.Disclaimer}");
