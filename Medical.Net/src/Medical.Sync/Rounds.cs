namespace Medical.Sync.Rounds;

/// <summary>
/// Contracts of the ward-round app: what a nurse's or doctor's device holds offline. The server publishes the ward
/// census (<see cref="RoundsPatient"/>, read-only on devices) and tasks; devices add vital signs and notes (append-only) and
/// complete tasks (last-writer-wins).
/// </summary>
public static class RoundsTypes
{
    public const string Patient = "patient";
    public const string Vitals = "vitals";
    public const string Note = "note";
    public const string Task = "task";

    public static IReadOnlyDictionary<string, ConflictPolicy> Policies { get; } = new Dictionary<string, ConflictPolicy>
    {
        [Patient] = ConflictPolicy.ReadOnly,
        [Vitals] = ConflictPolicy.AppendOnly,
        [Note] = ConflictPolicy.AppendOnly,
        [Task] = ConflictPolicy.LastWriterWins,
    };
}

/// <summary>A patient on the ward list (id = the inpatient visit id).</summary>
public sealed record RoundsPatient(string Id, string PatientId, string Mrn, string Name, int Age, string Sex, string Ward, string Bed,
    string? Diagnosis, string? Allergies, DateTime AdmittedAt, int? News2, DateTime? News2At, string? Payer);

/// <summary>One set of vital signs taken on the device (id generated on the device).</summary>
public sealed record RoundsVitals(string Id, string PatientId, string VisitId, DateTime Time, double RespiratoryRate, double SpO2, bool OnOxygen,
    double SystolicBp, double DiastolicBp, double HeartRate, double TemperatureC, string Avpu, int News2, string RecordedBy);

public sealed record RoundsNote(string Id, string PatientId, string VisitId, DateTime Time, string Author, string Text);

public sealed record RoundsTask(string Id, string PatientId, string VisitId, string Title, DateTime Due, bool Done, string? DoneBy, DateTime? DoneAt);
