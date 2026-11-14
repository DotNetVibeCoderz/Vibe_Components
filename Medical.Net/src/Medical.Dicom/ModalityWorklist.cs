using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;

namespace Medical.Dicom;

/// <summary>A coded procedure (DICOM code sequence item: Code Value, Coding Scheme Designator, Code Meaning — PS3.3 §8.8).</summary>
public sealed record WorklistCode(string Value, string Scheme, string Meaning);

/// <summary>
/// One scheduled procedure step offered on the Modality Worklist (DICOM PS3.4 Annex K, attributes of PS3.4 Table K.6-1).
/// Dates and times are the facility's local time, as DICOM DA / TM carry no time zone.
/// </summary>
public sealed record WorklistItem
{
    /// <summary>Patient's Name (0010,0010) in DICOM PN form, "Family^Given".</summary>
    public required string PatientName { get; init; }
    /// <summary>Patient ID (0010,0020), the medical record number.</summary>
    public required string PatientId { get; init; }
    public DateOnly? PatientBirthDate { get; init; }
    /// <summary>Patient's Sex (0010,0040): M, F or O.</summary>
    public string? PatientSex { get; init; }
    /// <summary>Accession Number (0008,0050), max 16 characters.</summary>
    public required string AccessionNumber { get; init; }
    /// <summary>Requested Procedure ID (0040,1001).</summary>
    public string? RequestedProcedureId { get; init; }
    /// <summary>Requested Procedure Description (0032,1060).</summary>
    public string? RequestedProcedureDescription { get; init; }
    /// <summary>Requested Procedure Code Sequence (0032,1064).</summary>
    public WorklistCode? RequestedProcedureCode { get; init; }
    /// <summary>Requested Procedure Priority (0040,1003): STAT, HIGH, ROUTINE, MEDIUM or LOW.</summary>
    public string? RequestedProcedurePriority { get; init; }
    /// <summary>Scheduled Station AE Title (0040,0001) of the modality that should perform the step.</summary>
    public string? ScheduledStationAeTitle { get; init; }
    /// <summary>Modality (0008,0060), e.g. CT, MR, CR, US.</summary>
    public required string Modality { get; init; }
    /// <summary>Scheduled Procedure Step Start Date (0040,0002) and Time (0040,0003), local time.</summary>
    public DateTime ScheduledStart { get; init; }
    /// <summary>Scheduled Procedure Step ID (0040,0009).</summary>
    public string? ScheduledProcedureStepId { get; init; }
    /// <summary>Scheduled Procedure Step Description (0040,0007).</summary>
    public string? ScheduledProcedureStepDescription { get; init; }
    /// <summary>Scheduled Performing Physician's Name (0040,0006).</summary>
    public string? ScheduledPerformingPhysician { get; init; }
    /// <summary>Referring Physician's Name (0008,0090).</summary>
    public string? ReferringPhysician { get; init; }
    /// <summary>Study Instance UID (0020,000D) the modality must use for the acquired images.</summary>
    public string? StudyInstanceUid { get; init; }

