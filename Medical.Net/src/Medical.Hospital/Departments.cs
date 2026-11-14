using System.Globalization;
using Medical.Bpjs.InaCbg;
using Medical.Clinical;
using Medical.Core;
using Medical.Hl7;
using Medical.Rules;
using Medical.Terminology;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

public sealed record PrescriptionLine(string Generic, double DoseMg, double FrequencyPerDay, int Days, string Route = "oral", string? Instructions = null, int? Quantity = null);

public enum ReviewSeverity { Info, Warning, Stop }

public sealed record ReviewFinding(ReviewSeverity Severity, string Kind, Localized Message);

public sealed record PharmacyReview(string PrescriptionId, IReadOnlyList<ReviewFinding> Findings)
{
    public bool Blocking => Findings.Any(f => f.Severity == ReviewSeverity.Stop);
}

public sealed record StockAlert(string Generic, int Quantity, int ReorderLevel, DateOnly? NearestExpiry, bool Low, bool Expiring);

/// <summary>
/// Pharmacy: prescriptions, pharmacist review (interactions from all configured sources, allergy cross-reactivity,
/// renal dosing from the latest creatinine, stock), first-expiry-first-out dispensing and stock alerts.
/// </summary>
public sealed class PharmacyService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null,
    IDrugInteractionSource? interactions = null, DrugKnowledge? knowledge = null, OrganDosing? dosing = null) : HospitalServiceBase(db, clock, bus)
{
    private readonly IDrugInteractionSource _interactions = interactions ?? CompositeInteractionSource.Default();
    private readonly DrugKnowledge _knowledge = knowledge ?? DrugKnowledge.Default;
    private readonly OrganDosing _dosing = dosing ?? OrganDosing.Default;

    public async Task<Prescription> PrescribeAsync(string visitId, IReadOnlyList<PrescriptionLine> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) throw new ArgumentException("A prescription needs at least one item.", nameof(lines));
        var v = await Find<Visit>(visitId, ct);
        var rx = new Prescription { VisitId = v.Id, PatientId = v.PatientId, PrescriberId = v.DoctorId, OrderedAt = Now, State = PrescriptionState.Ordered };
        foreach (var l in lines)
            rx.Items.Add(new PrescriptionItem
            {
                PrescriptionId = rx.Id, Generic = l.Generic.Trim().ToLowerInvariant(), DoseMg = l.DoseMg, FrequencyPerDay = l.FrequencyPerDay, Days = l.Days,
                Route = l.Route, Instructions = l.Instructions, Quantity = l.Quantity ?? (int)Math.Ceiling(l.FrequencyPerDay * l.Days),
            });
        Db.Prescriptions.Add(rx);
        await Db.SaveChangesAsync(ct);
        return rx;
    }

    /// <summary>Pharmacist review. Findings are stored on the prescription; a "Stop" finding blocks dispensing unless overridden.</summary>
    public async Task<PharmacyReview> ReviewAsync(string prescriptionId, CancellationToken ct = default)
    {
        var rx = await Db.Prescriptions.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == prescriptionId, ct) ?? throw new KeyNotFoundException(prescriptionId);
        var patient = await Find<HisPatient>(rx.PatientId, ct);
        var findings = new List<ReviewFinding>();
        var drugs = rx.Items.Select(i => i.Generic).ToList();
        // Drugs already dispensed in this visit chain count too.
        var visitIds = await VisitChainAsync(rx.VisitId, ct);
        var current = await Db.Prescriptions.Where(r => visitIds.Contains(r.VisitId) && r.Id != rx.Id && r.State == PrescriptionState.Dispensed)
            .SelectMany(r => r.Items).Select(i => i.Generic).Distinct().ToListAsync(ct);
        foreach (var f in await _interactions.CheckAsync(drugs.Concat(current).Distinct(), ct))
        {
            if (!drugs.Contains(f.DrugA) && !drugs.Contains(f.DrugB)) continue;
            findings.Add(new(f.Severity == InteractionSeverity.Moderate ? ReviewSeverity.Warning : f.Severity == InteractionSeverity.Contraindicated ? ReviewSeverity.Stop : ReviewSeverity.Warning,
                "interaction", new($"{f.DrugA} + {f.DrugB}: {f.Effect.En} ({f.Source})", $"{f.DrugA} + {f.DrugB}: {f.Effect.Id} ({f.Source})")));
        }
        foreach (var allergy in (patient.Allergies ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var d in drugs)
                if (_knowledge.AllergyConflict(allergy, d) is { } a)
                    findings.Add(new(a.Severity == InteractionSeverity.Moderate ? ReviewSeverity.Warning : ReviewSeverity.Stop, "allergy",
                        new($"{d}: {a.Effect.En}", $"{d}: {a.Effect.Id}")));
        if (await LatestCreatinineAsync(rx.PatientId, ct) is { } cr)
        {
            int age = patient.BirthDate == default ? 50 : new Patient { Id = "", Mrn = "", Name = new("", ""), BirthDate = patient.BirthDate }.AgeAt(DateOnly.FromDateTime(Now));
            double egfr = Calculators.EgfrCkdEpi2021(cr, age, patient.Gender).Egfr;
            foreach (var d in drugs.Distinct())
                foreach (var adv in _dosing.Renal(d, crClMlMin: egfr, egfr: egfr).Where(a => a.Action != DoseAction.NoChange))
                    findings.Add(new(adv.Action is DoseAction.Avoid or DoseAction.Contraindicated ? ReviewSeverity.Stop : ReviewSeverity.Warning, "renal",
                        new($"{d} ({adv.Regimen}), eGFR {egfr:0}: {adv.Advice.En}", $"{d} ({adv.Regimen}), eGFR {egfr:0}: {adv.Advice.Id}")));
        }
        foreach (var item in rx.Items)
        {
            int available = await AvailableAsync(item.Generic, ct);
            if (available < item.Quantity)
                findings.Add(new(ReviewSeverity.Warning, "stock", new($"{item.Generic}: {available} in stock, {item.Quantity} needed.", $"{item.Generic}: stok {available}, dibutuhkan {item.Quantity}.")));
        }
        rx.Review = string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Kind}: {f.Message.En}"));
        rx.State = findings.Any(f => f.Severity == ReviewSeverity.Stop) ? PrescriptionState.Ordered : PrescriptionState.Verified;
        if (rx.State == PrescriptionState.Verified) (rx.VerifiedAt, rx.VerifiedBy) = (Now, Db.Tenant.UserName);
        await Db.SaveChangesAsync(ct);
        return new PharmacyReview(rx.Id, findings);
    }

    /// <summary>Dispenses first-expiry-first-out from unexpired batches and charges the drugs. A blocked review needs an override reason.</summary>
    public async Task DispenseAsync(string prescriptionId, string? overrideReason = null, CancellationToken ct = default)
    {
        var rx = await Db.Prescriptions.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == prescriptionId, ct) ?? throw new KeyNotFoundException(prescriptionId);
        if (rx.State == PrescriptionState.Dispensed) throw new InvalidOperationException("Already dispensed.");
        if (rx.State != PrescriptionState.Verified)
        {
            if (string.IsNullOrWhiteSpace(overrideReason)) throw new InvalidOperationException("The prescription is not verified; review it or give an override reason.");
            rx.Review += $"\n[Override] {Db.Tenant.UserName}: {overrideReason}";
            Db.AuditEvent(AuditAction.Update, "Prescription/override", rx.PatientId, overrideReason);
        }
        var today = DateOnly.FromDateTime(Now);
        foreach (var item in rx.Items)
        {
            int need = item.Quantity;
            var batches = (await Db.Stock.Where(s => s.Generic == item.Generic && s.Quantity > 0).ToListAsync(ct))
                .Where(s => s.Expiry >= today).OrderBy(s => s.Expiry).ToList();
            if (batches.Sum(b => b.Quantity) < need) throw new InvalidOperationException($"Insufficient unexpired stock of {item.Generic}.");
            decimal cost = 0;
            foreach (var b in batches)
            {
                if (need == 0) break;
                int take = Math.Min(need, b.Quantity);
                b.Quantity -= take;
                need -= take;
                cost += take * b.UnitPrice;
            }
            await ChargeAsync(rx.VisitId, "OBAT", $"{item.Generic} × {item.Quantity}", ChargeCategory.Drug, item.Quantity, Math.Round(cost / item.Quantity, 2), 0, ct);
        }
        rx.State = PrescriptionState.Dispensed;
        rx.DispensedAt = Now;
        await Db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StockAlert>> StockAlertsAsync(int expiringWithinDays = 90, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(Now);
        var all = await Db.Stock.ToListAsync(ct);
        return all.GroupBy(s => s.Generic).Select(g =>
        {
            var live = g.Where(s => s.Quantity > 0 && s.Expiry >= today).ToList();
            int qty = live.Sum(s => s.Quantity);
            DateOnly? nearest = live.Count > 0 ? live.Min(s => s.Expiry) : null;
            bool low = qty < g.Max(s => s.ReorderLevel), expiring = nearest is { } n && n <= today.AddDays(expiringWithinDays);
            return new StockAlert(g.Key, qty, g.Max(s => s.ReorderLevel), nearest, low, expiring);
        }).Where(a => a.Low || a.Expiring).OrderBy(a => a.Generic).ToList();
    }

    private async Task<int> AvailableAsync(string generic, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Now);
        return (await Db.Stock.Where(s => s.Generic == generic).ToListAsync(ct)).Where(s => s.Expiry >= today).Sum(s => s.Quantity);
    }

    private async Task<double?> LatestCreatinineAsync(string patientId, CancellationToken ct)
    {
        var r = await Db.LabResults.Join(Db.LabOrders, r => r.LabOrderId, o => o.Id, (r, o) => new { r, o.PatientId })
            .Where(x => x.PatientId == patientId && x.r.Loinc == "2160-0" && x.r.Value != null).Select(x => x.r).ToListAsync(ct);
        return r.OrderByDescending(x => x.ResultedAt).FirstOrDefault()?.Value;
    }

    private async Task<List<string>> VisitChainAsync(string visitId, CancellationToken ct)
    {
        var ids = new List<string> { visitId };
        var v = await Db.Visits.FirstOrDefaultAsync(x => x.Id == visitId, ct);
        while (v?.ParentVisitId is { } parent) { ids.Add(parent); v = await Db.Visits.FirstOrDefaultAsync(x => x.Id == parent, ct); }
        return ids;
    }
}

