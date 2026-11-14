using System.Net;
using System.Net.Sockets;
using FellowOakDicom;
using Medical.Core;
using Medical.Dicom;
using Medical.His;
using Medical.Hl7;
using Medical.Hospital;
using Medical.Hospital.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NHapi.Base.Parser;
using NHapi.Model.V251.Message;

namespace Medical.Tests;

/// <summary>v2.1 interfaces: DICOM Modality Worklist SCP/SCU, HIS worklist adapter, HL7 ADT feed and the MLLP outbox sender.</summary>
public class V21InterfaceTests
{
    private static readonly DateOnly Day = new(2026, 11, 16);

    internal static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    // ------------------------------------------------------------------ Modality Worklist

    [Fact]
    public async Task WorklistScpMatchesModalityDateAndWildcardName()
    {
        int port = FreePort();
        using var server = ModalityWorklistServer.Start(port, InMemoryWorklistSource.Demo(Day));
        var (echo, _) = await DicomScu.EchoAsync("127.0.0.1", port, calledAe: ModalityWorklistServer.DefaultAeTitle);
        Assert.True(echo);

        Task<IReadOnlyList<WorklistItem>> Q(WorklistQuery q) => ModalityWorklistClient.QueryItemsAsync("127.0.0.1", port, "CT01", ModalityWorklistServer.DefaultAeTitle, q);

        Assert.Equal(5, (await Q(new WorklistQuery())).Count);
        var ct = await Q(WorklistQuery.ForDate(Day, "CT"));
        Assert.Equal(2, ct.Count);
        Assert.All(ct, i => Assert.Equal("CT", i.Modality));
        Assert.Equal(["RAD2611160002", "RAD2611160005"], ct.Select(i => i.AccessionNumber).Order().ToArray());
        Assert.Empty(await Q(WorklistQuery.ForDate(Day.AddDays(1), "CT")));
        Assert.Equal(5, (await Q(new WorklistQuery { DateFrom = Day.AddDays(-3) })).Count);              // "20261113-"
        Assert.Empty(await Q(new WorklistQuery { DateTo = Day.AddDays(-1) }));                          // "-20261115"
        Assert.Equal(2, (await Q(new WorklistQuery { TimeFrom = new(13, 0), TimeTo = new(23, 59) })).Count); // afternoon steps
        var siti = Assert.Single(await Q(new WorklistQuery { PatientName = "rahayu*" }));                // PN wildcard, case-insensitive
        Assert.Equal("Rahayu^Siti", siti.PatientName);
        Assert.Equal(2, (await Q(new WorklistQuery { PatientName = "W*a*^*" })).Count);                 // Wijaya^Andi, Wulandari^Rina
        Assert.Single(await Q(new WorklistQuery { PatientId = "000104" }));
        Assert.Single(await Q(new WorklistQuery { AccessionNumber = "RAD261116000?", ScheduledStationAeTitle = "US01" }));
        Assert.Empty(await Q(new WorklistQuery { Modality = "CT", ScheduledStationAeTitle = "MR01" }));
    }

