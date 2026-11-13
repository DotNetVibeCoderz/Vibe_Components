using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Medical.Core;
using Medical.Signals;

namespace Medical.Dicom;

/// <summary>
/// DICOM 12-lead ECG Waveform Storage (PS3.3 A.34.3): one multiplex group of 12 channels, 16-bit signed samples,
/// channel sources coded with the SCPECG lead codes of CID 3001, sensitivity in µV, and the global measurements
/// (heart rate, PR, QRS, QT, QTc) as waveform annotations with LOINC codes.
/// </summary>
public static class DicomEcg
{
    public static readonly DicomUID TwelveLeadEcgStorage = DicomUID.TwelveLeadECGWaveformStorage;

    /// <summary>SCPECG lead code (CID 3001), e.g. 5.6.3-9-1 for lead I.</summary>
    public static string ScpCode(EcgLead lead) => lead switch
    {
        EcgLead.I => "5.6.3-9-1", EcgLead.II => "5.6.3-9-2", EcgLead.V1 => "5.6.3-9-3", EcgLead.V2 => "5.6.3-9-4",
        EcgLead.V3 => "5.6.3-9-5", EcgLead.V4 => "5.6.3-9-6", EcgLead.V5 => "5.6.3-9-7", EcgLead.V6 => "5.6.3-9-8",
        EcgLead.III => "5.6.3-9-61", EcgLead.aVR => "5.6.3-9-62", EcgLead.aVL => "5.6.3-9-63", _ => "5.6.3-9-64",
    };