    /// <summary>The full worklist dataset of this item (before projection onto the requested return keys).</summary>
    public DicomDataset ToDataset()
    {
        var ds = new DicomDataset();
        if (!IsAscii(this)) ds.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
        ds.Add(DicomTag.AccessionNumber, Clip(AccessionNumber, 16));
        Put(ds, DicomTag.ReferringPhysicianName, ReferringPhysician, 64);
        ds.Add(DicomTag.PatientName, PatientName);
        ds.Add(DicomTag.PatientID, Clip(PatientId, 64));
        Put(ds, DicomTag.PatientBirthDate, PatientBirthDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), 8);
        Put(ds, DicomTag.PatientSex, PatientSex, 16);
        Put(ds, DicomTag.StudyInstanceUID, StudyInstanceUid, 64);
        Put(ds, DicomTag.RequestedProcedureDescription, RequestedProcedureDescription, 64);
        ds.Add(new DicomSequence(DicomTag.RequestedProcedureCodeSequence, RequestedProcedureCode is { } c ? [CodeItem(c)] : []));
        Put(ds, DicomTag.RequestedProcedureID, RequestedProcedureId, 16);
        Put(ds, DicomTag.RequestedProcedurePriority, RequestedProcedurePriority, 16);
        var sps = new DicomDataset();
        sps.Add(DicomTag.Modality, Clip(Modality, 16));
        Put(sps, DicomTag.ScheduledStationAETitle, ScheduledStationAeTitle, 16);
        sps.Add(DicomTag.ScheduledProcedureStepStartDate, ScheduledStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        sps.Add(DicomTag.ScheduledProcedureStepStartTime, ScheduledStart.ToString("HHmmss", CultureInfo.InvariantCulture));
        Put(sps, DicomTag.ScheduledPerformingPhysicianName, ScheduledPerformingPhysician, 64);
        Put(sps, DicomTag.ScheduledProcedureStepDescription, ScheduledProcedureStepDescription, 64);
        Put(sps, DicomTag.ScheduledProcedureStepID, ScheduledProcedureStepId, 16);
        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    /// <summary>Reads a worklist response dataset back into an item (missing attributes stay null).</summary>
    public static WorklistItem FromDataset(DicomDataset ds)
    {
        var sps = ds.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var seq) && seq.Items.Count > 0 ? seq.Items[0] : new DicomDataset();
        string? date = Str(sps, DicomTag.ScheduledProcedureStepStartDate), time = Str(sps, DicomTag.ScheduledProcedureStepStartTime);
        DateTime start = DateTime.MinValue;
        if (date is { Length: 8 } && DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            start = d + (WorklistQuery.ParseTime(time) ?? TimeOnly.MinValue).ToTimeSpan();
        WorklistCode? code = ds.TryGetSequence(DicomTag.RequestedProcedureCodeSequence, out var cs) && cs.Items.Count > 0
            ? new(Str(cs.Items[0], DicomTag.CodeValue) ?? "", Str(cs.Items[0], DicomTag.CodingSchemeDesignator) ?? "", Str(cs.Items[0], DicomTag.CodeMeaning) ?? "")
            : null;
        string? birth = Str(ds, DicomTag.PatientBirthDate);
        return new WorklistItem
        {
            PatientName = Str(ds, DicomTag.PatientName) ?? "", PatientId = Str(ds, DicomTag.PatientID) ?? "",
            PatientBirthDate = birth is { Length: 8 } && DateOnly.TryParseExact(birth, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b) ? b : null,
            PatientSex = Str(ds, DicomTag.PatientSex), AccessionNumber = Str(ds, DicomTag.AccessionNumber) ?? "",
            RequestedProcedureId = Str(ds, DicomTag.RequestedProcedureID), RequestedProcedureDescription = Str(ds, DicomTag.RequestedProcedureDescription),
            RequestedProcedureCode = code, RequestedProcedurePriority = Str(ds, DicomTag.RequestedProcedurePriority),
            ScheduledStationAeTitle = Str(sps, DicomTag.ScheduledStationAETitle), Modality = Str(sps, DicomTag.Modality) ?? "", ScheduledStart = start,
            ScheduledProcedureStepId = Str(sps, DicomTag.ScheduledProcedureStepID), ScheduledProcedureStepDescription = Str(sps, DicomTag.ScheduledProcedureStepDescription),
            ScheduledPerformingPhysician = Str(sps, DicomTag.ScheduledPerformingPhysicianName), ReferringPhysician = Str(ds, DicomTag.ReferringPhysicianName),
            StudyInstanceUid = Str(ds, DicomTag.StudyInstanceUID),
        };
    }

    private static DicomDataset CodeItem(WorklistCode c) => new()
    {
        { DicomTag.CodeValue, Clip(c.Value, 16) }, { DicomTag.CodingSchemeDesignator, Clip(c.Scheme, 16) }, { DicomTag.CodeMeaning, Clip(c.Meaning, 64) },
    };

    private static void Put(DicomDataset ds, DicomTag tag, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) ds.Add(tag, Array.Empty<string>());   // Type 2: present, zero length
        else ds.Add(tag, Clip(value.Trim(), max));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    internal static string? Str(DicomDataset ds, DicomTag tag) =>
        ds.TryGetString(tag, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static bool IsAscii(WorklistItem i) =>
        new[] { i.PatientName, i.PatientId, i.ReferringPhysician, i.RequestedProcedureDescription, i.ScheduledProcedureStepDescription, i.ScheduledPerformingPhysician, i.RequestedProcedureCode?.Meaning }
            .All(s => s is null || s.All(ch => ch < 128));
}

/// <summary>
/// Matching keys of a Modality Worklist C-FIND (DICOM PS3.4 §K.6.1.2.1): Patient's Name (wildcard, case-insensitive),
/// Patient ID, Accession Number, and inside the Scheduled Procedure Step Sequence the Modality, Scheduled Station AE Title
/// and the Scheduled Procedure Step Start Date / Time (range matching, PS3.4 §C.2.2.2.5). Null keys are universal.
/// </summary>
public sealed record WorklistQuery
{
    /// <summary>Patient's Name, with <c>*</c> / <c>?</c> wildcards (PS3.4 §C.2.2.2.4), compared case-insensitively.</summary>
    public string? PatientName { get; init; }
    public string? PatientId { get; init; }
    public string? AccessionNumber { get; init; }
    public string? Modality { get; init; }
    public string? ScheduledStationAeTitle { get; init; }
    /// <summary>Inclusive start-date range; one open end is allowed ("20261116-" or "-20261116").</summary>
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    /// <summary>Inclusive start-time range, matched independently of the date (no combined date-time matching).</summary>
    public TimeOnly? TimeFrom { get; init; }
    public TimeOnly? TimeTo { get; init; }
    /// <summary>Called AE title of the association (set by the SCP; lets one server publish several worklists).</summary>
    public string? CalledAe { get; init; }
    /// <summary>Calling AE title of the association (the querying modality).</summary>
    public string? CallingAe { get; init; }

    /// <summary>A query for one scheduled day (DA single-value matching).</summary>
    public static WorklistQuery ForDate(DateOnly day, string? modality = null) => new() { DateFrom = day, DateTo = day, Modality = modality };

    /// <summary>Reads the matching keys from a C-FIND identifier.</summary>
    public static WorklistQuery FromDataset(DicomDataset identifier, string? calledAe = null, string? callingAe = null)
    {
        var sps = identifier.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var seq) && seq.Items.Count > 0 ? seq.Items[0] : new DicomDataset();
        var (df, dt) = ParseRange(Key(sps, DicomTag.ScheduledProcedureStepStartDate), ParseDate);
        var (tf, tt) = ParseRange(Key(sps, DicomTag.ScheduledProcedureStepStartTime), ParseTime);
        return new WorklistQuery
        {
            PatientName = Key(identifier, DicomTag.PatientName), PatientId = Key(identifier, DicomTag.PatientID), AccessionNumber = Key(identifier, DicomTag.AccessionNumber),
            Modality = Key(sps, DicomTag.Modality), ScheduledStationAeTitle = Key(sps, DicomTag.ScheduledStationAETitle),
            DateFrom = df, DateTo = dt, TimeFrom = tf, TimeTo = tt, CalledAe = calledAe, CallingAe = callingAe,
        };
    }

    /// <summary>
    /// The C-FIND identifier of this query: the matching keys plus every return key of <see cref="WorklistItem"/> as a universal
    /// (zero-length) key, so the SCP returns the full scheduled procedure step.
    /// </summary>
    public DicomDataset ToDataset()
    {
        var ds = new DicomDataset
        {
            { DicomTag.AccessionNumber, AccessionNumber ?? "" }, { DicomTag.ReferringPhysicianName, "" }, { DicomTag.PatientName, PatientName ?? "" },
            { DicomTag.PatientID, PatientId ?? "" }, { DicomTag.PatientBirthDate, "" }, { DicomTag.PatientSex, "" }, { DicomTag.StudyInstanceUID, "" },
            { DicomTag.RequestedProcedureDescription, "" }, { DicomTag.RequestedProcedureID, "" }, { DicomTag.RequestedProcedurePriority, "" },
        };
        if (PatientName is { } pn && pn.Any(ch => ch >= 128)) ds.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
        ds.Add(new DicomSequence(DicomTag.RequestedProcedureCodeSequence));
        var sps = new DicomDataset
        {
            { DicomTag.Modality, Modality ?? "" }, { DicomTag.ScheduledStationAETitle, ScheduledStationAeTitle ?? "" },
            { DicomTag.ScheduledProcedureStepStartDate, Range(DateFrom, DateTo, d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) },
            { DicomTag.ScheduledProcedureStepStartTime, Range(TimeFrom, TimeTo, t => t.ToString("HHmmss", CultureInfo.InvariantCulture)) },
            { DicomTag.ScheduledPerformingPhysicianName, "" }, { DicomTag.ScheduledProcedureStepDescription, "" }, { DicomTag.ScheduledProcedureStepID, "" },
        };
        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    /// <summary>True when <paramref name="item"/> satisfies every matching key.</summary>
    public bool Matches(WorklistItem item) =>
        Text(PatientName, item.PatientName, ignoreCase: true) && Text(PatientId, item.PatientId) && Text(AccessionNumber, item.AccessionNumber) &&
        Text(Modality, item.Modality) && Text(ScheduledStationAeTitle, item.ScheduledStationAeTitle) &&
        (DateFrom is not { } df || DateOnly.FromDateTime(item.ScheduledStart) >= df) && (DateTo is not { } dt || DateOnly.FromDateTime(item.ScheduledStart) <= dt) &&
        (TimeFrom is not { } tf || TimeOnly.FromDateTime(item.ScheduledStart) >= tf) && (TimeTo is not { } tt || TimeOnly.FromDateTime(item.ScheduledStart) <= tt);

    /// <summary>
    /// Single-value / wildcard matching (PS3.4 §C.2.2.2.1, §C.2.2.2.4): a null key or "*" matches everything; otherwise the value
    /// must be present and equal, with <c>*</c> matching any run of characters and <c>?</c> exactly one.
    /// </summary>
    public static bool Text(string? key, string? value, bool ignoreCase = false)
    {
        if (key is null || key == "*") return true;
        if (value is null) return false;
        if (ignoreCase) { key = key.ToUpperInvariant(); value = value.ToUpperInvariant(); }
        return Glob(key.AsSpan(), value.AsSpan());
    }

    private static bool Glob(ReadOnlySpan<char> p, ReadOnlySpan<char> s)
    {
        int pi = 0, si = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si])) { pi++; si++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    private static string? Key(DicomDataset ds, DicomTag tag) => WorklistItem.Str(ds, tag);

    private static string Range<T>(T? from, T? to, Func<T, string> f) where T : struct =>
        (from, to) switch
        {
            (null, null) => "",
            ({ } a, { } b) when EqualityComparer<T>.Default.Equals(a, b) => f(a),
            _ => $"{(from is { } a2 ? f(a2) : "")}-{(to is { } b2 ? f(b2) : "")}",
        };

    private static (T? From, T? To) ParseRange<T>(string? value, Func<string?, T?> parse) where T : struct
    {
        if (value is null) return (null, null);
        int dash = value.IndexOf('-');
        if (dash < 0) { var v = parse(value); return (v, v); }
        return (parse(value[..dash]), parse(value[(dash + 1)..]));
    }

    internal static DateOnly? ParseDate(string? s) =>
        s is { Length: 8 } && DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>DICOM TM: HH[MM[SS[.FFFFFF]]] (PS3.5 §6.2).</summary>
    internal static TimeOnly? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace(":", "");
        int dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        if (s.Length is not (2 or 4 or 6) || !s.All(char.IsAsciiDigit)) return null;
        int h = int.Parse(s[..2], CultureInfo.InvariantCulture), m = s.Length >= 4 ? int.Parse(s[2..4], CultureInfo.InvariantCulture) : 0,
            sec = s.Length == 6 ? int.Parse(s[4..6], CultureInfo.InvariantCulture) : 0;
        return h < 24 && m < 60 && sec < 60 ? new TimeOnly(h, m, sec) : null;
    }
}

