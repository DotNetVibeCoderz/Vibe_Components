using System.Globalization;
using System.Text;
using FellowOakDicom;

namespace Medical.Dicom;

/// <summary>One numeric measurement (value + concept + UCUM unit).</summary>
public sealed record SrMeasurement(DicomCode Concept, double Value, DicomCode Unit);

/// <summary>
/// One measured finding (TID 1411 Volumetric ROI Measurements): what was found, where, the segment that delineates it,
/// and its measurements.
/// </summary>
public sealed record SrFinding(string TrackingId, DicomCode Finding, IReadOnlyList<SrMeasurement> Measurements)
{
    public DicomCode? FindingSite { get; init; }
    /// <summary>Segment number in the referenced SEG instance.</summary>
    public int? SegmentNumber { get; init; }
}

/// <summary>A measurement report read back from an SR document.</summary>
public sealed record MeasurementReportContent(string Title, string ObserverName, IReadOnlyList<SrFinding> Findings, string Text);

/// <summary>
/// DICOM Structured Reporting: TID 1500 "Measurement Report" in a Comprehensive 3D SR document, with one TID 1411
/// measurement group per finding referencing its DICOM SEG segment — the format OHIF, 3D Slicer, highdicom and AI
/// results viewers exchange. Includes the Current Requested Procedure Evidence for the source images and the SEG.
/// </summary>
public static class MeasurementReport
{
    public static readonly DicomUID Comprehensive3DSRStorage = DicomUID.Comprehensive3DSRStorage;

    private static readonly DicomCode ReportTitle = new("126000", "DCM", "Imaging Measurement Report");
    private static readonly DicomCode Language = new("121049", "DCM", "Language of Content Item and Descendants");
    private static readonly DicomCode English = new("en-US", "RFC5646", "English (United States)");
    private static readonly DicomCode ObserverType = new("121005", "DCM", "Observer Type");
    private static readonly DicomCode DeviceObserver = new("121007", "DCM", "Device");
    private static readonly DicomCode DeviceUid = new("121012", "DCM", "Device Observer UID");
    private static readonly DicomCode DeviceName = new("121013", "DCM", "Device Observer Name");
    private static readonly DicomCode ProcedureReported = new("121058", "DCM", "Procedure reported");
    private static readonly DicomCode ImagingMeasurements = new("126010", "DCM", "Imaging Measurements");
    private static readonly DicomCode MeasurementGroup = new("125007", "DCM", "Measurement Group");
    private static readonly DicomCode TrackingIdentifier = new("112039", "DCM", "Tracking Identifier");
    private static readonly DicomCode TrackingUid = new("112040", "DCM", "Tracking Unique Identifier");
    private static readonly DicomCode Finding = new("121071", "DCM", "Finding");
    private static readonly DicomCode ReferencedSegment = new("121191", "DCM", "Referenced Segment");
    private static readonly DicomCode SourceSeries = new("121232", "DCM", "Source series for segmentation");

