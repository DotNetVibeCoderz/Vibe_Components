using Medical.Core;
using Medical.His;
using Medical.Hospital;
using Medical.Hospital.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Medical.Tests;

/// <summary>v2.1: nurse-entered ACVPU and supplemental oxygen reach the live NEWS2 of the monitor bound to the patient.</summary>
public class V21RoundsTests
{
    private static async Task<(HisFixture Fx, MonitoringHub Hub, string Patient, string Bed1, string Bed2)> SetupAsync()
    {
        var fx = new HisFixture();
        await using var db = fx.Ctx("t1");
        var icu = new Department { Code = "ICU", Name = "ICU", NameId = "ICU", Kind = DepartmentKind.Ward };
        db.Departments.Add(icu);
        var p = new HisPatient { Mrn = "000001", Given = "Budi", Family = "Santoso", Gender = Gender.Male, BirthDate = new DateOnly(1960, 3, 1) };
        db.Patients.Add(p);
        var b1 = new Bed { WardId = icu.Id, Code = "ICU-01", Class = BedClass.Icu, MonitorId = "T1-MON-01", PatientId = p.Id, Status = BedStatus.Occupied };
        var b2 = new Bed { WardId = icu.Id, Code = "ICU-02", Class = BedClass.Icu, MonitorId = "T1-MON-02" };
        db.Beds.AddRange(b1, b2);
        await db.SaveChangesAsync();
        // The hosted service is never started (no ExecuteAsync, no simulated monitors): bindings come from RebindAsync only.
        var hub = new MonitoringHub(fx.Options, new ConfigurationBuilder().Build(), NullLogger<MonitoringHub>.Instance);
        await hub.RebindAsync("t1");
        return (fx, hub, p.Id, b1.Id, b2.Id);
    }

    // RR 16, SpO2 97 (scale 1), SBP 120, HR 80, T 37.0 → NEWS2 0 on room air and Alert (RCP 2017 chart).
    private static int? FeedNormal(MonitoringHub hub, string device)
    {
        var e = hub.Engine("t1");
        var t = DateTimeOffset.UtcNow;
        BedSnapshot? s = null;
        foreach (var (p, v) in new[] { (VitalParameter.RespiratoryRate, 16.0), (VitalParameter.SpO2, 97), (VitalParameter.SystolicBp, 120), (VitalParameter.HeartRate, 80), (VitalParameter.TemperatureC, 37) })
            s = e.Ingest(new DeviceReading(device, p, v, t));
        return s?.News2?.Score;
    }

    private static int? Score(MonitoringHub hub, string device) => hub.Engine("t1").Snapshots.Single(s => s.DeviceId == device).News2?.Score;

    [Fact]
    public async Task NurseAssessmentFeedsLiveNews2AndSurvivesTransfer()
    {
        var (fx, hub, patient, _, bed2) = await SetupAsync();
        using var _fx = fx;
        Assert.Equal(0, FeedNormal(hub, "T1-MON-01"));
        Assert.Null(hub.NurseAssessment("t1", "T1-MON-01"));             // nothing entered: NEWS2 assumes Alert on room air

        // Supplemental oxygen: +2 (RCP 2017).
        Assert.Equal("T1-MON-01", hub.ApplyNurseAssessment("t1", patient, Consciousness.Alert, onOxygen: true));
        Assert.Equal(2, Score(hub, "T1-MON-01"));
        Assert.Contains(hub.Engine("t1").Snapshots.Single(s => s.DeviceId == "T1-MON-01").News2!.Components, c => c.Points == 2);

        // New confusion, voice, pain or unresponsive: +3 each (ACVPU), on air and on oxygen.
        foreach (var c in new[] { Consciousness.Confusion, Consciousness.Voice, Consciousness.Pain, Consciousness.Unresponsive })
        {
            hub.ApplyNurseAssessment("t1", patient, c, onOxygen: false);
            Assert.Equal(3, Score(hub, "T1-MON-01"));
            hub.ApplyNurseAssessment("t1", patient, c, onOxygen: true);
            Assert.Equal(5, Score(hub, "T1-MON-01"));
        }
        hub.ApplyNurseAssessment("t1", patient, Consciousness.Voice, onOxygen: true);
        Assert.Equal(5, FeedNormal(hub, "T1-MON-01"));                    // later monitor readings keep the manual values
        Assert.Equal(new { C = Consciousness.Voice, O2 = true },
            hub.NurseAssessment("t1", "T1-MON-01") is { } m ? new { C = m.Consciousness, O2 = m.OnOxygen } : null);

        // Transfer to ICU-02: the hub rebinds and re-applies the patient's assessment to the new monitor.
        await using (var db = fx.Ctx("t1"))
        {
            var beds = await db.Beds.ToListAsync();
            var from = beds.Single(b => b.PatientId == patient);
            (from.PatientId, from.Status) = (null, BedStatus.Cleaning);
            var to = beds.Single(b => b.Id == bed2);
            (to.PatientId, to.Status) = (patient, BedStatus.Occupied);
            await db.SaveChangesAsync();
        }
        await hub.RebindAsync("t1");
        Assert.Null(hub.NurseAssessment("t1", "T1-MON-01"));
        Assert.Equal(Consciousness.Voice, hub.NurseAssessment("t1", "T1-MON-02")!.Consciousness);
        Assert.Equal(5, FeedNormal(hub, "T1-MON-02"));
        Assert.Equal("T1-MON-02", hub.ApplyNurseAssessment("t1", patient, Consciousness.Alert, onOxygen: false));
        Assert.Equal(0, Score(hub, "T1-MON-02"));

        // A patient without a monitor: remembered, applied nowhere; other tenants never see it.
        Assert.Null(hub.ApplyNurseAssessment("t1", "no-such-patient", Consciousness.Pain, true));
        Assert.Null(hub.NurseAssessment("t2", "T1-MON-02"));
    }
}
