namespace Medical.Hospital.Integration;

/// <summary>
/// Progress of a JKN claim through the E-Klaim web service. The order follows the E-Klaim manual's call sequence
/// (<c>new_claim</c> → <c>set_claim_data</c> → <c>grouper</c> → <c>claim_final</c> → <c>send_claim_individual</c>),
/// so a failed submission resumes from the last completed step.
/// </summary>
public enum BpjsClaimStatus { Draft, ValidationFailed, Created, DataSet, Grouped, Final, Sent, Failed }

/// <summary>
/// One INA-CBG claim per BPJS visit (the visit at the end of the chain: an outpatient visit that led to an admission
/// is claimed with the stay). Holds the grouper result next to the hospital tariff, so the selisih (difference between
/// the hospital tariff and the INA-CBG package rate) can be reported.
/// </summary>
public sealed class BpjsClaimRecord : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string SepNumber { get; set; } = "";
    /// <summary>INA-CBG group code from the grouper (e.g. <c>J-4-16-I</c>).</summary>
    public string? CbgCode { get; set; }
    public string? CbgDescription { get; set; }
    /// <summary>INA-CBG package rate (rupiah) returned by the grouper for the hospital's tariff class.</summary>
    public decimal CbgTariff { get; set; }
    /// <summary>Hospital tariff (sum of <c>tarif_rs</c>) at the time of grouping.</summary>
    public decimal HospitalTariff { get; set; }
    /// <summary>Current state (<see cref="BpjsClaimStatus.Failed"/> after an error).</summary>
    public BpjsClaimStatus Status { get; set; } = BpjsClaimStatus.Draft;
    /// <summary>Last step E-Klaim accepted; a retry continues after it.</summary>
    public BpjsClaimStatus Completed { get; set; } = BpjsClaimStatus.Draft;
    public string? CoderNik { get; set; }
    public DateTime? GroupedAt { get; set; }
    /// <summary>When <c>send_claim_individual</c> succeeded.</summary>
    public DateTime? SubmittedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Grouper response JSON (decrypted), kept for audit and for the coder's review.</summary>
    public string? RawResponse { get; set; }

    /// <summary>Hospital tariff minus INA-CBG rate: positive is a loss for the hospital, negative a surplus.</summary>
    public decimal Selisih => HospitalTariff - CbgTariff;
}

public enum SatuSehatSubmissionStatus { Pending, Sent, Failed }

/// <summary>
/// Outbox row for one visit sent to SATUSEHAT as a FHIR transaction bundle. Rows stay <see cref="SatuSehatSubmissionStatus.Failed"/>
/// with <see cref="LastError"/> until <c>SatuSehatPublisher.RetryPendingAsync</c> succeeds or the attempt limit is reached.
/// (Named <c>…Record</c> so it does not clash with <c>Medical.Fhir.SatuSehat.SatuSehatSubmission</c>, the prepared bundle.)
/// </summary>
public sealed class SatuSehatSubmissionRecord : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public SatuSehatSubmissionStatus Status { get; set; } = SatuSehatSubmissionStatus.Pending;
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    /// <summary>SATUSEHAT patient IHS number used for the subject references.</summary>
    public string? PatientIhs { get; set; }
    /// <summary>Server id of the Encounter created by the transaction.</summary>
    public string? EncounterId { get; set; }
    public int ResourceCount { get; set; }
    /// <summary>Workflow warnings (missing KFA codes, no vital signs…), one per line.</summary>
    public string? Warnings { get; set; }
    public string? LastError { get; set; }
    /// <summary>Transaction-response bundle (JSON).</summary>
    public string? RawResponse { get; set; }
}
