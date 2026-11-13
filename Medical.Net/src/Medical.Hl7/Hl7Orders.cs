using System.Globalization;
using Medical.Core;
using NHapi.Base.Util;
using NHapi.Model.V251.Message;

namespace Medical.Hl7;

/// <summary>Orders (ORM^O01, OML^O21), scheduling (SIU^S12) and documents (MDM^T02) — HL7 v2.5.1.</summary>
public static partial class Hl7V2
{
    private static string Id(Hl7Endpoint _) => Guid.NewGuid().ToString("N")[..20];

    private static string Priority(OrderPriority p) => p switch { OrderPriority.Stat => "S", OrderPriority.Urgent => "A", _ => "R" };

    private static string OrderControlStatus(OrderStatus s) => s switch
    {
        OrderStatus.Active => "IP", OrderStatus.Completed => "CM", OrderStatus.Cancelled => "CA", _ => "SC",
    };

    private static void Pv1(Terser t, string prefix, Encounter? e, bool required = false)
    {
        if (e is null)
        {
            // PV1 is mandatory in some structures (MDM): emit "N" (not applicable) when there is no visit.
            if (required) { t.Set($"{prefix}PV1-1", "1"); t.Set($"{prefix}PV1-2", "N"); }
            return;
        }
        t.Set($"{prefix}PV1-1", "1");
        t.Set($"{prefix}PV1-2", e.Class switch { EncounterClass.Inpatient => "I", EncounterClass.Emergency => "E", _ => "O" });
        if (e.Location is { } loc) t.Set($"{prefix}PV1-3-1", loc);
        t.Set($"{prefix}PV1-19-1", e.Id);
    }

    private static void OrderSegments(Terser t, string orc, string obr, ServiceOrder o, string control, int setId)
    {
        t.Set(orc + "ORC-1", control);
        t.Set(orc + "ORC-2-1", o.PlacerOrderNumber);
        if (o.FillerOrderNumber is { } f) t.Set(orc + "ORC-3-1", f);
        t.Set(orc + "ORC-5", OrderControlStatus(o.Status));
        t.Set(orc + "ORC-9", Ts(o.OrderedAt));
        if (o.Requester is { } rq) t.Set(orc + "ORC-12-2", rq);
        t.Set(obr + "OBR-1", setId.ToString(CultureInfo.InvariantCulture));
        t.Set(obr + "OBR-2-1", o.PlacerOrderNumber);
        if (o.FillerOrderNumber is { } f2) t.Set(obr + "OBR-3-1", f2);
        t.Set(obr + "OBR-4-1", o.Code.Code);
        t.Set(obr + "OBR-4-2", o.Code.Display ?? "");
        t.Set(obr + "OBR-4-3", o.Code.System == CodeSystems.Loinc ? "LN" : o.Code.System == CodeSystems.Snomed ? "SCT" : "L");
        t.Set(obr + "OBR-6", Ts(o.OrderedAt));
        if (o.ClinicalInfo is { } ci) t.Set(obr + "OBR-13", ci);
        if (o.Requester is { } rq2) t.Set(obr + "OBR-16-2", rq2);
        t.Set(obr + "OBR-24", o.Category switch { OrderCategory.Laboratory => "LAB", OrderCategory.Radiology => "RAD", _ => "OTH" });
        t.Set(obr + "OBR-27-6", Priority(o.Priority));
    }

    /// <summary>ORM^O01 general order (one ORC/OBR pair per order). <paramref name="orderControl"/>: NW new, CA cancel, XO change.</summary>
    public static ORM_O01 BuildOrder(Patient p, IReadOnlyList<ServiceOrder> orders, Hl7Endpoint from, Hl7Endpoint to, Encounter? encounter = null,
        string orderControl = "NW", string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new ORM_O01();
        var t = new Terser(msg);
        Header(t, "ORM", "O01", "ORM_O01", from, to, controlId ?? Id(from), at ?? DateTimeOffset.UtcNow);
        Pid(t, "/PATIENT/", p);
        Pv1(t, "/PATIENT/PATIENT_VISIT/", encounter);
        for (int i = 0; i < orders.Count; i++)
            OrderSegments(t, $"/ORDER({i})/", $"/ORDER({i})/ORDER_DETAIL/", orders[i], orderControl, i + 1);
        return msg;
    }

