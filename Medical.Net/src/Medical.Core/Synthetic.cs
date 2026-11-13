namespace Medical.Core;

/// <summary>
/// Small deterministic PRNG (SplitMix64). Output depends only on the seed — identical across processes, platforms
/// and .NET versions — so synthetic cohorts, demos and tests are reproducible.
/// </summary>
public sealed class SeededRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public int Next(int maxExclusive) => (int)(NextDouble() * maxExclusive);

    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    public double Uniform(double min, double max) => min + NextDouble() * (max - min);

    /// <summary>Standard normal via Box–Muller.</summary>
    public double Gaussian(double mean = 0, double sd = 1)
    {
        double u1 = 1.0 - NextDouble(), u2 = NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>Stable 64-bit FNV-1a hash of a string — use it (never <c>string.GetHashCode</c>) to derive seeds.</summary>
    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}

/// <summary>Deterministic synthetic patients, vitals and labs for demos, tests and model training. Contains no real PHI.</summary>
public static class SyntheticData
{
    private static readonly string[] GivenMale = ["Budi", "Agus", "Rizky", "Andi", "Dimas", "Fajar", "Hendra", "Arif", "John", "Michael", "David", "Ahmad", "Yusuf", "Bayu"];
    private static readonly string[] GivenFemale = ["Siti", "Dewi", "Ayu", "Rina", "Putri", "Nur", "Indah", "Maya", "Sarah", "Emily", "Laura", "Fitri", "Ratna", "Lestari"];
    private static readonly string[] Family = ["Santoso", "Wijaya", "Pratama", "Saputra", "Hidayat", "Kusuma", "Nugroho", "Lestari", "Siregar", "Harahap", "Smith", "Johnson", "Tan", "Lim", "Wibowo", "Setiawan"];
    private static readonly string[] Cities = ["Jakarta", "Bandung", "Surabaya", "Yogyakarta", "Medan", "Makassar", "Semarang", "Denpasar", "Palembang", "Balikpapan"];
    private static readonly string[] Streets = ["Jl. Merdeka", "Jl. Sudirman", "Jl. Diponegoro", "Jl. Gatot Subroto", "Jl. Ahmad Yani", "Jl. Pahlawan", "Jl. Cendana", "Jl. Melati"];

    /// <summary>Creates a reproducible synthetic patient. The NIK is structurally valid but never belongs to a real person (province code 99).</summary>
    public static Patient Patient(int index, ulong seed = 2026)
    {
        var r = new SeededRandom(seed * 7919 + (ulong)index);
        var gender = r.Chance(0.5) ? Gender.Male : Gender.Female;
        string given = r.Pick(gender == Gender.Male ? GivenMale : GivenFemale);
        string family = r.Pick(Family);
        var birth = new DateOnly(1940, 1, 1).AddDays(r.Next(0, 365 * 64)); // adults born 1940–2003
        string city = r.Pick(Cities);
        int day = birth.Day + (gender == Gender.Female ? 40 : 0);
        string nik = $"99{r.Next(10, 99):00}{r.Next(10, 99):00}{day:00}{birth.Month:00}{birth.Year % 100:00}{r.Next(1, 9999):0000}";
        return new Patient
        {
            Id = $"pat-{index:00000}",
            Mrn = $"MRN{100000 + index}",
            Name = new HumanName(given, family),
            Gender = gender,
            BirthDate = birth,
            Nik = nik,
            Phone = $"+62 8{r.Next(11, 99)} {r.Next(1000, 9999)} {r.Next(1000, 9999)}",
            Email = $"{given.ToLowerInvariant()}.{family.ToLowerInvariant()}{index}@example.org",
            Address = $"{r.Pick(Streets)} No. {r.Next(1, 200)}",
            City = city,
        };
    }

    public static IReadOnlyList<Patient> Cohort(int count, ulong seed = 2026) =>
        Enumerable.Range(1, count).Select(i => Patient(i, seed)).ToArray();

