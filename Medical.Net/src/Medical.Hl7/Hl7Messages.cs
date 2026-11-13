using System.Globalization;
using Medical.Core;
using NHapi.Base;
using NHapi.Base.Model;
using NHapi.Base.Parser;
using NHapi.Base.Util;
using NHapi.Model.V251.Message;

namespace Medical.Hl7;

/// <summary>Sender/receiver identity written into MSH-3..6.</summary>
public sealed record Hl7Endpoint(string Application, string Facility);

/// <summary>A parsed HL7 v2 message (NHapi <see cref="IMessage"/>) with convenient accessors.</summary>
public sealed class Hl7Message
{
    private readonly Terser _terser;

    internal Hl7Message(IMessage message, string raw)
    {
        Message = message;
        Raw = raw;
        _terser = new Terser(message);
    }

    public IMessage Message { get; }
    public string Raw { get; }
    public string Version => Message.Version;
    /// <summary>MSH-9 as "ADT^A01".</summary>
    public string Type => $"{Get("MSH-9-1")}^{Get("MSH-9-2")}";
    public string ControlId => Get("MSH-10");
    public string SendingApplication => Get("MSH-3");
    public string SendingFacility => Get("MSH-4");

    /// <summary>Terser path, e.g. "PID-5-1" or "/PATIENT_RESULT/ORDER_OBSERVATION/OBR-4-1". Returns "" when empty.</summary>
    public string Get(string path)
    {
        try { return _terser.Get(path.StartsWith('/') ? path : "/." + path) ?? ""; }
        catch (HL7Exception) { return ""; }
    }

    /// <summary>All segments with the given name anywhere in the message tree (e.g. every OBX).</summary>
    public IReadOnlyList<ISegment> Segments(string name)
    {
        var result = new List<ISegment>();
        Walk(Message, name, result);
        return result;
    }

    /// <summary>Value of field/component of a segment (1-based, like HL7).</summary>
    public static string Field(ISegment segment, int field, int component = 1, int repetition = 0, int sub = 1)
    {
        try { return Terser.Get(segment, field, repetition, component, sub) ?? ""; }
        catch (HL7Exception) { return ""; }
    }

    private static void Walk(IStructure s, string name, List<ISegment> into)
    {
        if (s is ISegment seg)
        {
            if (seg.GetStructureName() == name) into.Add(seg);
            return;
        }
        if (s is IGroup g)
            foreach (var child in g.Names)
                foreach (var rep in g.GetAll(child))
                    Walk(rep, name, into);
    }

    public override string ToString() => $"HL7 v{Version} {Type} #{ControlId}";
}

/// <summary>HL7 v2 parsing, construction (2.5.1) and acknowledgement.</summary>
public static partial class Hl7V2
{
    private static readonly PipeParser Parser = new();

    /// <summary>Normalizes line endings to the HL7 segment terminator (\r).</summary>
    public static string Normalize(string er7) => er7.Replace("\r\n", "\r").Replace('\n', '\r').Trim('\r', ' ', '\u000b', '\u001c') + "\r";

    /// <summary>Parses an ER7 ("pipe") message of any supported version (2.3, 2.5.1).</summary>
    public static Hl7Message Parse(string er7)
    {
        var raw = Normalize(er7);
        return new Hl7Message(Parser.Parse(raw), raw);
    }

    public static string Encode(IMessage message) => Parser.Encode(message);

    internal static string Ts(DateTimeOffset t) => t.ToString("yyyyMMddHHmmsszzz", CultureInfo.InvariantCulture).Replace(":", "");

    internal static void Header(Terser t, string code, string trigger, string structure, Hl7Endpoint from, Hl7Endpoint to, string controlId, DateTimeOffset at)
    {
        t.Set("/MSH-1", "|");
        t.Set("/MSH-2", "^~\\&");
        t.Set("/MSH-3-1", from.Application);
        t.Set("/MSH-4-1", from.Facility);
        t.Set("/MSH-5-1", to.Application);
        t.Set("/MSH-6-1", to.Facility);
        t.Set("/MSH-7", Ts(at));
        t.Set("/MSH-9-1", code);
        t.Set("/MSH-9-2", trigger);
        t.Set("/MSH-9-3", structure);
        t.Set("/MSH-10", controlId);
        t.Set("/MSH-11-1", "P");
        t.Set("/MSH-12-1", "2.5.1");
    }