    /// <summary>OML^O21 laboratory order with specimen (SPM) per order.</summary>
    public static OML_O21 BuildLabOrder(Patient p, IReadOnlyList<ServiceOrder> orders, Hl7Endpoint from, Hl7Endpoint to, Encounter? encounter = null,
        string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new OML_O21();
        var t = new Terser(msg);
        Header(t, "OML", "O21", "OML_O21", from, to, controlId ?? Id(from), at ?? DateTimeOffset.UtcNow);
        Pid(t, "/PATIENT/", p);
        Pv1(t, "/PATIENT/PATIENT_VISIT/", encounter);
        for (int i = 0; i < orders.Count; i++)
        {
            var o = orders[i];
            string req = $"/ORDER({i})/OBSERVATION_REQUEST/";
            OrderSegments(t, $"/ORDER({i})/", req, o, "NW", i + 1);
            t.Set(req + "SPECIMEN/SPM-1", (i + 1).ToString(CultureInfo.InvariantCulture));
            t.Set(req + "SPECIMEN/SPM-2-1", $"{o.PlacerOrderNumber}-S");
            t.Set(req + "SPECIMEN/SPM-4-1", o.Specimen ?? "BLD");
            t.Set(req + "SPECIMEN/SPM-4-3", "HL70487");
            t.Set(req + "SPECIMEN/SPM-17-1", Ts(o.OrderedAt));
        }
        return msg;
    }

    /// <summary>SIU^S12 new appointment booking (SCH + AIS service, AIL location, AIP personnel).</summary>
    public static SIU_S12 BuildAppointment(Patient p, Appointment a, Hl7Endpoint from, Hl7Endpoint to, string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new SIU_S12();
        var t = new Terser(msg);
        Header(t, "SIU", "S12", "SIU_S12", from, to, controlId ?? Id(from), at ?? DateTimeOffset.UtcNow);
        int minutes = (int)Math.Round((a.End - a.Start).TotalMinutes);
        t.Set("/SCH-1-1", a.Id);
        t.Set("/SCH-2-1", a.Id);
        if (a.Reason is { } reason) t.Set("/SCH-7-2", reason);
        t.Set("/SCH-9", minutes.ToString(CultureInfo.InvariantCulture));
        t.Set("/SCH-10-1", "min");
        t.Set("/SCH-11-4", Ts(a.Start));
        t.Set("/SCH-11-5", Ts(a.End));
        t.Set("/SCH-25-1", a.Status switch
        {
            AppointmentStatus.Cancelled => "Cancelled", AppointmentStatus.Arrived => "Started", AppointmentStatus.Fulfilled => "Complete",
            AppointmentStatus.NoShow => "Noshow", AppointmentStatus.Proposed => "Pending", _ => "Booked",
        });
        Pid(t, "/PATIENT/", p);
        t.Set("/RESOURCES/RGS-1", "1");
        if (a.ServiceType is { } st)
        {
            t.Set("/RESOURCES/SERVICE/AIS-1", "1");
            t.Set("/RESOURCES/SERVICE/AIS-3-1", st.Code);
            t.Set("/RESOURCES/SERVICE/AIS-3-2", st.Display ?? "");
            t.Set("/RESOURCES/SERVICE/AIS-4", Ts(a.Start));
            t.Set("/RESOURCES/SERVICE/AIS-7", minutes.ToString(CultureInfo.InvariantCulture));
            t.Set("/RESOURCES/SERVICE/AIS-8-1", "min");
        }
        if (a.Location is { } loc)
        {
            t.Set("/RESOURCES/LOCATION_RESOURCE/AIL-1", "1");
            t.Set("/RESOURCES/LOCATION_RESOURCE/AIL-3-1", loc);
        }
        if (a.Practitioner is { } pr)
        {
            t.Set("/RESOURCES/PERSONNEL_RESOURCE/AIP-1", "1");
            t.Set("/RESOURCES/PERSONNEL_RESOURCE/AIP-3-2", pr);
            t.Set("/RESOURCES/PERSONNEL_RESOURCE/AIP-4-1", "ATND");
        }
        return msg;
    }

