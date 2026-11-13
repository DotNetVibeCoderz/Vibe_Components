using Medical.Clinical;
using Medical.Core;

namespace Medical.Rules;

/// <summary>Patient-level facts inserted into the rules session.</summary>
public sealed class PatientFact
{
    public required string PatientId { get; init; }
    public int Age { get; init; }
    public Gender Gender { get; init; }
    public double? WeightKg { get; init; }
    public double? Egfr { get; init; }
    public double? CrCl { get; init; }
    public bool SuspectedInfection { get; init; }
    public IReadOnlySet<string> Icd10 { get; init; } = new HashSet<string>();
}

/// <summary>Latest vital signs with their pre-computed scores.</summary>
public sealed class VitalsFact
{
    public required string PatientId { get; init; }
    public required VitalSigns Vitals { get; init; }
    public required ScoreResult News2 { get; init; }
    public required ScoreResult QSofa { get; init; }
    public bool HasRedScore => News2.Components.Any(c => c.Points == 3);
}

public sealed class LabFact
{
    public required string PatientId { get; init; }
    public required ClinicalObservation Observation { get; init; }
    public required ResultFlag Flag { get; init; }
    public required Localized Message { get; init; }
    public string Code => Observation.Code.Code;
    public double Value => Observation.Value ?? double.NaN;
}

public sealed class MedicationFact
{
    public required string PatientId { get; init; }
    public required MedicationOrder Order { get; init; }
    public required IReadOnlySet<string> Classes { get; init; }
    public string Generic => Order.Generic;
    public bool Is(string cls) => Classes.Contains(cls);
}

public sealed class AllergyFact
{
    public required string PatientId { get; init; }
    public required Allergy Allergy { get; init; }
}

/// <summary>Builds facts from a <see cref="PatientRecord"/>.</summary>
public static class FactBuilder
{
    private static readonly string[] InfectionIcd = ["A", "B", "J1", "J2", "N39", "L03", "K35", "K37", "U07"];

    public static IReadOnlyList<object> Build(PatientRecord r, DrugKnowledge drugs, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(r.LatestVitals?.Timestamp.UtcDateTime ?? DateTime.UtcNow);
        int age = r.Patient.AgeAt(day);
        double? creat = r.Latest(Loinc.Creatinine)?.Value;
        var icd = r.Conditions.Where(c => c.Status is ConditionStatus.Active or ConditionStatus.Recurrence).Select(c => c.Code.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var facts = new List<object>
        {
            new PatientFact
            {
                PatientId = r.Patient.Id, Age = age, Gender = r.Patient.Gender, WeightKg = r.WeightKg,
                Egfr = creat is { } c ? Calculators.EgfrCkdEpi2021(c, age, r.Patient.Gender).Egfr : null,
                CrCl = creat is { } c2 && r.WeightKg is { } w ? Calculators.CockcroftGault(c2, age, w, r.Patient.Gender) : null,
                SuspectedInfection = icd.Any(code => InfectionIcd.Any(p => code.StartsWith(p, StringComparison.OrdinalIgnoreCase))),
                Icd10 = icd,
            },
        };
        if (r.LatestVitals is { } v)
            facts.Add(new VitalsFact { PatientId = r.Patient.Id, Vitals = v, News2 = ClinicalScores.News2(v), QSofa = ClinicalScores.QSofa(v) });
        var labs = new LabInterpreter();
        foreach (var o in r.Observations.GroupBy(o => o.Code.Code).Select(g => g.MaxBy(o => o.Effective)!))
        {
            var i = labs.Interpret(o, r.Patient.Gender);
            facts.Add(new LabFact { PatientId = r.Patient.Id, Observation = o, Flag = i.Flag, Message = i.Message });
        }
        foreach (var m in r.Medications)
            facts.Add(new MedicationFact { PatientId = r.Patient.Id, Order = m, Classes = drugs.Classes(m.Generic) });
        foreach (var a in r.Allergies)
            facts.Add(new AllergyFact { PatientId = r.Patient.Id, Allergy = a });
        return facts;
    }
}
