using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Medical.Core;
using Medical.Terminology;

namespace Medical.Bpjs.InaCbg;

/// <summary>1 = rawat inap (inpatient), 2 = rawat jalan (outpatient).</summary>
public enum CareType { Inpatient = 1, Outpatient = 2 }

/// <summary>1 doctor's approval, 2 referred, 3 at own request, 4 died, 5 other.</summary>
public enum DischargeStatus { DoctorApproval = 1, Referred = 2, OwnRequest = 3, Died = 4, Other = 5 }

/// <summary>Hospital tariff breakdown (rupiah) as required by E-Klaim <c>tarif_rs</c>.</summary>
public sealed record HospitalTariff
{
    public decimal ProsedurNonBedah { get; init; }
    public decimal ProsedurBedah { get; init; }
    public decimal Konsultasi { get; init; }
    public decimal TenagaAhli { get; init; }
    public decimal Keperawatan { get; init; }
    public decimal Penunjang { get; init; }
    public decimal Radiologi { get; init; }
    public decimal Laboratorium { get; init; }
    public decimal PelayananDarah { get; init; }
    public decimal Rehabilitasi { get; init; }
    public decimal Kamar { get; init; }
    public decimal RawatIntensif { get; init; }
    public decimal Obat { get; init; }
    public decimal ObatKronis { get; init; }
    public decimal ObatKemoterapi { get; init; }
    public decimal Alkes { get; init; }
    public decimal Bmhp { get; init; }
    public decimal SewaAlat { get; init; }

    public decimal Total => ProsedurNonBedah + ProsedurBedah + Konsultasi + TenagaAhli + Keperawatan + Penunjang + Radiologi + Laboratorium +
                            PelayananDarah + Rehabilitasi + Kamar + RawatIntensif + Obat + ObatKronis + ObatKemoterapi + Alkes + Bmhp + SewaAlat;
}

/// <summary>Everything E-Klaim needs to group a JKN claim with INA-CBG (the grouper itself runs inside E-Klaim).</summary>
public sealed record InaCbgClaim
{
    public required string NomorSep { get; init; }
    public required string NomorKartu { get; init; }
    public required string NomorRm { get; init; }
    public required string NamaPasien { get; init; }
    public required DateOnly TanggalLahir { get; init; }
    public required Gender Gender { get; init; }
    public required DateTimeOffset TanggalMasuk { get; init; }
    public required DateTimeOffset TanggalPulang { get; init; }
    public CareType JenisRawat { get; init; } = CareType.Outpatient;
    /// <summary>Kelas rawat 1, 2 or 3.</summary>
    public int KelasRawat { get; init; } = 3;
    public DischargeStatus DischargeStatus { get; init; } = DischargeStatus.DoctorApproval;
    /// <summary>ICD-10 codes, primary first.</summary>
    public required IReadOnlyList<string> Diagnoses { get; init; }
    /// <summary>ICD-9-CM procedure codes.</summary>
    public IReadOnlyList<string> Procedures { get; init; } = [];
    public int? BirthWeightGram { get; init; }
    public int IcuDays { get; init; }
    public int VentilatorHours { get; init; }
    public int AdlSubAcute { get; init; }
    public int AdlChronic { get; init; }
    public required HospitalTariff Tariff { get; init; }
    /// <summary>Hospital tariff class code in E-Klaim (e.g. "AP", "BP", "CP"…), set by the hospital's agreement.</summary>
    public string KodeTarif { get; init; } = "CP";
    public required string CoderNik { get; init; }
    public string DokterPenanggungJawab { get; init; } = "";

    public int LengthOfStay => JenisRawat == CareType.Outpatient ? 1 : Math.Max(1, TanggalPulang.Date.Subtract(TanggalMasuk.Date).Days);