/// <summary>Supplies worklist items to <see cref="ModalityWorklistScp"/> (an HIS / RIS adapter).</summary>
public interface IWorklistSource
{
    /// <summary>
    /// Candidate items for <paramref name="query"/>. A source may pre-filter (e.g. by modality or date) or return everything:
    /// the SCP applies the full Annex K matching afterwards.
    /// </summary>
    Task<IReadOnlyList<WorklistItem>> QueryAsync(WorklistQuery query, CancellationToken ct);
}

/// <summary>A fixed list of items (demos, tests).</summary>
public sealed class InMemoryWorklistSource(IEnumerable<WorklistItem> items) : IWorklistSource
{
    private readonly List<WorklistItem> _items = [.. items];

    public Task<IReadOnlyList<WorklistItem>> QueryAsync(WorklistQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorklistItem>>(_items.ToArray());

    /// <summary>Deterministic demo worklist for a day: chest X-ray, head CT, abdominal ultrasound and brain MR (fictional patients).</summary>
    public static InMemoryWorklistSource Demo(DateOnly day)
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        WorklistItem Item(int n, string name, string id, DateOnly birth, string sex, string modality, string code, string desc, int hour, int minute, string priority) => new()
        {
            PatientName = name, PatientId = id, PatientBirthDate = birth, PatientSex = sex,
            AccessionNumber = $"RAD{day:yyMMdd}{n:D4}", RequestedProcedureId = $"RP{n:D4}", RequestedProcedureDescription = desc,
            RequestedProcedureCode = new(code, "99MEDNET", desc), RequestedProcedurePriority = priority, Modality = modality,
            ScheduledStationAeTitle = $"{modality}01", ScheduledStart = d.AddHours(hour).AddMinutes(minute), ScheduledProcedureStepId = $"SPS{n:D4}",
            ScheduledProcedureStepDescription = desc, ReferringPhysician = "Santoso^Budi^^dr.", StudyInstanceUid = $"2.25.{1000 + n}{day:yyyyMMdd}",
        };
        return new([
            Item(1, "Rahayu^Siti", "000101", new(1990, 1, 25), "F", "CR", "87.44", "Foto toraks PA", 8, 30, "ROUTINE"),
            Item(2, "Wijaya^Andi", "000102", new(1975, 6, 3), "M", "CT", "87.03", "CT kepala tanpa kontras", 9, 0, "STAT"),
            Item(3, "Lestari^Dewi", "000103", new(1988, 11, 12), "F", "US", "88.76", "USG abdomen", 10, 15, "ROUTINE"),
            Item(4, "Saputra^Rudi", "000104", new(1962, 3, 30), "M", "MR", "88.91", "MRI otak", 13, 45, "HIGH"),
            Item(5, "Wulandari^Rina", "000105", new(2001, 8, 9), "F", "CT", "88.01", "CT abdomen dengan kontras", 14, 30, "ROUTINE"),
        ]);
    }
}

