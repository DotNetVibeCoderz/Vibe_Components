using System.Globalization;
using Medical.Core;
using NHapi.Base.Model;
using NHapi.Base.Util;
using NHapi.Model.V251.Message;

namespace Medical.Hl7;

/// <summary>ADT trigger events built by <see cref="Hl7V2.BuildAdt"/> (HL7 v2.5.1 chapter 3, Patient Administration).</summary>
public enum AdtEvent
{
    /// <summary>A01 — admit / visit notification (inpatient admission).</summary>
    Admit,
    /// <summary>A02 — transfer a patient (change of assigned location, PV1-3 new / PV1-6 prior).</summary>
    Transfer,
    /// <summary>A03 — discharge / end visit (PV1-45 discharge date/time, PV1-36 disposition).</summary>
    Discharge,
    /// <summary>A04 — register a patient (outpatient or emergency visit, message structure ADT_A01).</summary>
    Register,
}

/// <summary>
/// An HL7 PL (person location) value as used in PV1-3 / PV1-6: point of care (ward / clinic), room, bed and facility
/// (HL7 v2.5.1 §2.A.53).
/// </summary>
public sealed record Hl7Location(string PointOfCare, string? Room = null, string? Bed = null, string? Facility = null)
{
    public override string ToString() => string.Join('^', new[] { PointOfCare, Room ?? "", Bed ?? "", Facility ?? "" }).TrimEnd('^');
}

/// <summary>
/// Visit details for ADT messages beyond the domain <see cref="Encounter"/>. All fields are optional; unset fields are left empty.
/// Field numbers refer to HL7 v2.5.1 PV1 / PV2 (§3.4.3, §3.4.4).
/// </summary>
public sealed record AdtDetails
{
    /// <summary>PV1-3 assigned patient location; defaults to <see cref="Encounter.Location"/> as the point of care.</summary>
    public Hl7Location? Location { get; init; }
    /// <summary>PV1-6 prior patient location (A02 transfer).</summary>
    public Hl7Location? PriorLocation { get; init; }
    /// <summary>PV1-4 admission type (user table 0007: E emergency, R routine, U urgent, A accident, N newborn, C elective).</summary>
    public string? AdmissionType { get; init; }
    /// <summary>PV1-7 attending doctor id (XCN-1).</summary>
    public string? AttendingDoctorId { get; init; }
    /// <summary>PV1-7 attending doctor name (XCN-2 family / full name).</summary>
    public string? AttendingDoctorName { get; init; }
    /// <summary>PV1-10 hospital service (user table 0069, e.g. MED, SUR, ICU, or a local clinic code).</summary>
    public string? HospitalService { get; init; }
    /// <summary>PV1-14 admit source (user table 0023: 1 physician referral, 7 emergency room…).</summary>
    public string? AdmitSource { get; init; }
    /// <summary>PV1-20 financial class (user table 0064, e.g. BPJS / UMUM / ASURANSI).</summary>
    public string? FinancialClass { get; init; }
    /// <summary>PV1-36 discharge disposition (user table 0112: 01 home, 02 other short-term hospital, 07 left against medical advice, 20 expired).</summary>
    public string? DischargeDisposition { get; init; }
    /// <summary>PV1-45 discharge date/time; defaults to <see cref="Encounter.End"/>.</summary>
    public DateTimeOffset? DischargedAt { get; init; }
    /// <summary>PV2-3 admit reason (CE), coded in ICD-10 (coding system "I10").</summary>
    public ClinicalCode? AdmitReason { get; init; }
    /// <summary>EVN-6 event occurred; defaults to the message time.</summary>
    public DateTimeOffset? EventOccurred { get; init; }
}

public static partial class Hl7V2
{
    /// <summary>
    /// Builds an ADT message for <paramref name="ev"/>: A01 / A04 use structure ADT_A01, A02 ADT_A02, A03 ADT_A03 (HL7 v2.5.1 §3.3.1–§3.3.4).
    /// Segments: MSH, EVN (EVN-1 trigger, EVN-2 recorded, EVN-6 occurred), PID, PV1 and PV2 (when an admit reason is given).
    /// </summary>
    public static IMessage BuildAdt(AdtEvent ev, Patient p, Encounter e, Hl7Endpoint from, Hl7Endpoint to, AdtDetails? details = null,
        string? controlId = null, DateTimeOffset? at = null)
    {
        (IMessage msg, string trigger, string structure) = ev switch
        {
            AdtEvent.Admit => ((IMessage)new ADT_A01(), "A01", "ADT_A01"),
            AdtEvent.Register => (new ADT_A01(), "A04", "ADT_A01"),
            AdtEvent.Transfer => (new ADT_A02(), "A02", "ADT_A02"),
            AdtEvent.Discharge => (new ADT_A03(), "A03", "ADT_A03"),
            _ => throw new ArgumentOutOfRangeException(nameof(ev)),
        };
        var d = details ?? new AdtDetails();
        var t = new Terser(msg);
        var now = at ?? DateTimeOffset.UtcNow;
        Header(t, "ADT", trigger, structure, from, to, controlId ?? Guid.NewGuid().ToString("N")[..20], now);
        t.Set("/EVN-1", trigger);
        t.Set("/EVN-2", Ts(now));
        t.Set("/EVN-6", Ts(d.EventOccurred ?? now));
        Pid(t, "/", p);
        Pv1(t, ev, e, d);
        if (d.AdmitReason is { } reason)
        {
            t.Set("/PV2-3-1", reason.Code);
            t.Set("/PV2-3-2", reason.Display ?? "");
            t.Set("/PV2-3-3", reason.System == CodeSystems.Icd10 ? "I10" : "L");
        }
        return msg;
    }