    private static string Dt(DateTimeOffset d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary><c>new_claim</c> data block.</summary>
    public JsonObject NewClaimData() => new()
    {
        ["nomor_kartu"] = NomorKartu, ["nomor_sep"] = NomorSep, ["nomor_rm"] = NomorRm, ["nama_pasien"] = NamaPasien,
        ["tgl_lahir"] = TanggalLahir.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00", ["gender"] = Gender == Gender.Female ? "2" : "1",
    };

    /// <summary><c>set_claim_data</c> data block.</summary>
    public JsonObject ClaimData()
    {
        static string N(decimal v) => v.ToString("0", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["nomor_sep"] = NomorSep, ["nomor_kartu"] = NomorKartu,
            ["tgl_masuk"] = Dt(TanggalMasuk), ["tgl_pulang"] = Dt(TanggalPulang),
            ["jenis_rawat"] = ((int)JenisRawat).ToString(CultureInfo.InvariantCulture), ["kelas_rawat"] = KelasRawat.ToString(CultureInfo.InvariantCulture),
            ["adl_sub_acute"] = AdlSubAcute.ToString(CultureInfo.InvariantCulture), ["adl_chronic"] = AdlChronic.ToString(CultureInfo.InvariantCulture),
            ["icu_indikator"] = IcuDays > 0 ? "1" : "0", ["icu_los"] = IcuDays.ToString(CultureInfo.InvariantCulture),
            ["ventilator_hour"] = VentilatorHours.ToString(CultureInfo.InvariantCulture),
            ["upgrade_class_ind"] = "0", ["birth_weight"] = (BirthWeightGram ?? 0).ToString(CultureInfo.InvariantCulture),
            ["discharge_status"] = ((int)DischargeStatus).ToString(CultureInfo.InvariantCulture),
            ["diagnosa"] = string.Join('#', Diagnoses), ["procedure"] = string.Join('#', Procedures),
            ["tarif_rs"] = new JsonObject
            {
                ["prosedur_non_bedah"] = N(Tariff.ProsedurNonBedah), ["prosedur_bedah"] = N(Tariff.ProsedurBedah), ["konsultasi"] = N(Tariff.Konsultasi),
                ["tenaga_ahli"] = N(Tariff.TenagaAhli), ["keperawatan"] = N(Tariff.Keperawatan), ["penunjang"] = N(Tariff.Penunjang),
                ["radiologi"] = N(Tariff.Radiologi), ["laboratorium"] = N(Tariff.Laboratorium), ["pelayanan_darah"] = N(Tariff.PelayananDarah),
                ["rehabilitasi"] = N(Tariff.Rehabilitasi), ["kamar"] = N(Tariff.Kamar), ["rawat_intensif"] = N(Tariff.RawatIntensif),
                ["obat"] = N(Tariff.Obat), ["obat_kronis"] = N(Tariff.ObatKronis), ["obat_kemoterapi"] = N(Tariff.ObatKemoterapi),
                ["alkes"] = N(Tariff.Alkes), ["bmhp"] = N(Tariff.Bmhp), ["sewa_alat"] = N(Tariff.SewaAlat),
            },
            ["tarif_poli_eks"] = "0", ["nama_dokter"] = DokterPenanggungJawab, ["kode_tarif"] = KodeTarif,
            ["payor_id"] = "3", ["payor_cd"] = "JKN", ["cob_cd"] = "#", ["coder_nik"] = CoderNik,
        };
    }
}

public enum ClaimIssueSeverity { Error, Warning }

public sealed record ClaimIssue(ClaimIssueSeverity Severity, string Field, Localized Message)
{
    public override string ToString() => $"{Severity} {Field}: {Message.En}";
}

/// <summary>
/// Builds and checks INA-CBG claim inputs before they are sent to E-Klaim, so rejections are caught at the coder's desk:
/// SEP and card formats, ICD-10 / ICD-9-CM code shape and existence, primary-diagnosis rules (no external-cause V–Y codes,
/// symptom R-codes flagged), dates and length of stay, newborn birth weight, ICU/ventilator consistency and tariff.
/// Coding rules are a practical subset of the national coding guidelines; the E-Klaim grouper remains authoritative.
/// </summary>
public static partial class InaCbgClaimBuilder
{
    [GeneratedRegex(@"^[0-9A-Z]{19}$")] private static partial Regex SepRx();
    [GeneratedRegex(@"^\d{13}$")] private static partial Regex CardRx();
    [GeneratedRegex(@"^\d{2}(\.\d{1,2})?$")] private static partial Regex Icd9Rx();

    /// <summary>Claim input from a patient record: active ICD-10 problems become diagnoses (primary first).</summary>
    public static InaCbgClaim FromRecord(PatientRecord r, string nomorSep, string nomorKartu, HospitalTariff tariff, string coderNik,
        IReadOnlyList<string>? procedures = null, CareType? careType = null, DischargeStatus discharge = DischargeStatus.DoctorApproval, int kelas = 3)
    {
        var enc = r.Encounters.FirstOrDefault() ?? throw new InvalidOperationException("The record has no encounter.");
        var type = careType ?? (enc.Class == EncounterClass.Inpatient ? CareType.Inpatient : CareType.Outpatient);
        return new InaCbgClaim
        {
            NomorSep = nomorSep, NomorKartu = nomorKartu, NomorRm = r.Patient.Mrn, NamaPasien = r.Patient.Name.Full.ToUpperInvariant(),
            TanggalLahir = r.Patient.BirthDate, Gender = r.Patient.Gender,
            TanggalMasuk = enc.Start, TanggalPulang = enc.End ?? (type == CareType.Inpatient ? enc.Start.AddDays(3) : enc.Start.AddHours(2)),
            JenisRawat = type, KelasRawat = kelas, DischargeStatus = discharge,
            Diagnoses = r.Conditions.Where(c => c.Code.System is CodeSystems.Icd10 or CodeSystems.Icd10Cm && c.Status is ConditionStatus.Active or ConditionStatus.Recurrence)
                .Select(c => c.Code.Code).ToArray(),
            Procedures = procedures ?? [],
            Tariff = tariff, CoderNik = coderNik, DokterPenanggungJawab = enc.Practitioner ?? "",
        };
    }