    /// <summary>
    /// Encodes an ECG. Samples are stored in units of <paramref name="resolutionUv"/> µV (default 1 µV, i.e. ±32.7 mV range).
    /// </summary>
    public static DicomFile Create(Ecg12 ecg, Patient patient, Ecg12Analysis? measurements = null, double resolutionUv = 1, DateTimeOffset? acquired = null)
    {
        var at = (acquired ?? DateTimeOffset.Now).ToLocalTime();
        string study = DicomPhantom.Uid($"ecg-study/{patient.Id}/{at.ToUnixTimeSeconds()}");
        string series = DicomPhantom.Uid($"ecg-series/{study}");
        string sop = DicomPhantom.Uid($"ecg/{series}");
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, TwelveLeadEcgStorage },
            { DicomTag.SOPInstanceUID, sop },
            { DicomTag.StudyInstanceUID, study },
            { DicomTag.SeriesInstanceUID, series },
            { DicomTag.Modality, "ECG" },
            { DicomTag.PatientName, $"{patient.Name.Family}^{patient.Name.Given}" },
            { DicomTag.PatientID, patient.Mrn },
            { DicomTag.PatientBirthDate, patient.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.PatientSex, patient.Gender switch { Gender.Male => "M", Gender.Female => "F", _ => "O" } },
            { DicomTag.StudyDate, at.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.StudyTime, at.ToString("HHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.AccessionNumber, "" },
            { DicomTag.ReferringPhysicianName, "" },
            { DicomTag.StudyID, "1" },
            { DicomTag.SeriesNumber, "1" },
            { DicomTag.InstanceNumber, "1" },
            { DicomTag.ContentDate, at.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.ContentTime, at.ToString("HHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.AcquisitionDateTime, at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.StudyDescription, "Resting 12-lead ECG" },
        };
        DicomSegmentation.AddEquipment(ds);
        ds.Add(new DicomSequence(DicomTag.AcquisitionContextSequence));

        int n = ecg.Samples;
        var channels = new List<DicomDataset>();
        foreach (var lead in Ecg12.Order)
            channels.Add(new DicomDataset
            {
                new DicomSequence(DicomTag.ChannelSourceSequence, new DicomDataset
                {
                    { DicomTag.CodeValue, ScpCode(lead) }, { DicomTag.CodingSchemeDesignator, "SCPECG" },
                    { DicomTag.CodingSchemeVersion, "1.3" }, { DicomTag.CodeMeaning, $"Lead {lead}" },
                }),
                new DicomDecimalString(DicomTag.ChannelSensitivity, resolutionUv.ToString("0.###", CultureInfo.InvariantCulture)),
                new DicomSequence(DicomTag.ChannelSensitivityUnitsSequence, new DicomCode("uV", "UCUM", "microvolt").ToDataset()),
                new DicomDecimalString(DicomTag.ChannelSensitivityCorrectionFactor, "1"),
                new DicomDecimalString(DicomTag.ChannelBaseline, "0"),
                new DicomDecimalString(DicomTag.ChannelSampleSkew, "0"),
                { DicomTag.WaveformBitsStored, (ushort)16 },
                new DicomDecimalString(DicomTag.FilterLowFrequency, "0.05"),
                new DicomDecimalString(DicomTag.FilterHighFrequency, "150"),
            });

        var data = new short[n * 12];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 12; c++)
            {
                double v = Math.Round(ecg.Leads[c][i] * 1000 / resolutionUv, MidpointRounding.AwayFromZero);
                data[i * 12 + c] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        ds.Add(new DicomSequence(DicomTag.WaveformSequence, new DicomDataset
        {
            { DicomTag.MultiplexGroupTimeOffset, "0" },
            { DicomTag.WaveformOriginality, "ORIGINAL" },
            { DicomTag.NumberOfWaveformChannels, (ushort)12 },
            { DicomTag.NumberOfWaveformSamples, (uint)n },
            new DicomDecimalString(DicomTag.SamplingFrequency, ecg.SampleRate.ToString("0.###", CultureInfo.InvariantCulture)),
            { DicomTag.MultiplexGroupLabel, "RHYTHM" },
            new DicomSequence(DicomTag.ChannelDefinitionSequence, channels.ToArray()),
            { DicomTag.WaveformBitsAllocated, (ushort)16 },
            { DicomTag.WaveformSampleInterpretation, "SS" },
            new DicomOtherWord(DicomTag.WaveformData, new MemoryByteBuffer(bytes)),
        }));

        if (measurements is not null)
        {
            DicomDataset Ann(string loinc, string meaning, double value, DicomCode unit) => new()
            {
                new DicomSequence(DicomTag.ConceptNameCodeSequence, new DicomCode(loinc, "LN", meaning).ToDataset()),
                new DicomDecimalString(DicomTag.NumericValue, value.ToString("0.#", CultureInfo.InvariantCulture)),
                new DicomSequence(DicomTag.MeasurementUnitsCodeSequence, unit.ToDataset()),
                { DicomTag.AnnotationGroupNumber, (ushort)0 },
            };
            var ms = new DicomCode("ms", "UCUM", "millisecond");
            var items = new List<DicomDataset>
            {
                Ann("8867-4", "Heart rate", measurements.HeartRateBpm, new DicomCode("/min", "UCUM", "per minute")),
                Ann("8633-0", "QRS duration", measurements.QrsMs, ms),
                Ann("8634-8", "Q-T interval", measurements.QtMs, ms),
                Ann("8636-3", "Q-T interval corrected", measurements.QtcBazettMs, ms),
                Ann("8632-2", "QRS axis", measurements.QrsAxisDeg, new DicomCode("deg", "UCUM", "degree")),
            };
            if (measurements.PrMs is { } pr) items.Insert(1, Ann("8625-6", "P-R interval", pr, ms));
            items.Add(new DicomDataset
            {
                { DicomTag.UnformattedTextValue, measurements.Interpretation.En },
                { DicomTag.AnnotationGroupNumber, (ushort)0 },
            });
            ds.Add(new DicomSequence(DicomTag.WaveformAnnotationSequence, items.ToArray()));
        }
        return new DicomFile(ds);
    }

    /// <summary>Decodes the first 12-channel multiplex group (any channel order, identified by CID 3001 / MDC codes or labels).</summary>
    public static Ecg12 Read(DicomFile file)
    {
        var group = file.Dataset.GetSequence(DicomTag.WaveformSequence).Items
            .FirstOrDefault(g => g.GetSingleValueOrDefault<ushort>(DicomTag.NumberOfWaveformChannels, 0) >= 12)
            ?? throw new InvalidOperationException("No multiplex group with 12 channels.");
        int nch = group.GetSingleValue<ushort>(DicomTag.NumberOfWaveformChannels);
        int n = (int)group.GetSingleValue<uint>(DicomTag.NumberOfWaveformSamples);
        double fs = group.GetSingleValue<double>(DicomTag.SamplingFrequency);
        int bits = group.GetSingleValue<ushort>(DicomTag.WaveformBitsAllocated);
        if (bits != 16) throw new NotSupportedException($"WaveformBitsAllocated {bits} is not supported.");
        var raw = group.GetDicomItem<DicomElement>(DicomTag.WaveformData).Buffer.Data;
        var defs = group.GetSequence(DicomTag.ChannelDefinitionSequence).Items;
        var leads = new float[12][];
        for (int c = 0; c < nch && c < defs.Count; c++)
        {
            var def = defs[c];
            var src = def.GetSequence(DicomTag.ChannelSourceSequence).Items[0];
            var lead = Identify(src.GetSingleValueOrDefault(DicomTag.CodeValue, ""), src.GetSingleValueOrDefault(DicomTag.CodeMeaning, ""));
            if (lead is null || leads[(int)lead] is not null) continue;
            double sens = def.GetSingleValueOrDefault(DicomTag.ChannelSensitivity, 1.0) * def.GetSingleValueOrDefault(DicomTag.ChannelSensitivityCorrectionFactor, 1.0);
            double baseline = def.GetSingleValueOrDefault(DicomTag.ChannelBaseline, 0.0);
            string unit = def.TryGetSequence(DicomTag.ChannelSensitivityUnitsSequence, out var u) && u.Items.Count > 0 ? u.Items[0].GetSingleValueOrDefault(DicomTag.CodeValue, "uV") : "uV";
            double toMv = unit switch { "mV" => 1, "V" => 1000, _ => 0.001 };
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                short s = BitConverter.ToInt16(raw, (i * nch + c) * 2);
                x[i] = (float)((s * sens + baseline) * toMv);
            }
            leads[(int)lead] = x;
        }
        // Devices that store only I, II, V1–V6: derive the rest.
        if (leads[0] is not null && leads[1] is not null)
        {
            for (int l = 2; l <= 5; l++) leads[l] ??= new float[n];
            if (new[] { 2, 3, 4, 5 }.All(l => leads[l].All(v => v == 0)))
                Ecg12.DeriveLimbLeads(leads[0], leads[1], leads[2], leads[3], leads[4], leads[5]);
        }
        for (int l = 0; l < 12; l++) leads[l] ??= new float[n];
        return new Ecg12(leads, fs);
    }

    private static EcgLead? Identify(string code, string meaning)
    {
        foreach (var l in Ecg12.Order)
            if (code == ScpCode(l)) return l;
        // MDC codes (2:1 … ) and labels as a fallback
        string m = meaning.Replace("Lead", "", StringComparison.OrdinalIgnoreCase).Trim();
        foreach (var l in Ecg12.Order)
            if (string.Equals(m, l.ToString(), StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }
}