    /// <summary>ADT^A02 — transfer from <paramref name="prior"/> to <paramref name="location"/> (PV1-6 → PV1-3).</summary>
    public static ADT_A02 BuildTransfer(Patient p, Encounter e, Hl7Location prior, Hl7Location location, Hl7Endpoint from, Hl7Endpoint to,
        AdtDetails? details = null, string? controlId = null, DateTimeOffset? at = null) =>
        (ADT_A02)BuildAdt(AdtEvent.Transfer, p, e, from, to, (details ?? new AdtDetails()) with { PriorLocation = prior, Location = location }, controlId, at);

    /// <summary>ADT^A03 — discharge / end of visit (PV1-45 = <see cref="Encounter.End"/> unless given in <paramref name="details"/>).</summary>
    public static ADT_A03 BuildDischarge(Patient p, Encounter e, Hl7Endpoint from, Hl7Endpoint to, AdtDetails? details = null,
        string? controlId = null, DateTimeOffset? at = null) =>
        (ADT_A03)BuildAdt(AdtEvent.Discharge, p, e, from, to, details, controlId, at);

    /// <summary>ADT^A04 — register an outpatient / emergency visit (structure ADT_A01).</summary>
    public static ADT_A01 BuildRegistration(Patient p, Encounter e, Hl7Endpoint from, Hl7Endpoint to, AdtDetails? details = null,
        string? controlId = null, DateTimeOffset? at = null) =>
        (ADT_A01)BuildAdt(AdtEvent.Register, p, e, from, to, details, controlId, at);

    private static void Pv1(Terser t, AdtEvent ev, Encounter e, AdtDetails d)
    {
        t.Set("/PV1-1", "1");
        // PV1-2 patient class, user table 0004: I inpatient, O outpatient, E emergency.
        t.Set("/PV1-2", e.Class switch { EncounterClass.Inpatient => "I", EncounterClass.Emergency => "E", _ => "O" });
        if ((d.Location ?? (e.Location is { } loc ? new Hl7Location(loc) : null)) is { } l) Location(t, "/PV1-3", l);
        if (d.AdmissionType is { } type) t.Set("/PV1-4", type);
        if (d.PriorLocation is { } prior) Location(t, "/PV1-6", prior);
        string? doctorId = d.AttendingDoctorId ?? e.Practitioner;
        if (doctorId is not null) t.Set("/PV1-7-1", doctorId);
        if (d.AttendingDoctorName is { } dr) t.Set("/PV1-7-2", dr);
        if (d.HospitalService is { } svc) t.Set("/PV1-10", svc);
        if (d.AdmitSource is { } src) t.Set("/PV1-14", src);
        t.Set("/PV1-19-1", e.Id);
        if (d.FinancialClass is { } fc) t.Set("/PV1-20-1", fc);
        t.Set("/PV1-44", Ts(e.Start));
        if (ev == AdtEvent.Discharge)
        {
            if (d.DischargeDisposition is { } disp) t.Set("/PV1-36", disp);
            if ((d.DischargedAt ?? e.End) is { } end) t.Set("/PV1-45", Ts(end));
        }
    }

    private static void Location(Terser t, string field, Hl7Location l)
    {
        t.Set(field + "-1", l.PointOfCare);
        if (l.Room is { } room) t.Set(field + "-2", room);
        if (l.Bed is { } bed) t.Set(field + "-3", bed);
        if (l.Facility is { } fac) t.Set(field + "-4-1", fac);
    }

    /// <summary>Parses an HL7 TS / DTM value (YYYY[MM[DD[HH[MM[SS[.S]]]]]][+/-ZZZZ]) into a <see cref="DateTimeOffset"/>; null when empty or invalid.</summary>
    public static DateTimeOffset? ParseTs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        TimeSpan offset = TimeSpan.Zero;
        int sign = v.IndexOfAny(['+', '-'], 4);
        if (sign > 0)
        {
            string z = v[(sign + 1)..];
            if (z.Length == 4 && int.TryParse(z[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int hh) && int.TryParse(z[2..], NumberStyles.None, CultureInfo.InvariantCulture, out int mm))
                offset = new TimeSpan(hh, mm, 0) * (v[sign] == '-' ? -1 : 1);
            v = v[..sign];
        }
        int dot = v.IndexOf('.');
        if (dot > 0) v = v[..dot];
        string[] formats = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd", "yyyyMM", "yyyy"];
        return DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, offset) : null;
    }
}