    /// <summary>
    /// Vital-sign time series every <paramref name="intervalMinutes"/>. <paramref name="deterioration"/> in [0, 1] blends a
    /// healthy baseline towards a septic picture (tachycardia, tachypnoea, fever, hypotension, desaturation, confusion).
    /// </summary>
    public static IReadOnlyList<VitalSigns> VitalsSeries(int points, double deterioration, DateTimeOffset start, int intervalMinutes = 60, ulong seed = 7)
    {
        var r = new SeededRandom(seed);
        var list = new List<VitalSigns>(points);
        for (int i = 0; i < points; i++)
        {
            double t = points <= 1 ? deterioration : deterioration * i / (points - 1.0);
            double sbp = Lerp(124, 86, t) + r.Gaussian(0, 4);
            list.Add(new VitalSigns
            {
                Timestamp = start.AddMinutes(i * intervalMinutes),
                HeartRate = Math.Round(Lerp(76, 128, t) + r.Gaussian(0, 3)),
                RespiratoryRate = Math.Round(Lerp(15, 27, t) + r.Gaussian(0, 1)),
                SystolicBp = Math.Round(sbp),
                DiastolicBp = Math.Round(sbp * 0.62 + r.Gaussian(0, 3)),
                TemperatureC = Math.Round(Lerp(36.8, 39.3, t) + r.Gaussian(0, 0.15), 1),
                SpO2 = Math.Round(Math.Min(100, Lerp(98, 89, t) + r.Gaussian(0, 0.7))),
                OnSupplementalOxygen = t > 0.75,
                Consciousness = t > 0.85 ? Consciousness.Confusion : Consciousness.Alert,
            });
        }
        return list;
    }

    /// <summary>A realistic synthetic record: patient, encounter, vitals, labs, problems, medications and allergies.</summary>
    public static PatientRecord Record(int index, double deterioration = 0.0, ulong seed = 2026)
    {
        var p = Patient(index, seed);
        var r = new SeededRandom(SeededRandom.Hash(p.Id) ^ seed);
        var start = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.FromHours(7));
        var enc = new Encounter
        {
            Id = $"enc-{index:00000}", PatientId = p.Id, Class = deterioration > 0.5 ? EncounterClass.Inpatient : EncounterClass.Ambulatory,
            Start = start, Practitioner = "dr. Ratna Kusuma, Sp.PD", Location = deterioration > 0.5 ? "Ward 3B" : "Poli Penyakit Dalam",
        };
        bool diabetic = r.Chance(0.45), hypertensive = r.Chance(0.5), ckd = r.Chance(0.2);
        var conditions = new List<Condition>();
        if (diabetic) conditions.Add(new Condition { PatientId = p.Id, Code = ClinicalCode.Icd10("E11.9", "Type 2 diabetes mellitus without complications"), Onset = new DateOnly(2018, 3, 1) });
        if (hypertensive) conditions.Add(new Condition { PatientId = p.Id, Code = ClinicalCode.Icd10("I10", "Essential (primary) hypertension"), Onset = new DateOnly(2016, 6, 1) });
        if (ckd) conditions.Add(new Condition { PatientId = p.Id, Code = ClinicalCode.Icd10("N18.3", "Chronic kidney disease, stage 3"), Onset = new DateOnly(2022, 1, 1) });
        if (deterioration > 0.5) conditions.Add(new Condition { PatientId = p.Id, Code = ClinicalCode.Icd10("J18.9", "Pneumonia, unspecified organism"), Onset = DateOnly.FromDateTime(start.Date) });