/// <summary>Laboratory: orders, specimen collection, result entry with reference / critical flags, verification, critical-value read-back and HL7 exchange.</summary>
public sealed class LabService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null, LabInterpreter? interpreter = null, TerminologyService? terms = null)
    : HospitalServiceBase(db, clock, bus)
{
    private readonly LabInterpreter _lab = interpreter ?? new LabInterpreter();
    private readonly TerminologyService _terms = terms ?? TerminologyService.Default;
    public static readonly Hl7Endpoint His = new("MEDICALNET-HIS", "RS");
    public static readonly Hl7Endpoint Lis = new("LIS", "LAB");

    public async Task<LabOrder> OrderAsync(string visitId, IReadOnlyList<string> loincCodes, OrderPriority priority = OrderPriority.Routine, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        long n = await Db.NextAsync($"lab:{Now:yyMMdd}", ct);
        var o = new LabOrder { VisitId = v.Id, PatientId = v.PatientId, PlacerNumber = $"LAB{Now:yyMMdd}{n:D4}", Priority = priority, State = OrderState.Ordered, OrderedAt = Now, OrderedBy = Db.Tenant.UserName };
        foreach (var code in loincCodes.Distinct())
        {
            var concept = _terms.Lookup(CodeSystems.Loinc, code);
            var range = _lab.RangeFor(ClinicalCode.Loinc(code));
            o.Results.Add(new LabResult { LabOrderId = o.Id, Loinc = code, Display = concept?.DisplayFor("id") ?? concept?.Display ?? code, Unit = range?.Unit ?? concept?.Unit, RefLow = range?.Low, RefHigh = range?.High });
            await ChargeAsync(v.Id, $"LAB-{code}", $"Lab {concept?.DisplayFor("id") ?? concept?.Display ?? code}", ChargeCategory.Laboratory, 1, null, 75_000, ct);
        }
        Db.LabOrders.Add(o);
        await Db.SaveChangesAsync(ct);
        return o;
    }

    public async Task<string> CollectAsync(string orderId, CancellationToken ct = default)
    {
        var o = await Find<LabOrder>(orderId, ct);
        o.SpecimenBarcode ??= $"S{o.PlacerNumber[3..]}";
        o.State = OrderState.Collected;
        o.CollectedAt = Now;
        await Db.SaveChangesAsync(ct);
        return o.SpecimenBarcode;
    }

    /// <summary>Enters one result; flags it against the reference interval and raises <see cref="CriticalLabResult"/> for panic values.</summary>
    public async Task<LabResult> EnterResultAsync(string orderId, string loinc, double? value, string? text = null, CancellationToken ct = default)
    {
        var o = await Db.LabOrders.Include(x => x.Results).FirstOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new KeyNotFoundException(orderId);
        if (o.State is OrderState.Ordered) throw new InvalidOperationException("Collect the specimen first.");
        var r = o.Results.FirstOrDefault(x => x.Loinc == loinc) ?? throw new KeyNotFoundException($"Test {loinc} is not on order {o.PlacerNumber}.");
        var patient = await Find<HisPatient>(o.PatientId, ct);
        r.Value = value;
        r.Text = text;
        r.ResultedAt = Now;
        if (value is { } v)
        {
            var interp = _lab.Interpret(new ClinicalObservation { PatientId = o.PatientId, Code = ClinicalCode.Loinc(loinc), Value = v, Unit = r.Unit }, patient.Gender);
            r.Flag = interp.Flag;
            r.Critical = interp.Flag is ResultFlag.CriticalLow or ResultFlag.CriticalHigh;
        }
        if (o.Results.All(x => x.Value is not null || x.Text is not null)) o.State = OrderState.Resulted;
        await Db.SaveChangesAsync(ct);
        if (r.Critical) Publish(new CriticalLabResult(TenantId, Now, r.Id, o.PatientId, r.Display, r.Value, r.Flag));
        return r;
    }

    public async Task VerifyAsync(string orderId, CancellationToken ct = default)
    {
        var o = await Db.LabOrders.Include(x => x.Results).FirstOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new KeyNotFoundException(orderId);
        if (o.State != OrderState.Resulted) throw new InvalidOperationException("All results must be entered before verification.");
        if (o.Results.Any(r => r.Critical && r.CriticalNotifiedAt is null)) throw new InvalidOperationException("Report the critical values to a clinician (read-back) before verifying.");
        (o.State, o.VerifiedAt, o.VerifiedBy) = (OrderState.Verified, Now, Db.Tenant.UserName);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Documents that a critical value was read back to a clinician.</summary>
    public async Task NotifyCriticalAsync(string resultId, string clinician, CancellationToken ct = default)
    {
        var r = await Find<LabResult>(resultId, ct);
        (r.CriticalNotifiedAt, r.CriticalNotifiedTo) = (Now, clinician);
        Db.AuditEvent(AuditAction.Alert, "LabResult/critical", null, $"{r.Display} → {clinician}");
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>The order as HL7 v2 OML^O21 for the laboratory information system.</summary>
    public async Task<string> ToHl7Async(string orderId, CancellationToken ct = default)
    {
        var o = await Db.LabOrders.Include(x => x.Results).FirstOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new KeyNotFoundException(orderId);
        var p = await Find<HisPatient>(o.PatientId, ct);
        var orders = o.Results.Select((r, i) => new ServiceOrder
        {
            PatientId = p.Id, PlacerOrderNumber = $"{o.PlacerNumber}-{i + 1}", Code = ClinicalCode.Loinc(r.Loinc, r.Display), Category = OrderCategory.Laboratory,
            Priority = o.Priority, OrderedAt = new DateTimeOffset(o.OrderedAt, TimeSpan.Zero), Requester = o.OrderedBy, Specimen = "SER",
        }).ToList();
        return Hl7V2.Encode(Hl7V2.BuildLabOrder(HisRecords.ToDomain(p), orders, His, Lis, at: new DateTimeOffset(Now, TimeSpan.Zero)));
    }

    /// <summary>Imports an HL7 ORU^R01 from the LIS, matching the order by placer number (OBR-2) and tests by LOINC.</summary>
    public async Task<int> ImportHl7ResultsAsync(string er7, CancellationToken ct = default)
    {
        var m = Hl7V2.Parse(er7);
        var obr = m.Segments("OBR").FirstOrDefault() ?? throw new FormatException("No OBR segment.");
        string placer = Hl7Message.Field(obr, 2).Split('-')[0];
        var o = await Db.LabOrders.Include(x => x.Results).FirstOrDefaultAsync(x => x.PlacerNumber == placer, ct) ?? throw new KeyNotFoundException($"Order {placer} not found.");
        if (o.State == OrderState.Ordered) await CollectAsync(o.Id, ct);
        int n = 0;
        foreach (var obs in Hl7Mapping.ToObservations(m, o.PatientId).Where(x => x.Code.System == CodeSystems.Loinc))
            if (o.Results.Any(r => r.Loinc == obs.Code.Code)) { await EnterResultAsync(o.Id, obs.Code.Code, obs.Value, obs.ValueText, ct); n++; }
        return n;
    }
}

/// <summary>Radiology: orders with accession numbers, a modality worklist, performed studies (Study Instance UID) and reports.</summary>
public sealed class RadiologyService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null) : HospitalServiceBase(db, clock, bus)
{
    public async Task<RadiologyOrder> OrderAsync(string visitId, string procedureCode, string procedure, string modality, OrderPriority priority = OrderPriority.Routine,
        string? clinicalInfo = null, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        long n = await Db.NextAsync($"rad:{Now:yyMMdd}", ct);
        var o = new RadiologyOrder
        {
            VisitId = v.Id, PatientId = v.PatientId, ProcedureCode = procedureCode, Procedure = procedure, Modality = modality, Priority = priority,
            State = OrderState.Ordered, ClinicalInfo = clinicalInfo, OrderedAt = Now, AccessionNumber = $"RAD{Now:yyMMdd}{n:D4}",
        };
        Db.RadiologyOrders.Add(o);
        await ChargeAsync(v.Id, $"RAD-{procedureCode}", procedure, ChargeCategory.Radiology, 1, null, 200_000, ct);
        await Db.SaveChangesAsync(ct);
        return o;
    }

    public async Task ScheduleAsync(string orderId, DateTime at, CancellationToken ct = default)
    {
        var o = await Find<RadiologyOrder>(orderId, ct);
        (o.ScheduledAt, o.State) = (at, OrderState.Scheduled);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>DICOM modality worklist entries (scheduled or ordered) for a modality.</summary>
    public async Task<IReadOnlyList<(RadiologyOrder Order, HisPatient Patient)>> WorklistAsync(string modality, CancellationToken ct = default)
    {
        var rows = await Db.RadiologyOrders.Where(o => o.Modality == modality && (o.State == OrderState.Ordered || o.State == OrderState.Scheduled))
            .Join(Db.Patients, o => o.PatientId, p => p.Id, (o, p) => new { o, p }).ToListAsync(ct);
        return rows.OrderBy(r => r.o.Priority == OrderPriority.Stat ? 0 : r.o.Priority == OrderPriority.Urgent ? 1 : 2).ThenBy(r => r.o.ScheduledAt ?? r.o.OrderedAt)
            .Select(r => (r.o, r.p)).ToList();
    }

    public async Task PerformedAsync(string orderId, string studyInstanceUid, CancellationToken ct = default)
    {
        var o = await Find<RadiologyOrder>(orderId, ct);
        (o.StudyInstanceUid, o.PerformedAt, o.State) = (studyInstanceUid, Now, OrderState.InProgress);
        await Db.SaveChangesAsync(ct);
    }

    public async Task ReportAsync(string orderId, string findings, string impression, CancellationToken ct = default)
    {
        var o = await Find<RadiologyOrder>(orderId, ct);
        if (o.State != OrderState.InProgress) throw new InvalidOperationException("The study has not been performed.");
        (o.Findings, o.Impression, o.Radiologist, o.ReportedAt, o.State) = (findings, impression, Db.Tenant.UserName, Now, OrderState.Reported);
        await Db.SaveChangesAsync(ct);
    }
}

/// <summary>Invoice preview: charges grouped by category, payer split.</summary>
public sealed record BillSummary(IReadOnlyList<Charge> Charges, IReadOnlyDictionary<ChargeCategory, decimal> ByCategory, decimal Total, PayerType Payer);

/// <summary>Billing: charges across a visit chain, invoices (Umum / BPJS / insurance), payments, INA-CBG claim data and revenue.</summary>
public sealed class BillingService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null) : HospitalServiceBase(db, clock, bus)
{
    public async Task<BillSummary> SummaryAsync(string visitId, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        var ids = await ChainAsync(v, ct);
        var charges = await Db.Charges.Where(c => ids.Contains(c.VisitId) && c.InvoiceId == null).ToListAsync(ct);
        return new BillSummary(charges.OrderBy(c => c.At).ToList(), charges.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount)), charges.Sum(c => c.Amount), v.Payer);
    }

    /// <summary>
    /// Issues the invoice for all uninvoiced charges of the visit chain. Umum: the patient pays everything. BPJS: the
    /// hospital tariff is claimed through INA-CBG (the package rate is decided by the E-Klaim grouper) and the patient
    /// pays only a voluntary room upgrade. Insurance: the insurer is billed.
    /// </summary>
    public async Task<Invoice> IssueAsync(string visitId, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        var s = await SummaryAsync(visitId, ct);
        if (s.Charges.Count == 0) throw new InvalidOperationException("Nothing to invoice.");
        long n = await Db.NextAsync($"inv:{Now:yyyyMM}", ct);
        decimal upgrade = v.Payer == PayerType.Bpjs && v.BedClass == BedClass.Vip ? s.Charges.Where(c => c.Category == ChargeCategory.Room).Sum(c => c.Amount) : 0;
        var inv = new Invoice
        {
            VisitId = v.Id, Number = $"INV/{Now:yyyyMM}/{n:D5}", Payer = v.Payer, Total = s.Total, State = InvoiceState.Issued, IssuedAt = Now,
            PayerAmount = v.Payer == PayerType.Umum ? 0 : s.Total - upgrade,
            PatientAmount = v.Payer == PayerType.Umum ? s.Total : upgrade,
        };
        if (inv.PatientAmount == 0) inv.State = InvoiceState.Paid;
        Db.Invoices.Add(inv);
        foreach (var c in s.Charges) c.InvoiceId = inv.Id;
        await Db.SaveChangesAsync(ct);
        return inv;
    }

    public async Task<Invoice> PayAsync(string invoiceId, decimal amount, string method = "cash", CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var inv = await Find<Invoice>(invoiceId, ct);
        if (inv.State is InvoiceState.Void) throw new InvalidOperationException("Invoice is void.");
        Db.Payments.Add(new Payment { InvoiceId = inv.Id, Amount = amount, Method = method, At = Now, Cashier = Db.Tenant.UserName });
        inv.Paid += amount;
        if (inv.Paid >= inv.PatientAmount) inv.State = InvoiceState.Paid;
        await Db.SaveChangesAsync(ct);
        return inv;
    }

    /// <summary>INA-CBG claim input for a BPJS visit, with the hospital tariff broken down as E-Klaim expects, plus pre-grouping checks.</summary>
    public async Task<(InaCbgClaim Claim, IReadOnlyList<ClaimIssue> Issues)> BpjsClaimAsync(string visitId, string coderNik, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        if (v.Payer != PayerType.Bpjs) throw new InvalidOperationException("Not a BPJS visit.");
        var p = await Find<HisPatient>(v.PatientId, ct);
        var ids = await ChainAsync(v, ct);
        var charges = await Db.Charges.Where(c => ids.Contains(c.VisitId)).ToListAsync(ct);
        decimal Sum(params ChargeCategory[] cats) => charges.Where(c => cats.Contains(c.Category)).Sum(c => c.Amount);
        var tariff = new HospitalTariff
        {
            Konsultasi = Sum(ChargeCategory.Consultation, ChargeCategory.Registration), Keperawatan = Sum(ChargeCategory.Nursing),
            Laboratorium = Sum(ChargeCategory.Laboratory), Radiologi = Sum(ChargeCategory.Radiology), Obat = Sum(ChargeCategory.Drug),
            Kamar = v.BedClass == BedClass.Icu ? 0 : Sum(ChargeCategory.Room), RawatIntensif = v.BedClass == BedClass.Icu ? Sum(ChargeCategory.Room) : 0,
            ProsedurNonBedah = Sum(ChargeCategory.Procedure), Bmhp = Sum(ChargeCategory.Administration),
        };
        var dx = await Db.Diagnoses.Where(d => d.VisitId == v.Id).OrderByDescending(d => d.Primary).Select(d => d.Icd10).ToListAsync(ct);
        var tenant = await Db.Tenants.FirstAsync(t => t.Id == TenantId, ct);
        var claim = new InaCbgClaim
        {
            NomorSep = v.SepNumber ?? "", NomorKartu = p.BpjsNumber ?? "", NomorRm = p.Mrn, NamaPasien = p.FullName.ToUpperInvariant(), TanggalLahir = p.BirthDate, Gender = p.Gender,
            TanggalMasuk = new DateTimeOffset(v.Start, TimeSpan.Zero), TanggalPulang = new DateTimeOffset(v.End ?? Now, TimeSpan.Zero),
            JenisRawat = v.Kind == VisitKind.Inpatient ? CareType.Inpatient : CareType.Outpatient,
            KelasRawat = v.BedClass switch { BedClass.Kelas1 => 1, BedClass.Kelas2 => 2, _ => 3 },
            DischargeStatus = v.Discharge switch { DischargeType.Referred or DischargeType.Transferred => DischargeStatus.Referred, DischargeType.AgainstAdvice => DischargeStatus.OwnRequest, DischargeType.Died => DischargeStatus.Died, _ => DischargeStatus.DoctorApproval },
            Diagnoses = dx, Tariff = tariff, CoderNik = coderNik, KodeTarif = tenant.KodeTarif, IcuDays = v.BedClass == BedClass.Icu && v.End is { } e ? Math.Max(1, (e.Date - v.Start.Date).Days) : 0,
        };
        return (claim, InaCbgClaimBuilder.Validate(claim));
    }

    public async Task<IReadOnlyDictionary<ChargeCategory, decimal>> RevenueAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        (await Db.Charges.Where(c => c.At >= fromUtc && c.At < toUtc).ToListAsync(ct)).GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));

    private async Task<List<string>> ChainAsync(Visit v, CancellationToken ct)
    {
        var ids = new List<string> { v.Id };
        var cur = v;
        while (cur.ParentVisitId is { } parent && await Db.Visits.FirstOrDefaultAsync(x => x.Id == parent, ct) is { } pv) { ids.Add(pv.Id); cur = pv; }
        return ids;
    }
}