    /// <summary>MDM^T02 original document notification with content (TXA + one OBX TX line per text line).</summary>
    public static MDM_T02 BuildDocument(Patient p, ClinicalDocument d, Hl7Endpoint from, Hl7Endpoint to, Encounter? encounter = null,
        string? controlId = null, DateTimeOffset? at = null)
    {
        var msg = new MDM_T02();
        var t = new Terser(msg);
        var now = at ?? DateTimeOffset.UtcNow;
        Header(t, "MDM", "T02", "MDM_T02", from, to, controlId ?? Id(from), now);
        t.Set("/EVN-1", "T02");
        t.Set("/EVN-2", Ts(now));
        Pid(t, "/", p);
        Pv1(t, "/", encounter, required: true);
        t.Set("/TXA-1", "1");
        t.Set("/TXA-2-1", d.Type.Code);
        t.Set("/TXA-2-2", d.Type.Display ?? d.Title);
        t.Set("/TXA-2-3", d.Type.System == CodeSystems.Loinc ? "LN" : "L");
        t.Set("/TXA-3", "TX");
        t.Set("/TXA-4", Ts(d.Created));
        if (d.Author is { } au) t.Set("/TXA-9-2", au);
        t.Set("/TXA-12-1", d.Id);
        t.Set("/TXA-17", d.Status switch { DocumentStatus.Preliminary => "PA", _ => "AU" });
        var lines = d.Text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string obx = $"/OBSERVATION({i})/OBX-";
            t.Set(obx + "1", (i + 1).ToString(CultureInfo.InvariantCulture));
            t.Set(obx + "2", "TX");
            t.Set(obx + "3-1", d.Type.Code);
            t.Set(obx + "3-2", d.Title);
            t.Set(obx + "3-3", d.Type.System == CodeSystems.Loinc ? "LN" : "L");
            t.Set(obx + "5", lines[i]);
            t.Set(obx + "11", d.Status == DocumentStatus.Preliminary ? "P" : "F");
        }
        return msg;
    }
}

/// <summary>Version-agnostic extraction of orders, appointments and documents (segment walking, no group names).</summary>
public static class Hl7WorkflowMapping
{
    public static IReadOnlyList<ServiceOrder> ToOrders(Hl7Message m, string patientId)
    {
        var orcs = m.Segments("ORC");
        var obrs = m.Segments("OBR");
        var spms = m.Segments("SPM");
        var list = new List<ServiceOrder>();
        for (int i = 0; i < Math.Max(orcs.Count, obrs.Count); i++)
        {
            var orc = i < orcs.Count ? orcs[i] : null;
            var obr = i < obrs.Count ? obrs[i] : null;
            string F(NHapi.Base.Model.ISegment? s, int f, int c = 1) => s is null ? "" : Hl7Message.Field(s, f, c);
            string placer = F(orc, 2).Length > 0 ? F(orc, 2) : F(obr, 2);
            string sys = F(obr, 4, 3);
            list.Add(new ServiceOrder
            {
                Id = $"ord-{(placer.Length > 0 ? placer : $"{m.ControlId}-{i + 1}")}",
                PatientId = patientId,
                PlacerOrderNumber = placer,
                FillerOrderNumber = NullIfEmpty(F(orc, 3).Length > 0 ? F(orc, 3) : F(obr, 3)),
                Code = new ClinicalCode(sys == "LN" ? CodeSystems.Loinc : sys == "SCT" ? CodeSystems.Snomed : $"urn:hl7v2:{sys}", F(obr, 4), NullIfEmpty(F(obr, 4, 2))),
                Category = F(obr, 24) switch { "RAD" or "CT" or "MR" or "US" => OrderCategory.Radiology, "LAB" or "HM" or "CH" => OrderCategory.Laboratory, "" => OrderCategory.Laboratory, _ => OrderCategory.Other },
                Priority = F(obr, 27, 6) switch { "S" => OrderPriority.Stat, "A" => OrderPriority.Urgent, _ => OrderPriority.Routine },
                Status = F(orc, 1) == "CA" || F(orc, 5) == "CA" ? OrderStatus.Cancelled : F(orc, 5) switch { "IP" => OrderStatus.Active, "CM" => OrderStatus.Completed, _ => OrderStatus.Requested },
                OrderedAt = Hl7Mapping.ParseTs(F(orc, 9)) ?? Hl7Mapping.ParseTs(F(obr, 6)) ?? DateTimeOffset.UtcNow,
                Requester = NullIfEmpty(F(orc, 12, 2).Length > 0 ? F(orc, 12, 2) : F(obr, 16, 2)),
                Specimen = i < spms.Count ? NullIfEmpty(Hl7Message.Field(spms[i], 4)) : null,
                ClinicalInfo = NullIfEmpty(F(obr, 13)),
            });
        }
        return list;
    }