        double glucose = diabetic ? r.Uniform(150, 260) : r.Uniform(78, 110);
        double creat = ckd ? r.Uniform(1.6, 2.4) : r.Uniform(0.6, 1.1);
        var obs = new List<ClinicalObservation>
        {
            Lab(p.Id, enc.Id, Loinc.Glucose, glucose, "mg/dL", 70, 140, start),
            Lab(p.Id, enc.Id, Loinc.HbA1c, diabetic ? r.Uniform(7.2, 9.8) : r.Uniform(4.8, 5.6), "%", 4, 5.6, start),
            Lab(p.Id, enc.Id, Loinc.Creatinine, creat, "mg/dL", 0.6, 1.2, start),
            Lab(p.Id, enc.Id, Loinc.Hemoglobin, r.Uniform(10.5, 15.5), "g/dL", 12, 16, start),
            Lab(p.Id, enc.Id, Loinc.Wbc, Lerp(7, 17, deterioration) + r.Gaussian(0, 0.8), "10*3/uL", 4, 11, start),
            Lab(p.Id, enc.Id, Loinc.Platelets, Lerp(260, 120, deterioration) + r.Gaussian(0, 15), "10*3/uL", 150, 400, start),
            Lab(p.Id, enc.Id, Loinc.Potassium, r.Uniform(3.6, ckd ? 5.6 : 4.8), "mmol/L", 3.5, 5.1, start),
            Lab(p.Id, enc.Id, Loinc.Sodium, r.Uniform(134, 144), "mmol/L", 135, 145, start),
            Lab(p.Id, enc.Id, Loinc.Lactate, Lerp(1.0, 3.6, deterioration) + r.Gaussian(0, 0.1), "mmol/L", 0.5, 2.0, start),
            Lab(p.Id, enc.Id, Loinc.Cholesterol, r.Uniform(160, 260), "mg/dL", 0, 200, start),
            Lab(p.Id, enc.Id, Loinc.Crp, Lerp(3, 140, deterioration) + r.Uniform(0, 4), "mg/L", 0, 10, start),
        };

        var meds = new List<MedicationOrder>();
        if (diabetic) meds.Add(new MedicationOrder { PatientId = p.Id, Medication = ClinicalCode.RxNorm("861007", "metformin 500 MG Oral Tablet"), Generic = "metformin", DoseMg = 500, FrequencyPerDay = 2, Start = new DateOnly(2018, 3, 1) });
        if (hypertensive) meds.Add(new MedicationOrder { PatientId = p.Id, Medication = ClinicalCode.RxNorm("314076", "lisinopril 10 MG Oral Tablet"), Generic = "lisinopril", DoseMg = 10, Start = new DateOnly(2016, 6, 1) });
        if (r.Chance(0.3)) meds.Add(new MedicationOrder { PatientId = p.Id, Medication = ClinicalCode.RxNorm("855332", "warfarin sodium 5 MG Oral Tablet"), Generic = "warfarin", DoseMg = 5, Start = new DateOnly(2024, 2, 1) });
        if (r.Chance(0.4)) meds.Add(new MedicationOrder { PatientId = p.Id, Medication = ClinicalCode.RxNorm("617314", "atorvastatin 10 MG Oral Tablet"), Generic = "atorvastatin", DoseMg = 10, Start = new DateOnly(2023, 1, 1) });

        var allergies = new List<Allergy>();
        if (r.Chance(0.25)) allergies.Add(new Allergy { PatientId = p.Id, Substance = "penicillin", Reaction = "Urticaria", Severity = AllergySeverity.Moderate });

        return new PatientRecord
        {
            Patient = p,
            Encounters = [enc],
            Vitals = VitalsSeries(12, deterioration, start, 60, SeededRandom.Hash(p.Id)),
            Observations = obs,
            Conditions = conditions,
            Medications = meds,
            Allergies = allergies,
            WeightKg = Math.Round(r.Uniform(48, 98), 1),
            HeightCm = Math.Round(r.Uniform(150, 182)),
        };
    }

    private static ClinicalObservation Lab(string pid, string eid, ClinicalCode code, double value, string unit, double lo, double hi, DateTimeOffset at)
    {
        value = Math.Round(value, value < 10 ? 2 : 1);
        var flag = value < lo ? ResultFlag.Low : value > hi ? ResultFlag.High : ResultFlag.Normal;
        return new ClinicalObservation { Id = $"obs-{pid}-{code.Code}", PatientId = pid, EncounterId = eid, Code = code, Value = value, Unit = unit, ReferenceLow = lo, ReferenceHigh = hi, Flag = flag, Effective = at };
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
