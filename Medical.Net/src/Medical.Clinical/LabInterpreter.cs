using Medical.Core;

namespace Medical.Clinical;

/// <summary>Adult reference interval and critical limits for one LOINC code.</summary>
public sealed record ReferenceRange(ClinicalCode Code, string Unit, double Low, double High, double? CriticalLow = null, double? CriticalHigh = null,
    double? FemaleLow = null, double? FemaleHigh = null);

public sealed record LabInterpretation(ClinicalObservation Observation, ResultFlag Flag, Localized Message);

/// <summary>Flags lab results against adult reference intervals and critical (panic) values.</summary>
public sealed class LabInterpreter
{
    private readonly Dictionary<string, ReferenceRange> _ranges = new(StringComparer.Ordinal);

    public LabInterpreter(IEnumerable<ReferenceRange>? ranges = null)
    {
        foreach (var r in ranges ?? Default) _ranges[r.Code.Code] = r;
    }

    /// <summary>Typical adult ranges (illustrative; laboratories must use their own validated intervals).</summary>
    public static IReadOnlyList<ReferenceRange> Default { get; } =
    [
        new(Loinc.Glucose, "mg/dL", 70, 140, 40, 450),
        new(Loinc.HbA1c, "%", 4.0, 5.6),
        new(Loinc.Creatinine, "mg/dL", 0.7, 1.3, null, 10, 0.6, 1.1),
        new(Loinc.Hemoglobin, "g/dL", 13.5, 17.5, 7, 20, 12.0, 15.5),
        new(Loinc.Wbc, "10*3/uL", 4.0, 11.0, 2, 30),
        new(Loinc.Platelets, "10*3/uL", 150, 400, 20, 1000),
        new(Loinc.Potassium, "mmol/L", 3.5, 5.1, 2.5, 6.5),
        new(Loinc.Sodium, "mmol/L", 135, 145, 120, 160),
        new(Loinc.Lactate, "mmol/L", 0.5, 2.0, null, 4.0),
        new(Loinc.Bilirubin, "mg/dL", 0.1, 1.2, null, 15),
        new(Loinc.Inr, "1", 0.8, 1.2, null, 5),
        new(Loinc.Albumin, "g/dL", 3.5, 5.0),
        new(Loinc.Cholesterol, "mg/dL", 0, 200),
        new(Loinc.Ldl, "mg/dL", 0, 130),
        new(Loinc.Hdl, "mg/dL", 40, 100),
        new(Loinc.Triglycerides, "mg/dL", 0, 150),
        new(Loinc.Alt, "U/L", 7, 56),
        new(Loinc.Crp, "mg/L", 0, 10),
    ];

    public ReferenceRange? RangeFor(ClinicalCode code) => _ranges.GetValueOrDefault(code.Code);

    public LabInterpretation Interpret(ClinicalObservation o, Gender gender = Gender.Unknown)
    {
        if (o.Value is not { } v || RangeFor(o.Code) is not { } r)
            return new(o, o.Flag, new("No reference interval available.", "Rentang rujukan tidak tersedia."));
        if (o.Unit is { } u && u != r.Unit && Ucum.IsKnown(u) && Ucum.IsKnown(r.Unit)) v = Ucum.Convert(v, u, r.Unit);
        double lo = gender == Gender.Female && r.FemaleLow is { } fl ? fl : r.Low;
        double hi = gender == Gender.Female && r.FemaleHigh is { } fh ? fh : r.High;
        string name = o.Code.Display ?? o.Code.Code;
        if (r.CriticalLow is { } cl && v < cl)
            return new(o, ResultFlag.CriticalLow, new($"CRITICAL LOW {name} {v:0.##} {r.Unit} — notify the clinician immediately.", $"KRITIS RENDAH {name} {v:0.##} {r.Unit} — segera laporkan ke dokter."));
        if (r.CriticalHigh is { } ch && v > ch)
            return new(o, ResultFlag.CriticalHigh, new($"CRITICAL HIGH {name} {v:0.##} {r.Unit} — notify the clinician immediately.", $"KRITIS TINGGI {name} {v:0.##} {r.Unit} — segera laporkan ke dokter."));
        if (v < lo) return new(o, ResultFlag.Low, new($"Low ({lo}–{hi} {Ucum.Display(r.Unit)}).", $"Rendah ({lo}–{hi} {Ucum.Display(r.Unit)})."));
        if (v > hi) return new(o, ResultFlag.High, new($"High ({lo}–{hi} {Ucum.Display(r.Unit)}).", $"Tinggi ({lo}–{hi} {Ucum.Display(r.Unit)})."));
        return new(o, ResultFlag.Normal, new($"Within reference interval ({lo}–{hi} {Ucum.Display(r.Unit)}).", $"Dalam rentang rujukan ({lo}–{hi} {Ucum.Display(r.Unit)})."));
    }

    public IReadOnlyList<LabInterpretation> InterpretAll(PatientRecord record) =>
        record.Observations.Select(o => Interpret(o, record.Patient.Gender)).ToArray();
}