    [Fact]
    public async Task WorklistResponseCarriesReturnKeysAndIsValidDicom()
    {
        int port = FreePort();
        using var server = ModalityWorklistServer.Start(port, InMemoryWorklistSource.Demo(Day));
        var ds = Assert.Single(await ModalityWorklistClient.QueryAsync("127.0.0.1", port, "MR01", ModalityWorklistServer.DefaultAeTitle,
            new WorklistQuery { Modality = "MR", DateFrom = Day, DateTo = Day }));
        foreach (var tag in new[] { DicomTag.PatientName, DicomTag.PatientID, DicomTag.PatientBirthDate, DicomTag.PatientSex, DicomTag.AccessionNumber,
                     DicomTag.ReferringPhysicianName, DicomTag.StudyInstanceUID, DicomTag.RequestedProcedureID, DicomTag.RequestedProcedureDescription,
                     DicomTag.RequestedProcedurePriority, DicomTag.RequestedProcedureCodeSequence, DicomTag.ScheduledProcedureStepSequence })
            Assert.True(ds.Contains(tag), tag.ToString());
        Assert.Equal("Saputra^Rudi", ds.GetString(DicomTag.PatientName));
        Assert.Equal("19620330", ds.GetString(DicomTag.PatientBirthDate));
        Assert.Equal("HIGH", ds.GetString(DicomTag.RequestedProcedurePriority));
        var code = ds.GetSequence(DicomTag.RequestedProcedureCodeSequence).Items[0];
        Assert.Equal("88.91", code.GetString(DicomTag.CodeValue));
        Assert.Equal("99MEDNET", code.GetString(DicomTag.CodingSchemeDesignator));
        var sps = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];
        Assert.Equal("MR", sps.GetString(DicomTag.Modality));
        Assert.Equal("MR01", sps.GetString(DicomTag.ScheduledStationAETitle));
        Assert.Equal("20261116", sps.GetString(DicomTag.ScheduledProcedureStepStartDate));
        Assert.Equal("134500", sps.GetString(DicomTag.ScheduledProcedureStepStartTime));
        Assert.Equal("SPS0004", sps.GetString(DicomTag.ScheduledProcedureStepID));