/// <summary>
/// Modality Worklist SCP: Modality Worklist Information Model – FIND (SOP Class 1.2.840.10008.5.1.4.31, DICOM PS3.4 Annex K)
/// plus Verification. Each matching item is returned as a Pending (FF00) response projected onto the return keys of the
/// identifier (PS3.4 §C.2.2.1.3), followed by a final Success (0000). The <see cref="IWorklistSource"/> is the server's user state.
/// </summary>
public sealed class ModalityWorklistScp : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCEchoProvider
{
    private static readonly DicomTransferSyntax[] Accepted =
        [DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRBigEndian];

    public ModalityWorklistScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies) { }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification || pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind)
                pc.AcceptTransferSyntaxes(Accepted);
            else
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }
        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    public void OnConnectionClosed(Exception exception) { }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) => Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        if (request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind || UserState is not IWorklistSource source)
        {
            yield return new DicomCFindResponse(request, DicomStatus.SOPClassNotSupported);
            yield break;
        }
        var query = WorklistQuery.FromDataset(request.Dataset, Association?.CalledAE, Association?.CallingAE);
        IReadOnlyList<WorklistItem>? items = null;
        Exception? error = null;
        try { items = await source.QueryAsync(query, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { error = e; }
        if (items is null)
        {
            Logger.LogWarning(error, "Worklist source failed");
            // C-FIND failure "Unable to process" (C000–CFFF, PS3.4 Table K.4-1).
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveUnableToProcess);
            yield break;
        }
        foreach (var item in items.Where(query.Matches))
            yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = Project(item.ToDataset(), request.Dataset) };
        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    /// <summary>
    /// Copies the attributes requested in <paramref name="identifier"/> from <paramref name="full"/> (PS3.4 §C.2.2.1.3). A requested
    /// attribute the item lacks is returned zero-length; a sequence key with an item is projected item by item, an empty one returns the whole sequence.
    /// </summary>
    public static DicomDataset Project(DicomDataset full, DicomDataset identifier)
    {
        var result = new DicomDataset();
        if (full.TryGetString(DicomTag.SpecificCharacterSet, out var cs)) result.Add(DicomTag.SpecificCharacterSet, cs);
        foreach (var key in identifier)
        {
            if (key.Tag == DicomTag.SpecificCharacterSet || key.Tag.IsPrivate || key.Tag.Element == 0) continue;
            if (key is DicomSequence ks)
            {
                if (!full.TryGetSequence(ks.Tag, out var fs)) { result.Add(new DicomSequence(ks.Tag)); continue; }
                result.Add(ks.Items.Count == 0
                    ? new DicomSequence(ks.Tag, fs.Items.Select(i => i.Clone()).ToArray())
                    : new DicomSequence(ks.Tag, fs.Items.Select(i => Project(i, ks.Items[0])).ToArray()));
            }
            else if (full.Contains(key.Tag)) result.Add(full.GetDicomItem<DicomItem>(key.Tag));
            else if (key.ValueRepresentation.IsString) result.Add(key.ValueRepresentation, key.Tag, Array.Empty<string>());
        }
        return result;
    }
}