    internal static void Pid(Terser t, string prefix, Patient p)
    {
        t.Set($"{prefix}PID-1", "1");
        t.Set($"{prefix}PID-3(0)-1", p.Mrn);
        t.Set($"{prefix}PID-3(0)-4-1", "MEDNET");
        t.Set($"{prefix}PID-3(0)-5", "MR");
        if (p.Nik is { } nik)
        {
            t.Set($"{prefix}PID-3(1)-1", nik);
            t.Set($"{prefix}PID-3(1)-4-1", "KEMKES");
            t.Set($"{prefix}PID-3(1)-5", "NI");
        }
        t.Set($"{prefix}PID-5-1", p.Name.Family);
        t.Set($"{prefix}PID-5-2", p.Name.Given);
        t.Set($"{prefix}PID-7", p.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        t.Set($"{prefix}PID-8", p.Gender switch { Gender.Male => "M", Gender.Female => "F", Gender.Other => "O", _ => "U" });
        if (p.Address is { } a) t.Set($"{prefix}PID-11-1", a);
        if (p.City is { } c) t.Set($"{prefix}PID-11-3", c);
        if (p.Phone is { } ph) t.Set($"{prefix}PID-13-1", ph);
    }

    /// <summary>Builds an ADT^A01 (admit/visit notification) for a patient and encounter.</summary>
    public static ADT_A01 BuildAdmit(Patient p, Encounter e, Hl7Endpoint from, Hl7Endpoint to, string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new ADT_A01();
        var t = new Terser(msg);
        var now = at ?? DateTimeOffset.UtcNow;
        Header(t, "ADT", "A01", "ADT_A01", from, to, controlId ?? Guid.NewGuid().ToString("N")[..20], now);
        t.Set("/EVN-1", "A01");
        t.Set("/EVN-2", Ts(now));
        Pid(t, "/", p);
        t.Set("/PV1-1", "1");
        t.Set("/PV1-2", e.Class switch { EncounterClass.Inpatient => "I", EncounterClass.Emergency => "E", _ => "O" });
        if (e.Location is { } loc) t.Set("/PV1-3-1", loc);
        if (e.Practitioner is { } dr) t.Set("/PV1-7-2", dr);
        t.Set("/PV1-19-1", e.Id);
        t.Set("/PV1-44", Ts(e.Start));
        return msg;
    }

    /// <summary>Builds an ORU^R01 lab-result message: one OBR with an OBX per observation (LOINC coded, NM values with reference ranges and flags).</summary>
    public static ORU_R01 BuildResults(Patient p, IReadOnlyList<ClinicalObservation> results, Hl7Endpoint from, Hl7Endpoint to,
        string orderId = "ORD-1", string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new ORU_R01();
        var t = new Terser(msg);
        var now = at ?? DateTimeOffset.UtcNow;
        Header(t, "ORU", "R01", "ORU_R01", from, to, controlId ?? Guid.NewGuid().ToString("N")[..20], now);
        Pid(t, "/PATIENT_RESULT/PATIENT/", p);
        const string order = "/PATIENT_RESULT/ORDER_OBSERVATION/";
        t.Set(order + "OBR-1", "1");
        t.Set(order + "OBR-2-1", orderId);
        t.Set(order + "OBR-4-1", "24323-8");
        t.Set(order + "OBR-4-2", "Comprehensive metabolic 2000 panel - Serum or Plasma");
        t.Set(order + "OBR-4-3", "LN");
        t.Set(order + "OBR-7", Ts(results.Count > 0 ? results[0].Effective : now));
        t.Set(order + "OBR-25", "F");
        for (int i = 0; i < results.Count; i++)
        {
            var o = results[i];
            string obx = $"{order}OBSERVATION({i})/OBX-";
            t.Set(obx + "1", (i + 1).ToString(CultureInfo.InvariantCulture));
            t.Set(obx + "2", o.Value is null ? "ST" : "NM");
            t.Set(obx + "3-1", o.Code.Code);
            t.Set(obx + "3-2", o.Code.Display ?? "");
            t.Set(obx + "3-3", o.Code.System == CodeSystems.Loinc ? "LN" : "L");
            t.Set(obx + "5", o.Value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : o.ValueText ?? "");
            t.Set(obx + "6-1", o.Unit ?? "");
            t.Set(obx + "6-3", "UCUM");
            if (o.ReferenceLow is { } lo && o.ReferenceHigh is { } hi)
                t.Set(obx + "7", $"{lo.ToString("0.##", CultureInfo.InvariantCulture)}-{hi.ToString("0.##", CultureInfo.InvariantCulture)}");
            t.Set(obx + "8", o.Flag switch
            {
                ResultFlag.Low => "L", ResultFlag.High => "H", ResultFlag.CriticalLow => "LL", ResultFlag.CriticalHigh => "HH", ResultFlag.Abnormal => "A", _ => "N",
            });
            t.Set(obx + "11", o.Status == ObservationStatus.Preliminary ? "P" : "F");
            t.Set(obx + "14", Ts(o.Effective));
        }
        return msg;
    }

    /// <summary>
    /// Builds an ACK for <paramref name="incoming"/>: AA (accept), AE (error) or AR (reject), with MSA-2 = the original control id
    /// and sender/receiver swapped.
    /// </summary>
    public static string BuildAck(Hl7Message incoming, string code = "AA", string? text = null, DateTimeOffset? at = null)
    {
        var ack = new ACK();
        var t = new Terser(ack);
        Header(t, "ACK", incoming.Get("MSH-9-2"), "ACK",
            new(incoming.Get("MSH-5"), incoming.Get("MSH-6")), new(incoming.SendingApplication, incoming.SendingFacility),
            $"ACK{incoming.ControlId}", at ?? DateTimeOffset.UtcNow);
        t.Set("/MSA-1", code);
        t.Set("/MSA-2", incoming.ControlId);
        if (text is not null) t.Set("/MSA-3", text);
        return Encode(ack);
    }

    /// <summary>A minimal AR acknowledgement for input that could not be parsed at all.</summary>
    public static string BuildReject(string reason, DateTimeOffset? at = null)
    {
        var ack = new ACK();
        var t = new Terser(ack);
        Header(t, "ACK", "", "ACK", new("MEDICAL.NET", "MEDNET"), new("", ""), Guid.NewGuid().ToString("N")[..20], at ?? DateTimeOffset.UtcNow);
        t.Set("/MSA-1", "AR");
        t.Set("/MSA-2", "");
        t.Set("/MSA-3", reason.Length > 80 ? reason[..80] : reason);
        return Encode(ack);
    }
}
