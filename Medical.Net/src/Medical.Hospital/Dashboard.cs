using Medical.Core;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

public sealed record WardOccupancy(string WardId, string Ward, int Beds, int Occupied, int Cleaning, int Free);

public sealed record QueueLength(string DepartmentId, string Clinic, int Waiting, int InConsultation);

public sealed record WatchPatient(string VisitId, string PatientId, string Name, string Bed, int News2, DateTime At);

public sealed record BedTile(string BedId, string Code, BedClass Class, BedStatus Status, string? PatientName, string? Mrn, int? News2, string? MonitorId);

/// <summary>Key indicators of one hospital (tenant).</summary>
public sealed record HospitalDashboard(
    string Tenant, int OutpatientsToday, int EmergencyToday, int Inpatients, int AdmissionsToday, int DischargesToday,
    double BedOccupancyRate, double AverageLengthOfStay, IReadOnlyList<WardOccupancy> Wards, IReadOnlyList<QueueLength> Queues,
    int PendingLab, int CriticalUnacknowledged, int PendingRadiology, int PrescriptionsToReview, decimal RevenueToday,
    IReadOnlyList<WatchPatient> HighNews2);

public sealed class DashboardService(HospitalDbContext db, TimeProvider? clock = null) : HospitalServiceBase(db, clock)
{
    /// <summary>
    /// Census, bed occupancy rate (occupied / available beds, %), average length of stay of patients discharged in the
    /// last 30 days, queues, pending work and patients whose latest NEWS2 is 5 or more.
    /// </summary>
    public async Task<HospitalDashboard> SnapshotAsync(CancellationToken ct = default)
    {
        var today = Now.Date;
        var tenant = await Db.Tenants.FirstAsync(t => t.Id == TenantId, ct);
        var visits = await Db.Visits.Where(v => v.Start >= today || v.Status == VisitStatus.Admitted || (v.End != null && v.End >= today.AddDays(-30))).ToListAsync(ct);
        var beds = await Db.Beds.ToListAsync(ct);
        var depts = await Db.Departments.ToListAsync(ct);
        var wards = depts.Where(d => d.Kind is DepartmentKind.Ward or DepartmentKind.Icu).Select(w =>
        {
            var wb = beds.Where(b => b.WardId == w.Id).ToList();
            return new WardOccupancy(w.Id, w.NameId, wb.Count, wb.Count(b => b.Status == BedStatus.Occupied), wb.Count(b => b.Status == BedStatus.Cleaning), wb.Count(b => b.Status == BedStatus.Free));
        }).ToList();
        int available = beds.Count(b => b.Status != BedStatus.Blocked);
        var discharged = visits.Where(v => v.Kind == VisitKind.Inpatient && v.Status == VisitStatus.Discharged && v.End >= today.AddDays(-30)).ToList();
        var queues = depts.Where(d => d.QueuePrefix is not null).Select(d => new QueueLength(d.Id, d.NameId,
            visits.Count(v => v.DepartmentId == d.Id && v.Start >= today && v.Status == VisitStatus.Waiting),
            visits.Count(v => v.DepartmentId == d.Id && v.Start >= today && v.Status == VisitStatus.InConsultation))).ToList();
        var admitted = visits.Where(v => v.Status == VisitStatus.Admitted && v.Kind == VisitKind.Inpatient).ToList();
        var admittedIds = admitted.Select(a => a.PatientId).ToList();
        var latest = (await Db.Vitals.Where(v => admittedIds.Contains(v.PatientId)).ToListAsync(ct))
            .GroupBy(v => v.PatientId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Time).First());
        var names = await Db.Patients.Where(p => admittedIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Given + " " + p.Family, ct);
        var watch = admitted.Where(a => latest.TryGetValue(a.PatientId, out var l) && l.News2 >= 5)
            .Select(a => new WatchPatient(a.Id, a.PatientId, names.GetValueOrDefault(a.PatientId, "?"), beds.FirstOrDefault(b => b.Id == a.BedId)?.Code ?? "", latest[a.PatientId].News2, latest[a.PatientId].Time))
            .OrderByDescending(w => w.News2).ToList();
        decimal revenue = (await Db.Charges.Where(c => c.At >= today).ToListAsync(ct)).Sum(c => c.Amount);
        return new HospitalDashboard(tenant.Name,
            visits.Count(v => v.Start >= today && v.Kind == VisitKind.Outpatient), visits.Count(v => v.Start >= today && v.Kind == VisitKind.Emergency),
            admitted.Count, visits.Count(v => v.Kind == VisitKind.Inpatient && v.Start >= today), visits.Count(v => v.Kind == VisitKind.Inpatient && v.Status == VisitStatus.Discharged && v.End >= today),
            available == 0 ? 0 : Math.Round(100.0 * beds.Count(b => b.Status == BedStatus.Occupied) / available, 1),
            discharged.Count == 0 ? 0 : Math.Round(discharged.Average(v => Math.Max(1, (v.End!.Value.Date - v.Start.Date).Days)), 1),
            wards, queues,
            await Db.LabOrders.CountAsync(o => o.State == OrderState.Ordered || o.State == OrderState.Collected || o.State == OrderState.Resulted, ct),
            await Db.LabResults.CountAsync(r => r.Critical && r.CriticalNotifiedAt == null, ct),
            await Db.RadiologyOrders.CountAsync(o => o.State != OrderState.Reported && o.State != OrderState.Cancelled, ct),
            await Db.Prescriptions.CountAsync(r => r.State == PrescriptionState.Ordered, ct),
            revenue, watch);
    }

    /// <summary>Bed board of a ward: each bed with its patient and latest NEWS2.</summary>
    public async Task<IReadOnlyList<BedTile>> BedBoardAsync(string wardId, CancellationToken ct = default)
    {
        var beds = await Db.Beds.Where(b => b.WardId == wardId).OrderBy(b => b.Code).ToListAsync(ct);
        var pids = beds.Where(b => b.PatientId != null).Select(b => b.PatientId!).ToList();
        var patients = await Db.Patients.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vitals = (await Db.Vitals.Where(v => pids.Contains(v.PatientId)).ToListAsync(ct)).GroupBy(v => v.PatientId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Time).First().News2);
        return beds.Select(b => new BedTile(b.Id, b.Code, b.Class, b.Status,
            b.PatientId is { } pid && patients.TryGetValue(pid, out var p) ? p.FullName : null, b.PatientId is { } pid2 && patients.TryGetValue(pid2, out var p2) ? p2.Mrn : null,
            b.PatientId is { } pid3 && vitals.TryGetValue(pid3, out var n) ? n : null, b.MonitorId)).ToList();
    }
}