        // fo-dicom validation of VRs / value multiplicity, and a Part 10 round trip.
        ds.Validate();
        var file = new DicomFile(ds.Clone().AddOrUpdate(DicomTag.SOPClassUID, DicomUID.ModalityWorklistInformationModelFind).AddOrUpdate(DicomTag.SOPInstanceUID, DicomUID.Generate()));
        using var ms = new MemoryStream();
        await file.SaveAsync(ms);
        ms.Position = 0;
        var back = await DicomFile.OpenAsync(ms);
        Assert.Equal(WorklistItem.FromDataset(ds), WorklistItem.FromDataset(back.Dataset));
    }

    [Fact]
    public async Task ProjectionReturnsOnlyRequestedKeys()
    {
        var full = (await InMemoryWorklistSource.Demo(Day).QueryAsync(new(), default))[1].ToDataset();
        var identifier = new DicomDataset { { DicomTag.PatientName, "" }, { DicomTag.IssuerOfPatientID, "" } };
        identifier.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, new DicomDataset { { DicomTag.Modality, "CT" } }));
        var r = ModalityWorklistScp.Project(full, identifier);
        Assert.Equal("Wijaya^Andi", r.GetString(DicomTag.PatientName));
        Assert.True(r.Contains(DicomTag.IssuerOfPatientID));                       // unknown key returned zero-length
        Assert.Equal("", r.GetString(DicomTag.IssuerOfPatientID));
        Assert.False(r.Contains(DicomTag.AccessionNumber));                         // not requested → not returned
        var sps = r.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];
        Assert.Equal("CT", sps.GetString(DicomTag.Modality));
        Assert.False(sps.Contains(DicomTag.ScheduledProcedureStepStartDate));
    }

    private static async Task<(HisFixture Fx, RadiologyOrder Order, HisPatient Patient)> RadiologySetupAsync()
    {
        var fx = new HisFixture();
        await using var db = fx.Ctx("t1");
        var clinic = new Department { Code = "POLI-UMUM", Name = "General", NameId = "Poli Umum", Kind = DepartmentKind.Outpatient, QueuePrefix = "A" };
        var doctor = new Staff { UserName = "dr.budi", Name = "dr. Budi Santoso", Role = StaffRole.Doctor };
        db.Departments.Add(clinic);
        db.Staff.Add(doctor);
        await db.SaveChangesAsync();
        var p = await new RegistrationService(db, fx.Clock).RegisterAsync(new("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25), "3171012501900001"));
        var v = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum, doctorId: doctor.Id);
        var rad = new RadiologyService(db, fx.Clock);
        var o = await rad.OrderAsync(v.Id, "87.03", "CT kepala", "CT", OrderPriority.Stat);
        await rad.ScheduleAsync(o.Id, new DateTime(2026, 11, 16, 3, 30, 0, DateTimeKind.Utc));
        await rad.OrderAsync(v.Id, "87.44", "Foto toraks PA", "CR");
        return (fx, o, p);
    }

    [Fact]
    public async Task HisWorklistServesTenantByCalledAeAndAssignsStudyUid()
    {
        var (fx, order, patient) = await RadiologySetupAsync();
        using var _ = fx;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["His:Worklist:Port"] = "0", ["His:Worklist:TimeZone"] = "UTC", ["His:Worklist:MWL-T1"] = "t1", ["His:Worklist:MWL-T2"] = "t2",
            ["His:Worklist:Stations:CT"] = "CT01",
        }).Build();
        var source = new HisWorklistSource(fx.Options, config);
        int port = FreePort();
        using var server = ModalityWorklistServer.Start(port, source);

        var items = await ModalityWorklistClient.QueryItemsAsync("127.0.0.1", port, "CT01", "MWL-T1", new WorklistQuery { Modality = "CT", ScheduledStationAeTitle = "CT01" });
        var item = Assert.Single(items);
        Assert.Equal(order.AccessionNumber, item.AccessionNumber);
        Assert.Equal(patient.Mrn, item.PatientId);
        Assert.Equal("Rahayu^Siti", item.PatientName);
        Assert.Equal("STAT", item.RequestedProcedurePriority);
        Assert.Equal("dr. Budi Santoso", item.ReferringPhysician);
        Assert.Equal(new DateTime(2026, 11, 16, 3, 30, 0), item.ScheduledStart);
        Assert.NotNull(item.StudyInstanceUid);
        Assert.True(DicomUID.IsValidUid(item.StudyInstanceUid!));
        await using (var db = fx.Ctx("t1"))
            Assert.Equal(item.StudyInstanceUid, (await db.RadiologyOrders.SingleAsync(o => o.Id == order.Id)).StudyInstanceUid);
        // The UID is stable across queries; the CR order (no modality key → all modalities) is listed too.
        var all = await ModalityWorklistClient.QueryItemsAsync("127.0.0.1", port, "ANY", "MWL-T1", new WorklistQuery());
        Assert.Equal(2, all.Count);
        Assert.Equal(item.StudyInstanceUid, all.Single(i => i.Modality == "CT").StudyInstanceUid);
        Assert.Null(all.Single(i => i.Modality == "CR").ScheduledStationAeTitle);
        // Another tenant's AE sees nothing; an unmapped AE sees nothing; the default AE serves the first tenant.
        Assert.Empty(await ModalityWorklistClient.QueryAsync("127.0.0.1", port, "CT01", "MWL-T2", new WorklistQuery()));
        Assert.Empty(await ModalityWorklistClient.QueryAsync("127.0.0.1", port, "CT01", "OTHER-AE", new WorklistQuery()));
        Assert.Equal(2, (await ModalityWorklistClient.QueryAsync("127.0.0.1", port, "CT01", ModalityWorklistServer.DefaultAeTitle, new WorklistQuery())).Count);
    }

    // ------------------------------------------------------------------ HL7 ADT feed

    private static async Task<(HisFixture Fx, Department Clinic, Department Ward, Bed[] Beds, Staff Doctor)> WardSetupAsync()
    {
        var fx = new HisFixture();
        await using var db = fx.Ctx("t1");
        var clinic = new Department { Code = "POLI-UMUM", Name = "General", NameId = "Poli Umum", Kind = DepartmentKind.Outpatient, QueuePrefix = "A" };
        var ward = new Department { Code = "MELATI", Name = "Melati", NameId = "Bangsal Melati", Kind = DepartmentKind.Ward };
        db.Departments.AddRange(clinic, ward);
        var beds = new[] { new Bed { WardId = ward.Id, Code = "MEL-01" }, new Bed { WardId = ward.Id, Code = "MEL-02" } };
        db.Beds.AddRange(beds);
        var doctor = new Staff { UserName = "dr.budi", Name = "dr. Budi Santoso", Role = StaffRole.Doctor, Licence = "SIP-123" };
        db.Staff.Add(doctor);
        await db.SaveChangesAsync();
        return (fx, clinic, ward, beds, doctor);
    }

    [Fact]
    public async Task AdtFeedBuildsA04A01A02A03FromHospitalEvents()
    {
        var (fx, clinic, ward, beds, doctor) = await WardSetupAsync();
        using var _ = fx;
        var bus = new InMemoryMedicalEventBus();
        await using var feed = new AdtFeed(fx.Options, bus, new AdtFeedOptions { Receiver = new("ADT-RECV", "RIS") }, fx.Clock);
        var failures = new List<Exception>();
        feed.Failed += (_, e) => failures.Add(e);
        feed.Start();

        await using var db = fx.Ctx("t1");
        var reg = new RegistrationService(db, fx.Clock, bus);
        var clin = new ClinicalService(db, fx.Clock, bus);
        var p = await reg.RegisterAsync(new("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25), "3171012501900001"));
        var visit = await reg.StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum, doctorId: doctor.Id);
        await feed.WhenIdleAsync();
        await clin.AddDiagnosisAsync(visit.Id, "J18.9");
        var stay = await clin.AdmitAsync(visit.Id, ward.Id, bedId: beds[0].Id);
        await feed.WhenIdleAsync();
        fx.Clock.Advance(TimeSpan.FromHours(5));
        await clin.TransferAsync(stay.Id, beds[1].Id);
        await AdtFeed.TransferAsync(db, stay.Id, beds[0].Id, "MEL-02", new AdtFeedOptions { Receiver = new("ADT-RECV", "RIS") }, fx.Clock);
        fx.Clock.Advance(TimeSpan.FromDays(2));
        await clin.DischargeAsync(stay.Id, DischargeType.Home);
        await feed.WhenIdleAsync();
        Assert.Empty(failures);

        var outbox = (await db.Set<Hl7OutboxMessage>().ToListAsync()).OrderBy(m => m.CreatedAt).ToList();
        Assert.Equal(["ADT^A04", "ADT^A01", "ADT^A02", "ADT^A03"], outbox.Select(m => m.MessageType));
        Assert.All(outbox, m => Assert.Equal(OutboxStatus.Pending, m.Status));
        Assert.All(outbox, m => Assert.Equal("ADT", m.Destination));
        Assert.Equal(outbox.Count, outbox.Select(m => m.ControlId).Distinct().Count());

        var parser = new PipeParser();
        var a04 = Hl7V2.Parse(outbox[0].Payload);
        Assert.IsType<ADT_A01>(parser.Parse(outbox[0].Payload));
        Assert.Equal(p.Mrn, a04.Get("PID-3-1"));
        Assert.Equal("Rahayu", a04.Get("PID-5-1"));
        Assert.Equal("Siti", a04.Get("PID-5-2"));
        Assert.Equal("19900125", a04.Get("PID-7"));
        Assert.Equal("F", a04.Get("PID-8"));
        Assert.Equal("O", a04.Get("PV1-2"));
        Assert.Equal("POLI-UMUM", a04.Get("PV1-3-1"));
        Assert.Equal("SIP-123", a04.Get("PV1-7-1"));
        Assert.Equal("UMUM", a04.Get("PV1-20-1"));
        Assert.Equal(visit.Id, a04.Get("PV1-19-1"));
        Assert.Equal("ADT-RECV", a04.Get("MSH-5"));
        Assert.Equal("T1", a04.Get("MSH-4"));

        var a01 = Hl7V2.Parse(outbox[1].Payload);
        Assert.IsType<ADT_A01>(parser.Parse(outbox[1].Payload));
        Assert.Equal("A01", a01.Get("EVN-1"));
        Assert.Equal("I", a01.Get("PV1-2"));
        Assert.Equal("MELATI", a01.Get("PV1-3-1"));
        Assert.Equal("MEL-01", a01.Get("PV1-3-3"));
        Assert.Equal("T1", a01.Get("PV1-3-4"));
        Assert.Equal(stay.Id, a01.Get("PV1-19-1"));
        Assert.Equal("J18.9", a01.Get("PV2-3-1"));
        Assert.Equal("I10", a01.Get("PV2-3-3"));
        Assert.Equal("202611160200", a01.Get("PV1-44")[..12]);
        Assert.Equal(p.Mrn, Hl7Mapping.ToPatient(a01).Mrn);

        var a02 = Hl7V2.Parse(outbox[2].Payload);
        Assert.IsType<ADT_A02>(parser.Parse(outbox[2].Payload));
        Assert.Equal("MEL-02", a02.Get("PV1-3-3"));
        Assert.Equal("MEL-01", a02.Get("PV1-6-3"));
        Assert.Equal("MELATI", a02.Get("PV1-6-1"));

        var a03 = Hl7V2.Parse(outbox[3].Payload);
        var typed = Assert.IsType<ADT_A03>(parser.Parse(outbox[3].Payload));
        Assert.Equal("A03", typed.EVN.EventTypeCode.Value);
        Assert.Equal("01", a03.Get("PV1-36"));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero), Hl7V2.ParseTs(a03.Get("PV1-45")));
        Assert.Equal(fx.Clock.GetUtcNow(), Hl7V2.ParseTs(a03.Get("EVN-2")));
    }

    [Fact]
    public void AdtBuildersProduceConformantStructures()
    {
        var rec = SyntheticData.Record(4);
        var e = rec.Encounters[0] with { End = rec.Encounters[0].Start.AddDays(3), Class = EncounterClass.Inpatient };
        var from = new Hl7Endpoint("HIS", "RS");
        var to = new Hl7Endpoint("RIS", "RS");
        var a03 = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildDischarge(rec.Patient, e, from, to, new AdtDetails { DischargeDisposition = "20" }, "C3")));
        Assert.Equal("ADT^A03", a03.Type);
        Assert.Equal("ADT_A03", a03.Get("MSH-9-3"));
        Assert.Equal("20", a03.Get("PV1-36"));
        Assert.Equal(e.End, Hl7V2.ParseTs(a03.Get("PV1-45")));
        var a02 = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildTransfer(rec.Patient, e, new("ICU", "1", "ICU-03"), new("MELATI", null, "MEL-07"), from, to, controlId: "C2")));
        Assert.Equal("ICU", a02.Get("PV1-6-1"));
        Assert.Equal("1", a02.Get("PV1-6-2"));
        Assert.Equal("MEL-07", a02.Get("PV1-3-3"));
        var a04 = Hl7V2.Parse(Hl7V2.Encode(Hl7V2.BuildRegistration(rec.Patient, e with { Class = EncounterClass.Emergency }, from, to, controlId: "C4")));
        Assert.Equal("ADT^A04", a04.Type);
        Assert.Equal("ADT_A01", a04.Get("MSH-9-3"));
        Assert.Equal("E", a04.Get("PV1-2"));
        Assert.Equal(rec.Patient.Name, Hl7Mapping.ToPatient(a04).Name);
    }

    // ------------------------------------------------------------------ outbox sender

    private static async Task<List<Hl7OutboxMessage>> EnqueueAsync(HisFixture fx, int n)
    {
        var rec = SyntheticData.Record(2);
        await using var db = fx.Ctx("t1");
        var list = new List<Hl7OutboxMessage>();
        for (int i = 0; i < n; i++)
        {
            var er7 = Hl7V2.Encode(Hl7V2.BuildRegistration(rec.Patient, rec.Encounters[0], new("HIS", "T1"), new("RIS", "RS"), controlId: $"MSG{i:D4}", at: fx.Clock.GetUtcNow()));
            list.Add(Hl7OutboxMessage.Enqueue(db, er7, "ADT", fx.Clock.GetUtcNow().UtcDateTime.AddSeconds(i)));
        }
        await db.SaveChangesAsync();
        return list;
    }

    [Fact]
    public async Task OutboxSenderDeliversInOrderAndRetriesNegativeAck()
    {
        using var fx = new HisFixture();
        await EnqueueAsync(fx, 3);
        var received = new List<string>();
        int rejectFirst = 1;
        await using var server = new MllpServer(new IPEndPoint(IPAddress.Loopback, 0), (m, _) =>
        {
            lock (received) received.Add(m.ControlId);
            return ValueTask.FromResult(Interlocked.Decrement(ref rejectFirst) >= 0 ? Hl7V2.BuildAck(m, "AE", "Receiver busy") : Hl7V2.BuildAck(m));
        });
        server.Start();
        var sender = new Hl7OutboxSender(fx.Options, new Hl7OutboxSenderOptions
        {
            Destinations = new Dictionary<string, (string, int)> { ["ADT"] = ("127.0.0.1", server.Endpoint.Port) }, BaseDelay = TimeSpan.FromSeconds(10),
        }, fx.Clock);

        var r1 = await sender.SendPendingAsync();
        Assert.Equal(new OutboxPassResult(0, 1, 0), r1);                 // first rejected; the rest wait behind it
        Assert.Equal(["MSG0000"], received);
        await using (var db = fx.Ctx("t1"))
        {
            var first = await db.Set<Hl7OutboxMessage>().SingleAsync(m => m.ControlId == "MSG0000");
            Assert.Equal(1, first.Attempts);
            Assert.Contains("AE: Receiver busy", first.LastError);
            Assert.Equal(fx.Clock.GetUtcNow().UtcDateTime.AddSeconds(10), first.NextAttemptAt);
        }
        Assert.Equal(new OutboxPassResult(0, 0, 0), await sender.SendPendingAsync());   // still backing off
        fx.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(new OutboxPassResult(3, 0, 0), await sender.SendPendingAsync());
        Assert.Equal(["MSG0000", "MSG0000", "MSG0001", "MSG0002"], received);
        await using (var db = fx.Ctx("t1"))
        {
            var all = await db.Set<Hl7OutboxMessage>().ToListAsync();
            Assert.All(all, m => Assert.Equal(OutboxStatus.Sent, m.Status));
            Assert.All(all, m => Assert.NotNull(m.SentAt));
        }
        await using (var other = fx.Ctx("t2")) Assert.Empty(await other.Set<Hl7OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task OutboxSenderBacksOffOnConnectionFailureAndGivesUp()
    {
        using var fx = new HisFixture();
        await EnqueueAsync(fx, 1);
        var sender = new Hl7OutboxSender(fx.Options, new Hl7OutboxSenderOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromSeconds(8) }, fx.Clock);
        Assert.Equal(TimeSpan.FromSeconds(5), sender.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(8), sender.Backoff(2));        // capped
        // Destination "ADT" is not mapped and is not host:port → error; then point it at a closed port.
        Assert.Equal(new OutboxPassResult(0, 1, 0), await sender.SendPendingAsync());
        fx.Clock.Advance(TimeSpan.FromSeconds(6));
        var closed = new Hl7OutboxSender(fx.Options, new Hl7OutboxSenderOptions
        {
            MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(5), MaxDelay = TimeSpan.FromSeconds(8),
            Destinations = new Dictionary<string, (string, int)> { ["ADT"] = ("127.0.0.1", FreePort()) },
        }, fx.Clock);
        Assert.Equal(new OutboxPassResult(0, 1, 0), await closed.SendPendingAsync());
        fx.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(new OutboxPassResult(0, 0, 1), await closed.SendPendingAsync());
        await using var db = fx.Ctx("t1");
        var m = await db.Set<Hl7OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.Failed, m.Status);
        Assert.Equal(3, m.Attempts);
        Assert.Contains("Exception", m.LastError);
    }
}