    public static Appointment? ToAppointment(Hl7Message m, string patientId)
    {
        var sch = m.Segments("SCH").FirstOrDefault();
        if (sch is null) return null;
        var ais = m.Segments("AIS").FirstOrDefault();
        var ail = m.Segments("AIL").FirstOrDefault();
        var aip = m.Segments("AIP").FirstOrDefault();
        var start = Hl7Mapping.ParseTs(Hl7Message.Field(sch, 11, 4)) ?? (ais is null ? null : Hl7Mapping.ParseTs(Hl7Message.Field(ais, 4))) ?? DateTimeOffset.UtcNow;
        var end = Hl7Mapping.ParseTs(Hl7Message.Field(sch, 11, 5))
                  ?? start.AddMinutes(int.TryParse(Hl7Message.Field(sch, 9), out var mins) ? mins : 15);
        string id = Hl7Message.Field(sch, 1);
        return new Appointment
        {
            Id = id.Length > 0 ? id : $"appt-{m.ControlId}",
            PatientId = patientId,
            Start = start,
            End = end,
            Status = m.Type switch
            {
                "SIU^S15" => AppointmentStatus.Cancelled,
                "SIU^S26" => AppointmentStatus.NoShow,
                _ => Hl7Message.Field(sch, 25) switch
                {
                    "Cancelled" => AppointmentStatus.Cancelled, "Complete" => AppointmentStatus.Fulfilled, "Started" => AppointmentStatus.Arrived,
                    "Noshow" => AppointmentStatus.NoShow, "Pending" => AppointmentStatus.Proposed, _ => AppointmentStatus.Booked,
                },
            },
            ServiceType = ais is null || Hl7Message.Field(ais, 3).Length == 0 ? null : new ClinicalCode("urn:hl7v2:service", Hl7Message.Field(ais, 3), NullIfEmpty(Hl7Message.Field(ais, 3, 2))),
            Location = ail is null ? null : NullIfEmpty(Hl7Message.Field(ail, 3)),
            Practitioner = aip is null ? null : NullIfEmpty(Hl7Message.Field(aip, 3, 2).Length > 0 ? Hl7Message.Field(aip, 3, 2) : Hl7Message.Field(aip, 3)),
            Reason = NullIfEmpty(Hl7Message.Field(sch, 7, 2).Length > 0 ? Hl7Message.Field(sch, 7, 2) : Hl7Message.Field(sch, 7)),
        };
    }

    public static ClinicalDocument? ToDocument(Hl7Message m, string patientId)
    {
        var txa = m.Segments("TXA").FirstOrDefault();
        if (txa is null) return null;
        var text = string.Join('\n', m.Segments("OBX").Select(o => Hl7Message.Field(o, 5)));
        string sys = Hl7Message.Field(txa, 2, 3);
        string id = Hl7Message.Field(txa, 12);
        return new ClinicalDocument
        {
            Id = id.Length > 0 ? id : $"doc-{m.ControlId}",
            PatientId = patientId,
            EncounterId = m.Segments("PV1").FirstOrDefault() is { } pv1 ? NullIfEmpty(Hl7Message.Field(pv1, 19)) : null,
            Type = new ClinicalCode(sys == "LN" ? CodeSystems.Loinc : $"urn:hl7v2:{sys}", Hl7Message.Field(txa, 2), NullIfEmpty(Hl7Message.Field(txa, 2, 2))),
            Title = NullIfEmpty(Hl7Message.Field(txa, 2, 2)) ?? "Clinical document",
            Author = NullIfEmpty(Hl7Message.Field(txa, 9, 2).Length > 0 ? Hl7Message.Field(txa, 9, 2) : Hl7Message.Field(txa, 9)),
            Created = Hl7Mapping.ParseTs(Hl7Message.Field(txa, 4)) ?? DateTimeOffset.UtcNow,
            Status = Hl7Message.Field(txa, 17) is "PA" or "IP" or "IN" ? DocumentStatus.Preliminary : DocumentStatus.Final,
            Text = text,
        };
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
