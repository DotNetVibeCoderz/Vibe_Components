using System.Globalization;
using System.Text.Json.Nodes;
using Medical.Bpjs.InaCbg;
using Medical.Bpjs.VClaim;
using Medical.Core;
using Medical.Terminology;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital.Integration;

/// <summary>A payer / national-platform call failed or is not configured; <see cref="Text"/> is the bilingual message for the UI.</summary>
public sealed class PayerIntegrationException(Localized text, Exception? inner = null) : InvalidOperationException(text.En, inner)
{
    public Localized Text { get; } = text;
}

/// <summary>Referral (rujukan) block of a VClaim SEP.</summary>
/// <param name="AsalRujukan">1 = from a primary-care provider (FKTP), 2 = from a hospital (FKRTL).</param>
/// <param name="NoRujukan">Referral number.</param>
/// <param name="TanggalRujukan">Referral date.</param>
/// <param name="PpkRujukan">BPJS provider code of the referring facility.</param>
public sealed record SepReferral(string AsalRujukan, string NoRujukan, DateOnly TanggalRujukan, string PpkRujukan);

/// <summary>
/// What the registration desk adds to the HIS visit to create a SEP: the hospital's BPJS provider code, the BPJS poli code,
/// the initial ICD-10 diagnosis, the DPJP (doctor in charge, BPJS code) and the referral. Field names follow the VClaim 2.0
/// <c>t_sep</c> object (<c>SEP/2.0/insert</c>, VClaim REST catalogue, BPJS Kesehatan).
/// </summary>
public sealed record SepRequest
{
    /// <summary>Hospital BPJS provider code (<c>ppkPelayanan</c>, 8 characters, e.g. 0301R011).</summary>
    public required string PpkPelayanan { get; init; }
    /// <summary>Initial diagnosis (<c>diagAwal</c>), ICD-10.</summary>
    public required string DiagnosaAwal { get; init; }
    /// <summary>BPJS poli code (<c>poli.tujuan</c>, e.g. INT, UMU, IGD); required for outpatient / emergency SEPs.</summary>
    public string? PoliTujuan { get; init; }
    /// <summary>DPJP code at BPJS (<c>dpjpLayan</c> for outpatient care, <c>skdp.kodeDPJP</c> for a control letter / SPRI).</summary>
    public string? KodeDpjp { get; init; }
    public SepReferral? Rujukan { get; init; }
    /// <summary>Control letter / inpatient order number (<c>skdp.noSurat</c>), required for inpatient SEPs.</summary>
    public string? NoSuratKontrol { get; init; }
    /// <summary>Entitled class (<c>klsRawat.klsRawatHak</c>, 1–3); null looks it up with <c>Peserta/nokartu</c>.</summary>
    public int? KelasHak { get; init; }
    /// <summary>Upgrade class code (<c>klsRawat.klsRawatNaik</c>) when the patient pays for a higher class; empty otherwise.</summary>
    public string KelasNaik { get; init; } = "";
    /// <summary>Visit purpose (<c>tujuanKunj</c>): 0 normal, 1 procedure, 2 consultation.</summary>
    public string TujuanKunjungan { get; init; } = "0";
    public bool PoliEksekutif { get; init; }
    public string? NoTelp { get; init; }
    public string? Catatan { get; init; }
}

/// <summary>Participant eligibility from VClaim <c>Peserta/nokartu</c>.</summary>
/// <param name="Active">True when <c>statusPeserta.kode</c> is 0 (AKTIF).</param>
public sealed record BpjsEligibility(string NoKartu, string Nama, bool Active, string Status, int? KelasHak, string? JenisPeserta, string? Faskes);

/// <summary>Result of <see cref="BpjsWorkflowService.SubmitClaimAsync"/>.</summary>
public sealed record BpjsClaimOutcome(BpjsClaimRecord Record, IReadOnlyList<ClaimIssue> Issues)
{
    public bool Sent => Record.Status == BpjsClaimStatus.Sent;
    public bool Refused => Record.Status == BpjsClaimStatus.ValidationFailed;
}

