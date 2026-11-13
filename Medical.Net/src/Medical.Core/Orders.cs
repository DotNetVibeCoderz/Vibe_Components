namespace Medical.Core;

public enum OrderCategory { Laboratory, Radiology, Procedure, Other }

public enum OrderPriority { Routine, Urgent, Stat }

public enum OrderStatus { Requested, Active, Completed, Cancelled }

/// <summary>A diagnostic or procedure order (HL7 v2 ORC/OBR, FHIR ServiceRequest).</summary>
public sealed record ServiceOrder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public string? EncounterId { get; init; }
    /// <summary>Placer order number (ORC-2) assigned by the ordering system.</summary>
    public required string PlacerOrderNumber { get; init; }
    /// <summary>Filler order number (ORC-3) assigned by the performing department.</summary>
    public string? FillerOrderNumber { get; init; }
    public required ClinicalCode Code { get; init; }
    public OrderCategory Category { get; init; } = OrderCategory.Laboratory;
    public OrderPriority Priority { get; init; } = OrderPriority.Routine;
    public OrderStatus Status { get; init; } = OrderStatus.Requested;
    public DateTimeOffset OrderedAt { get; init; }
    public string? Requester { get; init; }
    /// <summary>Specimen type for lab orders (e.g. "BLD", "SER", "UR").</summary>
    public string? Specimen { get; init; }
    public string? ClinicalInfo { get; init; }
}

public enum AppointmentStatus { Proposed, Booked, Arrived, Fulfilled, Cancelled, NoShow }

/// <summary>A scheduled visit (HL7 v2 SIU SCH/AIS/AIL/AIP, FHIR Appointment).</summary>
public sealed record Appointment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public AppointmentStatus Status { get; init; } = AppointmentStatus.Booked;
    public ClinicalCode? ServiceType { get; init; }
    public string? Practitioner { get; init; }
    public string? Location { get; init; }
    public string? Reason { get; init; }
}

public enum DocumentStatus { Preliminary, Final, Amended }

/// <summary>A clinical document such as a discharge summary or radiology report (HL7 v2 MDM TXA/OBX, FHIR DocumentReference).</summary>
public sealed record ClinicalDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public string? EncounterId { get; init; }
    /// <summary>Document type, normally LOINC (e.g. 18842-5 Discharge summary).</summary>
    public required ClinicalCode Type { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset Created { get; init; }
    public DocumentStatus Status { get; init; } = DocumentStatus.Final;
    public required string Text { get; init; }
}