/// <summary>Converters between HIS entities and the Medical.Net domain (FHIR export, decision support, BPJS).</summary>
public static class HisRecords
{
    public static Patient ToDomain(HisPatient p) => new()
    {
        Id = p.Id, Mrn = p.Mrn, Name = new HumanName(p.Given, p.Family), Gender = p.Gender, BirthDate = p.BirthDate, Nik = p.Nik, Phone = p.Phone, Address = p.Address, City = p.City,
    };

    /// <summary>The patient's full record (all visits, vitals, labs, diagnoses, dispensed drugs, allergies) as a <see cref="PatientRecord"/>.</summary>
    public static async Task<PatientRecord> ToPatientRecordAsync(HospitalDbContext db, string patientId, CancellationToken ct = default)
    {
        var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == patientId, ct) ?? throw new KeyNotFoundException(patientId);
        var visits = (await db.Visits.Where(v => v.PatientId == patientId).ToListAsync(ct)).OrderByDescending(v => v.Start).ToList();
        var visitIds = visits.Select(v => v.Id).ToList();
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, ct);
        var vitals = (await db.Vitals.Where(v => v.PatientId == patientId && v.Validated).ToListAsync(ct)).OrderBy(v => v.Time).Select(v => v.ToVitalSigns()).ToList();
        var labs = await db.LabOrders.Include(o => o.Results).Where(o => o.PatientId == patientId).ToListAsync(ct);
        var dx = await db.Diagnoses.Where(d => visitIds.Contains(d.VisitId)).ToListAsync(ct);
        var meds = await db.Prescriptions.Include(r => r.Items).Where(r => r.PatientId == patientId && r.State == PrescriptionState.Dispensed).ToListAsync(ct);
        static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
        return new PatientRecord
        {
            Patient = ToDomain(p),
            Encounters = visits.Select(v => new Encounter
            {
                Id = v.Id, PatientId = p.Id, Start = Utc(v.Start), End = v.End is { } e ? Utc(e) : null,
                Class = v.Kind switch { VisitKind.Inpatient => EncounterClass.Inpatient, VisitKind.Emergency => EncounterClass.Emergency, _ => EncounterClass.Ambulatory },
                Status = v.Status is VisitStatus.Done or VisitStatus.Discharged or VisitStatus.Admitted && v.End is not null ? EncounterStatus.Finished : v.Status == VisitStatus.Cancelled ? EncounterStatus.Cancelled : EncounterStatus.InProgress,
                Location = depts.GetValueOrDefault(v.DepartmentId)?.NameId, Practitioner = v.DoctorId,
            }).ToList(),
            Vitals = vitals,
            Observations = labs.SelectMany(o => o.Results.Where(r => r.ResultedAt is not null).Select(r => new ClinicalObservation
            {
                Id = r.Id, PatientId = p.Id, EncounterId = o.VisitId, Code = ClinicalCode.Loinc(r.Loinc, r.Display), Value = r.Value, ValueText = r.Text, Unit = r.Unit,
                ReferenceLow = r.RefLow, ReferenceHigh = r.RefHigh, Flag = r.Flag, Effective = Utc(r.ResultedAt!.Value),
                Status = o.State == OrderState.Verified ? ObservationStatus.Final : ObservationStatus.Preliminary,
            })).ToList(),
            Conditions = dx.GroupBy(d => d.Icd10).Select(g => new Condition { Id = g.First().Id, PatientId = p.Id, Code = ClinicalCode.Icd10(g.Key, g.First().Display) }).ToList(),
            Medications = meds.SelectMany(r => r.Items.Select(i => new MedicationOrder
            {
                Id = i.Id, PatientId = p.Id, Generic = i.Generic, Medication = new ClinicalCode(CodeSystems.RxNorm, i.Generic, i.Generic), DoseMg = i.DoseMg,
                FrequencyPerDay = i.FrequencyPerDay, Route = i.Route, Start = DateOnly.FromDateTime(r.DispensedAt ?? r.OrderedAt), Instructions = i.Instructions,
            })).ToList(),
            Allergies = (p.Allergies ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => new Allergy { PatientId = p.Id, Substance = a.ToLowerInvariant() }).ToList(),
        };
    }
}