/// <summary>Hosts a <see cref="ModalityWorklistScp"/>.</summary>
public static class ModalityWorklistServer
{
    /// <summary>Default AE title of the Medical.Net worklist.</summary>
    public const string DefaultAeTitle = "MEDICALNET-MWL";

    /// <summary>Starts a Modality Worklist (C-FIND) + Verification server on <paramref name="port"/> backed by <paramref name="source"/>.</summary>
    public static IDicomServer Start(int port, IWorklistSource source) => DicomServerFactory.Create<ModalityWorklistScp>(port, userState: source);
}

/// <summary>Modality Worklist SCU (the modality side), for tests, the CLI and integration checks.</summary>
public static class ModalityWorklistClient
{
    /// <summary>Sends one Modality Worklist C-FIND and returns the Pending response identifiers; throws when the final status is not Success.</summary>
    public static async Task<IReadOnlyList<DicomDataset>> QueryAsync(string host, int port, string callingAe, string calledAe, WorklistQuery query,
        CancellationToken ct = default)
    {
        var client = DicomClientFactory.Create(host, port, false, callingAe, calledAe);
        var results = new List<DicomDataset>();
        DicomStatus? final = null;
        var req = new DicomCFindRequest(DicomUID.ModalityWorklistInformationModelFind, DicomPriority.Medium) { Dataset = query.ToDataset() };
        req.OnResponseReceived = (_, r) =>
        {
            if (r.Status.State == DicomState.Pending) { if (r.HasDataset) lock (results) results.Add(r.Dataset); }
            else final = r.Status;
        };
        await client.AddRequestAsync(req).ConfigureAwait(false);
        await client.SendAsync(ct).ConfigureAwait(false);
        if (final != DicomStatus.Success) throw new InvalidOperationException($"Worklist C-FIND failed: {final?.ToString() ?? "no final response"}.");
        return results;
    }

    /// <summary><see cref="QueryAsync"/> mapped to <see cref="WorklistItem"/>s.</summary>
    public static async Task<IReadOnlyList<WorklistItem>> QueryItemsAsync(string host, int port, string callingAe, string calledAe, WorklistQuery query,
        CancellationToken ct = default) =>
        (await QueryAsync(host, port, callingAe, calledAe, query, ct).ConfigureAwait(false)).Select(WorklistItem.FromDataset).ToList();
}
