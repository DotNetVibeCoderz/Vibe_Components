using FellowOakDicom;
using Medical.Core;

namespace Medical.Dicom;

/// <summary>Summary of what was removed or replaced.</summary>
public sealed record DeidentificationReport(string OriginalPatientId, string Pseudonym, int ElementsBefore, int ElementsAfter, int DateShiftDays);

/// <summary>
/// DICOM PS3.15 Annex E Basic Application Level Confidentiality Profile (fo-dicom <see cref="DicomAnonymizer"/>) plus
/// stable HMAC pseudonyms for PatientID / PatientName so de-identified studies of one patient stay linkable.
/// UIDs are replaced consistently for every dataset passed through the same instance.
/// </summary>
public sealed class DicomDeidentifier(string secret)
{
    private readonly Pseudonymizer _pseudo = new(secret);
    private readonly DicomAnonymizer _anonymizer = new(DicomAnonymizer.SecurityProfile.LoadProfile(null, DicomAnonymizer.SecurityProfileOptions.BasicProfile));

    public DicomFile Deidentify(DicomFile file, out DeidentificationReport report)
    {
        var copy = file.Clone();
        report = DeidentifyInPlace(copy.Dataset);
        return copy;
    }

    public DeidentificationReport DeidentifyInPlace(DicomDataset ds)
    {
        string pid = ds.GetSingleValueOrDefault(DicomTag.PatientID, "");
        int before = ds.Count();
        int shift = _pseudo.DateShiftDays(pid);
        string token = _pseudo.Pseudonym(pid, "ANON-");
        _anonymizer.AnonymizeInPlace(ds);
        ds.AddOrUpdate(DicomTag.PatientID, token);
        ds.AddOrUpdate(DicomTag.PatientName, token);
        ds.AddOrUpdate(DicomTag.PatientIdentityRemoved, "YES");
        ds.AddOrUpdate(DicomTag.DeidentificationMethod, "DICOM PS3.15 Basic Profile; HMAC-SHA256 pseudonym (Medical.Net)");
        ds.AddOrUpdate(new DicomSequence(DicomTag.DeidentificationMethodCodeSequence,
            new DicomDataset { { DicomTag.CodeValue, "113100" }, { DicomTag.CodingSchemeDesignator, "DCM" }, { DicomTag.CodeMeaning, "Basic Application Confidentiality Profile" } }));
        return new DeidentificationReport(pid, token, before, ds.Count(), shift);
    }
}