    public static DicomFile Create(DicomVolume source, DicomFile? segmentation, IReadOnlyList<SrFinding> findings,
        string observerName = "Medical.Net imaging AI", DateTimeOffset? at = null)
    {
        if (source.Reference is null) throw new ArgumentException("The volume has no reference dataset.", nameof(source));
        var now = (at ?? DateTimeOffset.Now).ToLocalTime();
        string series = DicomPhantom.Uid($"sr-series/{source.SeriesInstanceUid}");
        string sop = DicomPhantom.Uid($"sr/{series}/{now.ToUnixTimeMilliseconds()}/{findings.Count}");
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        DicomSegmentation.CopyPatientStudy(source.Reference, ds);
        DicomSegmentation.AddEquipment(ds);
        ds.AddOrUpdate(DicomTag.SOPClassUID, Comprehensive3DSRStorage);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sop);
        ds.AddOrUpdate(DicomTag.Modality, "SR");
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, series);
        ds.AddOrUpdate(DicomTag.SeriesNumber, "301");
        ds.AddOrUpdate(DicomTag.SeriesDescription, "Measurement report (Medical.Net)");
        ds.AddOrUpdate(DicomTag.InstanceNumber, "1");
        ds.AddOrUpdate(DicomTag.ContentDate, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        ds.AddOrUpdate(DicomTag.ContentTime, now.ToString("HHmmss", CultureInfo.InvariantCulture));
        ds.AddOrUpdate(DicomTag.CompletionFlag, "COMPLETE");
        ds.AddOrUpdate(DicomTag.VerificationFlag, "UNVERIFIED");
        ds.Add(new DicomSequence(DicomTag.ReferencedPerformedProcedureStepSequence));
        ds.Add(new DicomSequence(DicomTag.PerformedProcedureCodeSequence));

        // Evidence: the source series and the SEG.
        var evidence = new List<(string Series, string SopClass, IEnumerable<string> Instances)>
        {
            (source.SeriesInstanceUid, source.SopClassUid, source.SliceSopInstanceUids),
        };
        if (segmentation is not null)
            evidence.Add((segmentation.Dataset.GetString(DicomTag.SeriesInstanceUID), DicomSegmentation.SegmentationStorage.UID, [segmentation.Dataset.GetString(DicomTag.SOPInstanceUID)]));
        ds.Add(new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence, new DicomDataset
        {
            { DicomTag.StudyInstanceUID, source.StudyInstanceUid },
            new DicomSequence(DicomTag.ReferencedSeriesSequence, evidence.Select(e => new DicomDataset
            {
                { DicomTag.SeriesInstanceUID, e.Series },
                new DicomSequence(DicomTag.ReferencedSOPSequence, e.Instances.Select(i => new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, e.SopClass }, { DicomTag.ReferencedSOPInstanceUID, i },
                }).ToArray()),
            }).ToArray()),
        }));

        // Content tree.
        ds.AddOrUpdate(DicomTag.ValueType, "CONTAINER");
        ds.Add(new DicomSequence(DicomTag.ConceptNameCodeSequence, ReportTitle.ToDataset()));
        ds.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
        ds.Add(new DicomSequence(DicomTag.ContentTemplateSequence, new DicomDataset { { DicomTag.MappingResource, "DCMR" }, { DicomTag.TemplateIdentifier, "1500" } }));
        var root = new List<DicomDataset>
        {
            Code("HAS CONCEPT MOD", Language, English),
            Code("HAS OBS CONTEXT", ObserverType, DeviceObserver),
            UidRef("HAS OBS CONTEXT", DeviceUid, DicomPhantom.Uid("device/medical.net")),
            Text("HAS OBS CONTEXT", DeviceName, observerName),
            Code("HAS CONCEPT MOD", ProcedureReported, DicomCodes.ComputedTomography),
        };
        var groups = new List<DicomDataset>();
        string? segSop = segmentation?.Dataset.GetString(DicomTag.SOPInstanceUID);
        foreach (var f in findings)
        {
            var items = new List<DicomDataset>
            {
                Text("HAS OBS CONTEXT", TrackingIdentifier, f.TrackingId),
                UidRef("HAS OBS CONTEXT", TrackingUid, DicomPhantom.Uid($"track/{sop}/{f.TrackingId}")),
                Code("CONTAINS", Finding, f.Finding),
            };
            if (f.FindingSite is { } site) items.Add(Code("HAS CONCEPT MOD", DicomCodes.FindingSite, site));
            if (segSop is not null && f.SegmentNumber is { } seg)
            {
                var img = Item("CONTAINS", "IMAGE", ReferencedSegment);
                img.Add(new DicomSequence(DicomTag.ReferencedSOPSequence, new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomSegmentation.SegmentationStorage.UID },
                    { DicomTag.ReferencedSOPInstanceUID, segSop },
                    { DicomTag.ReferencedSegmentNumber, (ushort)seg },
                }));
                items.Add(img);
                items.Add(UidRef("CONTAINS", SourceSeries, source.SeriesInstanceUid));
            }
            foreach (var m in f.Measurements) items.Add(Num(m));
            var group = Item("CONTAINS", "CONTAINER", MeasurementGroup);
            group.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
            group.Add(new DicomSequence(DicomTag.ContentTemplateSequence, new DicomDataset { { DicomTag.MappingResource, "DCMR" }, { DicomTag.TemplateIdentifier, "1411" } }));
            group.Add(new DicomSequence(DicomTag.ContentSequence, items.ToArray()));
            groups.Add(group);
        }
        var measurements = Item("CONTAINS", "CONTAINER", ImagingMeasurements);
        measurements.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
        measurements.Add(new DicomSequence(DicomTag.ContentSequence, groups.ToArray()));
        root.Add(measurements);
        ds.Add(new DicomSequence(DicomTag.ContentSequence, root.ToArray()));
        return new DicomFile(ds);
    }

    /// <summary>Reads the findings of a TID 1500 report (also produces a plain-text rendering of the whole tree).</summary>
    public static MeasurementReportContent Read(DicomFile sr)
    {
        var ds = sr.Dataset;
        string title = DicomCode.From(ds.GetSequence(DicomTag.ConceptNameCodeSequence).Items[0]).Meaning;
        string observer = "";
        var findings = new List<SrFinding>();
        foreach (var item in ds.GetSequence(DicomTag.ContentSequence).Items)
        {
            var name = Concept(item);
            if (name == DeviceName) observer = item.GetSingleValueOrDefault(DicomTag.TextValue, "");
            if (name != ImagingMeasurements || !item.TryGetSequence(DicomTag.ContentSequence, out var groups)) continue;
            foreach (var g in groups.Items)
            {
                string id = ""; DicomCode finding = default; DicomCode? site = null; int? segment = null;
                var ms = new List<SrMeasurement>();
                foreach (var c in g.GetSequence(DicomTag.ContentSequence).Items)
                {
                    var cn = Concept(c);
                    string vt = c.GetSingleValueOrDefault(DicomTag.ValueType, "");
                    if (cn == TrackingIdentifier) id = c.GetSingleValueOrDefault(DicomTag.TextValue, "");
                    else if (cn == Finding) finding = DicomCode.From(c.GetSequence(DicomTag.ConceptCodeSequence).Items[0]);
                    else if (cn == DicomCodes.FindingSite) site = DicomCode.From(c.GetSequence(DicomTag.ConceptCodeSequence).Items[0]);
                    else if (cn == ReferencedSegment) segment = c.GetSequence(DicomTag.ReferencedSOPSequence).Items[0].GetSingleValueOrDefault<ushort>(DicomTag.ReferencedSegmentNumber, 0);
                    else if (vt == "NUM")
                    {
                        var mv = c.GetSequence(DicomTag.MeasuredValueSequence).Items[0];
                        ms.Add(new SrMeasurement(cn, double.Parse(mv.GetString(DicomTag.NumericValue), CultureInfo.InvariantCulture),
                            DicomCode.From(mv.GetSequence(DicomTag.MeasurementUnitsCodeSequence).Items[0])));
                    }
                }
                findings.Add(new SrFinding(id, finding, ms) { FindingSite = site, SegmentNumber = segment });
            }
        }
        var text = new StringBuilder();
        Render(ds, 0, text);
        return new MeasurementReportContent(title, observer, findings, text.ToString());
    }

    private static void Render(DicomDataset item, int depth, StringBuilder sb)
    {
        string vt = item.GetSingleValueOrDefault(DicomTag.ValueType, "");
        string name = item.TryGetSequence(DicomTag.ConceptNameCodeSequence, out var cn) && cn.Items.Count > 0 ? DicomCode.From(cn.Items[0]).Meaning : "";
        string rel = item.GetSingleValueOrDefault(DicomTag.RelationshipType, "");
        string value = vt switch
        {
            "TEXT" => item.GetSingleValueOrDefault(DicomTag.TextValue, ""),
            "UIDREF" => item.GetSingleValueOrDefault(DicomTag.UID, ""),
            "CODE" => DicomCode.From(item.GetSequence(DicomTag.ConceptCodeSequence).Items[0]).Meaning,
            "NUM" when item.TryGetSequence(DicomTag.MeasuredValueSequence, out var mv) && mv.Items.Count > 0 =>
                $"{mv.Items[0].GetString(DicomTag.NumericValue)} {DicomCode.From(mv.Items[0].GetSequence(DicomTag.MeasurementUnitsCodeSequence).Items[0]).Value}",
            "IMAGE" when item.TryGetSequence(DicomTag.ReferencedSOPSequence, out var r) && r.Items.Count > 0 =>
                $"SEG segment {r.Items[0].GetSingleValueOrDefault<ushort>(DicomTag.ReferencedSegmentNumber, 0)}",
            _ => "",
        };
        sb.Append(new string(' ', depth * 2)).Append(rel.Length > 0 ? $"[{rel}] " : "").Append(vt).Append(' ').Append(name);
        if (value.Length > 0) sb.Append(" = ").Append(value);
        sb.Append('\n');
        if (item.TryGetSequence(DicomTag.ContentSequence, out var children))
            foreach (var c in children.Items) Render(c, depth + 1, sb);
    }

    private static DicomCode Concept(DicomDataset item) =>
        item.TryGetSequence(DicomTag.ConceptNameCodeSequence, out var s) && s.Items.Count > 0 ? DicomCode.From(s.Items[0]) : default;

    private static DicomDataset Item(string relationship, string valueType, DicomCode concept) => new()
    {
        { DicomTag.RelationshipType, relationship }, { DicomTag.ValueType, valueType },
        new DicomSequence(DicomTag.ConceptNameCodeSequence, concept.ToDataset()),
    };

    private static DicomDataset Code(string rel, DicomCode concept, DicomCode value)
    {
        var d = Item(rel, "CODE", concept);
        d.Add(new DicomSequence(DicomTag.ConceptCodeSequence, value.ToDataset()));
        return d;
    }

    private static DicomDataset Text(string rel, DicomCode concept, string value)
    {
        var d = Item(rel, "TEXT", concept);
        d.Add(DicomTag.TextValue, value);
        return d;
    }

    private static DicomDataset UidRef(string rel, DicomCode concept, string uid)
    {
        var d = Item(rel, "UIDREF", concept);
        d.Add(DicomTag.UID, uid);
        return d;
    }

    private static DicomDataset Num(SrMeasurement m)
    {
        var d = Item("CONTAINS", "NUM", m.Concept);
        d.Add(new DicomSequence(DicomTag.MeasuredValueSequence, new DicomDataset
        {
            new DicomDecimalString(DicomTag.NumericValue, m.Value.ToString("0.###", CultureInfo.InvariantCulture)),
            new DicomSequence(DicomTag.MeasurementUnitsCodeSequence, m.Unit.ToDataset()),
        }));
        return d;
    }
}