/// <summary>One row of the selisih report (hospital tariff vs INA-CBG rate).</summary>
public sealed record SelisihRow(string VisitId, string SepNumber, string? CbgCode, string? CbgDescription, decimal HospitalTariff, decimal CbgTariff, BpjsClaimStatus Status)
{
    public decimal Selisih => HospitalTariff - CbgTariff;
}

/// <summary>
/// BPJS (JKN) workflows from the HIS: SEP creation through VClaim and claim submission through E-Klaim (INA-CBG).
/// The claim is built and checked by <see cref="BillingService.BpjsClaimAsync"/>; anything with an Error issue is refused
/// before E-Klaim is called. The E-Klaim sequence follows the E-Klaim web-service manual
/// (<c>new_claim</c> → <c>set_claim_data</c> → <c>grouper</c> stage 1 → <c>claim_final</c> → <c>send_claim_individual</c>);
/// the grouper result (CBG code, description, package rate) is stored in <see cref="BpjsClaimRecord"/> and becomes the
/// payer amount of the visit's invoice, while the hospital tariff is kept for the selisih report.
/// </summary>
public sealed class BpjsWorkflowService(HospitalDbContext db, VClaimClient? vclaim, EKlaimClient? eklaim, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Offset used for the local service date of a SEP (WIB, UTC+7, by default).</summary>
    public TimeSpan LocalOffset { get; init; } = TimeSpan.FromHours(7);

    public bool VClaimConfigured => vclaim is not null;
    public bool EKlaimConfigured => eklaim is not null;

    private static readonly Localized VClaimMissing = new("BPJS VClaim is not configured (His:Bpjs:VClaim).", "BPJS VClaim belum dikonfigurasi (His:Bpjs:VClaim).");
    private static readonly Localized EKlaimMissing = new("E-Klaim is not configured (His:Bpjs:EKlaim).", "E-Klaim belum dikonfigurasi (His:Bpjs:EKlaim).");

    private static bool IsRemoteFailure(Exception e, CancellationToken ct) =>
        e is BpjsApiException or EKlaimException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException
            or System.Security.Cryptography.CryptographicException or FormatException
        || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private async Task<(Visit Visit, HisPatient Patient)> LoadAsync(string visitId, CancellationToken ct)
    {
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == visitId, ct) ?? throw new KeyNotFoundException($"Visit '{visitId}' not found.");
        var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == v.PatientId, ct) ?? throw new KeyNotFoundException($"Patient '{v.PatientId}' not found.");
        return (v, p);
    }

    // ------------------------------------------------------------------ VClaim

    /// <summary>Checks the patient's JKN card on the visit date (<c>Peserta/nokartu/{noKartu}/tglSEP/{date}</c>).</summary>
    public async Task<BpjsEligibility> CheckEligibilityAsync(string visitId, CancellationToken ct = default)
    {
        var client = vclaim ?? throw new PayerIntegrationException(VClaimMissing);
        var (v, p) = await LoadAsync(visitId, ct);
        string card = p.BpjsNumber ?? throw new PayerIntegrationException(new("The patient has no BPJS card number.", "Pasien belum memiliki nomor kartu BPJS."));
        JsonNode node;
        try { node = await client.PesertaByKartuAsync(card, LocalDate(v.Start), ct); }
        catch (Exception e) when (IsRemoteFailure(e, ct))
        {
            throw new PayerIntegrationException(new($"VClaim participant lookup failed: {e.Message}", $"Cek peserta VClaim gagal: {e.Message}"), e);
        }
        return ParseEligibility(node, card);
    }

    internal static BpjsEligibility ParseEligibility(JsonNode node, string card)
    {
        var peserta = node["peserta"] ?? node;
        string statusCode = peserta["statusPeserta"]?["kode"]?.ToString() ?? "";
        int? kelas = int.TryParse(peserta["hakKelas"]?["kode"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : null;
        return new BpjsEligibility(peserta["noKartu"]?.ToString() ?? card, peserta["nama"]?.ToString() ?? "", statusCode == "0",
            peserta["statusPeserta"]?["keterangan"]?.ToString() ?? statusCode, kelas, peserta["jenisPeserta"]?["keterangan"]?.ToString(),
            peserta["provUmum"]?["nmProvider"]?.ToString());
    }

    private DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(utc + LocalOffset);

    /// <summary>
    /// Builds the VClaim 2.0 <c>t_sep</c> from the visit, patient and <paramref name="request"/>; jenis pelayanan is
    /// 1 (rawat inap) for inpatient stays and 2 (rawat jalan) otherwise. When <see cref="SepRequest.KelasHak"/> is not given
    /// the entitled class is read from <c>Peserta/nokartu</c>, which also refuses inactive participants.
    /// </summary>
    public JsonObject BuildSepRequest(Visit visit, HisPatient patient, SepRequest request, int kelasHak, string user)
    {
        bool inpatient = visit.Kind == VisitKind.Inpatient;
        string dpjp = request.KodeDpjp ?? "";
        var r = request.Rujukan;
        return new JsonObject
        {
            ["noKartu"] = patient.BpjsNumber,
            ["tglSep"] = LocalDate(visit.Start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["ppkPelayanan"] = request.PpkPelayanan,
            ["jnsPelayanan"] = inpatient ? "1" : "2",
            ["klsRawat"] = new JsonObject
            {
                ["klsRawatHak"] = kelasHak.ToString(CultureInfo.InvariantCulture), ["klsRawatNaik"] = request.KelasNaik,
                ["pembiayaan"] = request.KelasNaik.Length > 0 ? "1" : "", ["penanggungJawab"] = request.KelasNaik.Length > 0 ? "Pribadi" : "",
            },
            ["noMR"] = patient.Mrn,
            ["rujukan"] = new JsonObject
            {
                ["asalRujukan"] = r?.AsalRujukan ?? "", ["tglRujukan"] = r?.TanggalRujukan.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                ["noRujukan"] = r?.NoRujukan ?? "", ["ppkRujukan"] = r?.PpkRujukan ?? "",
            },
            ["catatan"] = request.Catatan ?? visit.ChiefComplaint ?? "",
            ["diagAwal"] = request.DiagnosaAwal.Trim().ToUpperInvariant(),
            ["poli"] = new JsonObject { ["tujuan"] = inpatient ? "" : request.PoliTujuan ?? "", ["eksekutif"] = request.PoliEksekutif ? "1" : "0" },
            ["cob"] = new JsonObject { ["cob"] = "0" },
            ["katarak"] = new JsonObject { ["katarak"] = "0" },
            ["jaminan"] = new JsonObject
            {
                ["lakaLantas"] = "0", ["noLP"] = "",
                ["penjamin"] = new JsonObject
                {
                    ["tglKejadian"] = "", ["keterangan"] = "",
                    ["suplesi"] = new JsonObject
                    {
                        ["suplesi"] = "0", ["noSepSuplesi"] = "",
                        ["lokasiLaka"] = new JsonObject { ["kdPropinsi"] = "", ["kdKabupaten"] = "", ["kdKecamatan"] = "" },
                    },
                },
            },
            ["tujuanKunj"] = request.TujuanKunjungan,
            ["flagProcedure"] = "", ["kdPenunjang"] = "", ["assesmentPel"] = "",
            ["skdp"] = new JsonObject { ["noSurat"] = request.NoSuratKontrol ?? "", ["kodeDPJP"] = request.NoSuratKontrol is null ? "" : dpjp },
            ["dpjpLayan"] = inpatient ? "" : dpjp,
            ["noTelp"] = request.NoTelp ?? patient.Phone ?? "",
            ["user"] = user,
        };
    }

    /// <summary>Creates the SEP for a BPJS visit through VClaim, stores the SEP number on the visit and audits it.</summary>
    public async Task<string> CreateSepAsync(string visitId, SepRequest request, CancellationToken ct = default)
    {
        var client = vclaim ?? throw new PayerIntegrationException(VClaimMissing);
        var (v, p) = await LoadAsync(visitId, ct);
        if (v.Payer != PayerType.Bpjs) throw new PayerIntegrationException(new("Not a BPJS visit.", "Bukan kunjungan BPJS."));
        if (v.SepNumber is { Length: > 0 } existing)
            throw new PayerIntegrationException(new($"The visit already has SEP {existing}.", $"Kunjungan sudah memiliki SEP {existing}."));
        if (p.BpjsNumber is not { Length: 13 } || !p.BpjsNumber.All(char.IsAsciiDigit))
            throw new PayerIntegrationException(new("The BPJS card number must have 13 digits.", "Nomor kartu BPJS harus 13 digit."));
        if (request.PpkPelayanan.Trim().Length != 8)
            throw new PayerIntegrationException(new("The hospital BPJS provider code (PPK) must have 8 characters.", "Kode PPK rumah sakit harus 8 karakter."));
        if (!CodeValidators.IsIcd10Format(request.DiagnosaAwal.Trim().ToUpperInvariant()))
            throw new PayerIntegrationException(new($"'{request.DiagnosaAwal}' is not an ICD-10 code.", $"'{request.DiagnosaAwal}' bukan kode ICD-10."));
        if (v.Kind != VisitKind.Inpatient && string.IsNullOrWhiteSpace(request.PoliTujuan))
            throw new PayerIntegrationException(new("Outpatient SEPs need the BPJS poli code.", "SEP rawat jalan memerlukan kode poli BPJS."));
        if (v.Kind == VisitKind.Inpatient && string.IsNullOrWhiteSpace(request.NoSuratKontrol))
            throw new PayerIntegrationException(new("Inpatient SEPs need the inpatient order (SPRI) number.", "SEP rawat inap memerlukan nomor SPRI."));

        int kelas;
        if (request.KelasHak is { } k) kelas = k;
        else
        {
            var e = await CheckEligibilityAsync(visitId, ct);
            if (!e.Active) throw new PayerIntegrationException(new($"The JKN participant is not active ({e.Status}).", $"Peserta JKN tidak aktif ({e.Status})."));
            kelas = e.KelasHak ?? 3;
        }
        if (kelas is < 1 or > 3) throw new PayerIntegrationException(new("The entitled class must be 1, 2 or 3.", "Hak kelas harus 1, 2 atau 3."));

        var tSep = BuildSepRequest(v, p, request, kelas, db.Tenant.UserName);
        JsonNode response;
        try { response = await client.InsertSepAsync(tSep, ct); }
        catch (Exception e) when (IsRemoteFailure(e, ct))
        {
            db.AuditEvent(AuditAction.Create, "Visit/SEP", p.Id, $"failed: {e.Message}");
            await db.SaveChangesAsync(ct);
            throw new PayerIntegrationException(new($"VClaim refused the SEP: {e.Message}", $"VClaim menolak SEP: {e.Message}"), e);
        }
        string sep = response["sep"]?["noSep"]?.ToString() ?? response["noSep"]?.ToString()
                     ?? throw new PayerIntegrationException(new("VClaim returned no SEP number.", "VClaim tidak mengembalikan nomor SEP."));
        v.SepNumber = sep;
        db.AuditEvent(AuditAction.Create, "Visit/SEP", p.Id, sep);
        await db.SaveChangesAsync(ct);
        return sep;
    }

    /// <summary>Deletes the visit's SEP at VClaim (<c>SEP/2.0/delete</c>) and clears it on the visit.</summary>
    public async Task DeleteSepAsync(string visitId, CancellationToken ct = default)
    {
        var client = vclaim ?? throw new PayerIntegrationException(VClaimMissing);
        var (v, p) = await LoadAsync(visitId, ct);
        string sep = v.SepNumber ?? throw new PayerIntegrationException(new("The visit has no SEP.", "Kunjungan belum memiliki SEP."));
        BpjsResponse r;
        try { r = await client.DeleteSepAsync(sep, db.Tenant.UserName, ct); }
        catch (Exception e) when (IsRemoteFailure(e, ct))
        {
            throw new PayerIntegrationException(new($"VClaim could not delete the SEP: {e.Message}", $"VClaim gagal menghapus SEP: {e.Message}"), e);
        }
        if (!r.IsSuccess) throw new PayerIntegrationException(new($"VClaim could not delete the SEP: {r.Code} {r.Message}", $"VClaim gagal menghapus SEP: {r.Code} {r.Message}"));
        v.SepNumber = null;
        db.AuditEvent(AuditAction.Delete, "Visit/SEP", p.Id, sep);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ E-Klaim

    /// <summary>The claim record of a visit, if any.</summary>
    public Task<BpjsClaimRecord?> ClaimAsync(string visitId, CancellationToken ct = default) =>
        db.Set<BpjsClaimRecord>().FirstOrDefaultAsync(c => c.VisitId == visitId, ct);

    /// <summary>
    /// Builds and validates the claim, then runs the E-Klaim sequence, resuming after the last completed step on a retry.
    /// Error issues refuse the claim (status <see cref="BpjsClaimStatus.ValidationFailed"/>) without calling E-Klaim; a remote
    /// failure leaves status <see cref="BpjsClaimStatus.Failed"/> with the step and message in <see cref="BpjsClaimRecord.LastError"/>.
    /// </summary>
    public async Task<BpjsClaimOutcome> SubmitClaimAsync(string visitId, string coderNik, CancellationToken ct = default)
    {
        var client = eklaim ?? throw new PayerIntegrationException(EKlaimMissing);
        var (_, p) = await LoadAsync(visitId, ct);
        var (claim, issues) = await new BillingService(db, _clock).BpjsClaimAsync(visitId, coderNik, ct);
        var rec = await ClaimAsync(visitId, ct);
        if (rec is null)
        {
            rec = new BpjsClaimRecord { VisitId = visitId };
            db.Set<BpjsClaimRecord>().Add(rec);
        }
        if (rec.Status == BpjsClaimStatus.Sent)
            throw new PayerIntegrationException(new($"The claim for SEP {rec.SepNumber} was already sent.", $"Klaim SEP {rec.SepNumber} sudah dikirim."));
        if (rec.Completed >= BpjsClaimStatus.Created && rec.SepNumber.Length > 0 && rec.SepNumber != claim.NomorSep)
            throw new PayerIntegrationException(new($"The claim was created for SEP {rec.SepNumber}, but the visit now has {claim.NomorSep}.",
                $"Klaim dibuat untuk SEP {rec.SepNumber}, tetapi kunjungan kini memiliki SEP {claim.NomorSep}."));
        rec.SepNumber = claim.NomorSep;
        rec.CoderNik = coderNik;
        rec.HospitalTariff = claim.Tariff.Total;
        rec.UpdatedAt = Now;

        if (!InaCbgClaimBuilder.IsValid(issues))
        {
            rec.Status = BpjsClaimStatus.ValidationFailed;
            rec.LastError = string.Join("\n", issues.Where(i => i.Severity == ClaimIssueSeverity.Error).Select(i => $"{i.Field}: {i.Message.En}"));
            await db.SaveChangesAsync(ct);
            return new BpjsClaimOutcome(rec, issues);
        }

        rec.Attempts++;
        string step = "";
        try
        {
            if (rec.Completed < BpjsClaimStatus.Created)
            {
                step = "new_claim";
                await client.NewClaimAsync(claim, ct);
                Done(BpjsClaimStatus.Created);
            }
            if (rec.Completed < BpjsClaimStatus.Final)
            {
                step = "set_claim_data";
                await client.SetClaimDataAsync(claim, ct);
                Done(BpjsClaimStatus.DataSet);

                step = "grouper";
                var grouped = await client.GrouperAsync(claim.NomorSep, 1, ct: ct);
                ApplyGrouper(rec, grouped);
                rec.GroupedAt = Now;
                Done(BpjsClaimStatus.Grouped);
                await ApplyCbgToInvoiceAsync(rec, ct);

                step = "claim_final";
                await client.ClaimFinalAsync(claim.NomorSep, coderNik, ct);
                Done(BpjsClaimStatus.Final);
            }
            step = "send_claim_individual";
            await client.SendClaimAsync(claim.NomorSep, ct);
            Done(BpjsClaimStatus.Sent);
            rec.SubmittedAt = Now;
            rec.LastError = null;
            db.AuditEvent(AuditAction.Export, "Claim/E-Klaim", p.Id, $"{claim.NomorSep} {rec.CbgCode} {rec.CbgTariff.ToString("0", CultureInfo.InvariantCulture)}");
        }
        catch (Exception e) when (IsRemoteFailure(e, ct))
        {
            rec.Status = BpjsClaimStatus.Failed;
            rec.LastError = $"{step}: {e.Message}";
            db.AuditEvent(AuditAction.Export, "Claim/E-Klaim", p.Id, $"{claim.NomorSep} failed at {step}");
        }
        rec.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return new BpjsClaimOutcome(rec, issues);

        void Done(BpjsClaimStatus s) => (rec.Status, rec.Completed) = (s, s);
    }

    /// <summary>
    /// Reads the stage-1 grouper response (<c>response.cbg.code / description / tariff</c>, E-Klaim manual). INA-CBG
    /// groups whose code starts with <c>X</c> are grouper error groups (e.g. an unacceptable primary diagnosis), not payable packages.
    /// </summary>
    internal static void ApplyGrouper(BpjsClaimRecord rec, JsonNode grouped)
    {
        rec.RawResponse = grouped.ToJsonString();
        var cbg = grouped["cbg"] ?? grouped["response"]?["cbg"] ?? throw new InvalidDataException("The grouper response has no 'cbg' block.");
        rec.CbgCode = cbg["code"]?.ToString();
        rec.CbgDescription = cbg["description"]?.ToString();
        rec.CbgTariff = decimal.TryParse(cbg["tariff"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var t) ? t : 0;
        if (rec.CbgCode is null or "" || rec.CbgCode.StartsWith('X'))
            throw new InvalidDataException($"The grouper returned error group '{rec.CbgCode}' ({rec.CbgDescription}).");
    }

    /// <summary>Sets the payer amount of the visit's invoice to the INA-CBG rate (call again after issuing a later invoice).</summary>
    public async Task<Invoice?> ApplyCbgToInvoiceAsync(string visitId, CancellationToken ct = default) =>
        await ClaimAsync(visitId, ct) is { CbgCode: not null } rec ? await ApplyCbgToInvoiceAsync(rec, ct) : null;

    private async Task<Invoice?> ApplyCbgToInvoiceAsync(BpjsClaimRecord rec, CancellationToken ct)
    {
        var inv = (await db.Invoices.Where(i => i.VisitId == rec.VisitId && i.State != InvoiceState.Void).ToListAsync(ct)).MaxBy(i => i.IssuedAt);
        if (inv is not null && inv.Payer == PayerType.Bpjs) inv.PayerAmount = rec.CbgTariff;
        await db.SaveChangesAsync(ct);
        return inv;
    }

    /// <summary>Selisih (hospital tariff vs INA-CBG rate) of grouped claims in a period (by grouping time, UTC).</summary>
    public async Task<IReadOnlyList<SelisihRow>> SelisihReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        (await db.Set<BpjsClaimRecord>().Where(c => c.GroupedAt != null && c.GroupedAt >= fromUtc && c.GroupedAt < toUtc).ToListAsync(ct))
        .OrderBy(c => c.GroupedAt)
        .Select(c => new SelisihRow(c.VisitId, c.SepNumber, c.CbgCode, c.CbgDescription, c.HospitalTariff, c.CbgTariff, c.Status)).ToList();
}