    public static IReadOnlyList<ClaimIssue> Validate(InaCbgClaim c, TerminologyService? terms = null)
    {
        terms ??= TerminologyService.Default;
        var issues = new List<ClaimIssue>();
        void Err(string f, string en, string id) => issues.Add(new(ClaimIssueSeverity.Error, f, new(en, id)));
        void Warn(string f, string en, string id) => issues.Add(new(ClaimIssueSeverity.Warning, f, new(en, id)));

        if (!SepRx().IsMatch(c.NomorSep)) Err("nomor_sep", "SEP number must be 19 characters (e.g. 0301R0011126V000123).", "Nomor SEP harus 19 karakter (mis. 0301R0011126V000123).");
        if (!CardRx().IsMatch(c.NomorKartu)) Err("nomor_kartu", "JKN card number must have 13 digits.", "Nomor kartu JKN harus 13 digit.");
        if (c.KelasRawat is < 1 or > 3) Err("kelas_rawat", "Care class must be 1, 2 or 3.", "Kelas rawat harus 1, 2 atau 3.");
        if (c.TanggalPulang < c.TanggalMasuk) Err("tgl_pulang", "Discharge is before admission.", "Tanggal pulang sebelum tanggal masuk.");
        if (c.JenisRawat == CareType.Outpatient && c.TanggalPulang.Date != c.TanggalMasuk.Date)
            Err("tgl_pulang", "Outpatient claims must start and end on the same day.", "Klaim rawat jalan harus pada hari yang sama.");
        if (c.CoderNik.Length != 16 || !c.CoderNik.All(char.IsAsciiDigit)) Err("coder_nik", "Coder NIK must have 16 digits.", "NIK koder harus 16 digit.");

        if (c.Diagnoses.Count == 0) Err("diagnosa", "At least one ICD-10 diagnosis is required.", "Minimal satu diagnosis ICD-10 wajib diisi.");
        for (int i = 0; i < c.Diagnoses.Count; i++)
        {
            string code = c.Diagnoses[i].Trim().ToUpperInvariant();
            string field = i == 0 ? "diagnosa[utama]" : $"diagnosa[{i + 1}]";
            if (!CodeValidators.IsIcd10Format(code)) { Err(field, $"'{code}' is not an ICD-10 code.", $"'{code}' bukan kode ICD-10."); continue; }
            if (terms.Icd10.Lookup(code) is null)
                Warn(field, $"'{code}' is not in the loaded ICD-10 edition — check the code with the coder.", $"'{code}' tidak ada di edisi ICD-10 yang dimuat — periksa kembali.");
            if (i == 0 && code[0] is 'V' or 'W' or 'X' or 'Y')
                Err(field, "External-cause codes (V01–Y98) cannot be the primary diagnosis; code the injury (S/T) first.",
                    "Kode sebab luar (V01–Y98) tidak boleh menjadi diagnosis utama; kode cederanya (S/T) terlebih dahulu.");
            if (i == 0 && code[0] == 'R' && c.Diagnoses.Skip(1).Any(d => d.Length > 0 && d[0] is not ('R' or 'Z')))
                Warn(field, "A symptom (R) code is primary while a definitive diagnosis is present — review the sequencing.",
                     "Kode gejala (R) menjadi diagnosis utama padahal ada diagnosis definitif — tinjau urutan kode.");
            if (i == 0 && code[0] == 'P' && c.BirthWeightGram is null or 0)
                Err("birth_weight", "Perinatal (P) primary diagnosis requires the birth weight.", "Diagnosis utama perinatal (P) wajib disertai berat lahir.");
        }
        if (c.Diagnoses.Count != c.Diagnoses.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            Warn("diagnosa", "Duplicate diagnosis codes.", "Terdapat kode diagnosis ganda.");
        foreach (var p in c.Procedures)
            if (!Icd9Rx().IsMatch(p.Trim())) Err("procedure", $"'{p}' is not an ICD-9-CM procedure code (e.g. 87.44).", $"'{p}' bukan kode tindakan ICD-9-CM (mis. 87.44).");

        if (c.IcuDays > 0 && c.JenisRawat != CareType.Inpatient) Err("icu_los", "ICU days are only valid for inpatient claims.", "Hari ICU hanya untuk rawat inap.");
        if (c.IcuDays > c.LengthOfStay) Err("icu_los", "ICU days exceed the length of stay.", "Hari ICU melebihi lama rawat.");
        if (c.VentilatorHours > c.IcuDays * 24 && c.VentilatorHours > 0) Warn("ventilator_hour", "Ventilator hours exceed ICU time.", "Jam ventilator melebihi waktu di ICU.");
        if (c.Tariff.Total <= 0) Err("tarif_rs", "The hospital tariff is empty.", "Tarif rumah sakit kosong.");
        if (c.DischargeStatus == DischargeStatus.Died && c.JenisRawat == CareType.Outpatient)
            Warn("discharge_status", "Death recorded on an outpatient claim — confirm the care type.", "Status meninggal pada klaim rawat jalan — konfirmasi jenis rawat.");
        return issues;
    }

    public static bool IsValid(IReadOnlyList<ClaimIssue> issues) => issues.All(i => i.Severity != ClaimIssueSeverity.Error);
}
