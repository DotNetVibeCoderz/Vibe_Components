using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using Medical.Core;

namespace Medical.Dicom;

public enum PhantomKind { Head, Chest }

/// <summary>
/// Synthetic CT series in Hounsfield units (a Shepp-Logan-inspired head with a hyperdense lesion, or a chest with lungs,
/// heart and spine). Produces valid CT Image Storage instances with deterministic UIDs — handy for demos, tests and
/// pipelines without real patient data.
/// </summary>
public static class DicomPhantom
{
    private readonly record struct Ellipse(double Cx, double Cy, double Rx, double Ry, double AngleDeg, double Hu, double ZRadius, double Cz = 0);

    // Normalised coordinates in [-1, 1]; later ellipses override earlier ones.
    private static readonly Ellipse[] Head =
    [
        new(0, 0, 0.72, 0.92, 0, 1000, 1.10),        // skull
        new(0, -0.02, 0.66, 0.86, 0, 34, 1.05),      // brain parenchyma
        new(0.0, -0.02, 0.60, 0.80, 0, 38, 0.95),    // grey matter rim (slightly denser)
        new(0.0, -0.05, 0.50, 0.68, 0, 28, 0.85),    // white matter
        new(-0.12, 0.08, 0.07, 0.22, 18, 6, 0.45),   // left lateral ventricle (CSF)
        new(0.12, 0.08, 0.07, 0.22, -18, 6, 0.45),   // right lateral ventricle
        new(0, 0.32, 0.05, 0.05, 0, 6, 0.25),        // third ventricle
        new(0.30, -0.32, 0.09, 0.07, 30, 72, 0.18, 0.15), // hyperdense lesion (acute haemorrhage)
        new(0, -0.62, 0.18, 0.10, 0, -1000, 0.40, -0.5),  // frontal sinus (air)
    ];

    private static readonly Ellipse[] Chest =
    [
        new(0, 0, 0.94, 0.70, 0, -110, 1.2),          // subcutaneous fat
        new(0, 0, 0.88, 0.64, 0, 45, 1.2),            // soft tissue / muscle
        new(-0.42, -0.05, 0.32, 0.46, 8, -840, 1.0),  // right lung
        new(0.42, -0.05, 0.30, 0.44, -8, -840, 1.0),  // left lung
        new(0.10, 0.08, 0.24, 0.20, 25, 48, 0.7),     // heart
        new(0.00, -0.20, 0.07, 0.07, 0, 52, 1.2),     // aorta (contrast-free)
        new(0, 0.52, 0.10, 0.09, 0, 650, 1.2),        // vertebral body
        new(0, 0.62, 0.04, 0.05, 0, 20, 1.2),         // spinal canal
        new(-0.50, -0.10, 0.06, 0.05, 0, 30, 0.2, 0.2), // pulmonary nodule
    ];

    /// <summary>Hounsfield value at normalised (x, y, z) — z in [-1, 1] across the series.</summary>
    public static double HuAt(PhantomKind kind, double x, double y, double z)
    {
        double hu = -1000;
        foreach (var e in kind == PhantomKind.Head ? Head : Chest)
        {
            double dz = (z - e.Cz) / e.ZRadius;
            if (Math.Abs(dz) >= 1) continue;
            double scale = Math.Sqrt(1 - dz * dz);
            double a = e.AngleDeg * Math.PI / 180, dx = x - e.Cx, dy = y - e.Cy;
            double u = (dx * Math.Cos(a) + dy * Math.Sin(a)) / (e.Rx * scale), v = (-dx * Math.Sin(a) + dy * Math.Cos(a)) / (e.Ry * scale);
            if (u * u + v * v <= 1) hu = e.Hu;
        }
        return hu;
    }

    /// <summary>Creates a CT series of <paramref name="slices"/> instances, each <paramref name="size"/>² pixels (int16, intercept −1024).</summary>
    public static IReadOnlyList<DicomFile> CreateSeries(PhantomKind kind = PhantomKind.Head, int slices = 24, int size = 256, Patient? patient = null, ulong seed = 42)
    {
        patient ??= SyntheticData.Patient(1);
        string study = Uid($"study/{kind}/{patient.Id}/{seed}"), series = Uid($"series/{kind}/{patient.Id}/{seed}");
        var rng = new SeededRandom(seed);
        double spacing = (kind == PhantomKind.Head ? 220.0 : 380.0) / size, thickness = kind == PhantomKind.Head ? 5.0 : 6.0;
        var files = new List<DicomFile>(slices);
        for (int s = 0; s < slices; s++)
        {
            double z = slices == 1 ? 0 : -0.95 + 1.9 * s / (slices - 1);
            var pixels = new short[size * size];
            for (int py = 0; py < size; py++)
            {
                double y = (py + 0.5) / size * 2 - 1;
                for (int px = 0; px < size; px++)
                {
                    double x = (px + 0.5) / size * 2 - 1;
                    double hu = HuAt(kind, x, y, z);
                    if (hu > -1000) hu += rng.Gaussian(0, 6); // quantum noise
                    pixels[py * size + px] = (short)Math.Clamp(Math.Round(hu + 1024), -2048, 4095);
                }
            }
            files.Add(CreateInstance(patient, kind, study, series, s + 1, size, spacing, thickness, z * slices * thickness / 2, pixels));
        }
        return files;
    }

    private static DicomFile CreateInstance(Patient p, PhantomKind kind, string study, string series, int number, int size, double spacing, double thickness, double location, short[] pixels)
    {
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        string sop = Uid($"{series}/{number}");
        var preset = kind == PhantomKind.Head ? WindowPreset.Brain : WindowPreset.Lung;
        ds.Add(DicomTag.SOPClassUID, DicomUID.CTImageStorage);
        ds.Add(DicomTag.SOPInstanceUID, sop);
        ds.Add(DicomTag.StudyInstanceUID, study);
        ds.Add(DicomTag.SeriesInstanceUID, series);
        ds.Add(DicomTag.PatientName, $"{p.Name.Family}^{p.Name.Given}");
        ds.Add(DicomTag.PatientID, p.Mrn);
        ds.Add(DicomTag.PatientBirthDate, p.BirthDate.ToString("yyyyMMdd"));
        ds.Add(DicomTag.PatientSex, p.Gender switch { Gender.Male => "M", Gender.Female => "F", _ => "O" });
        ds.Add(DicomTag.StudyDate, "20261110");
        ds.Add(DicomTag.StudyTime, "083000");
        ds.Add(DicomTag.AccessionNumber, $"ACC{SeededRandom.Hash(study) % 1_000_000:000000}");
        ds.Add(DicomTag.StudyID, "1");
        ds.Add(DicomTag.ReferringPhysicianName, "Kusuma^Ratna^^dr.");
        ds.Add(DicomTag.InstitutionName, "Medical.Net Demo Hospital");
        ds.Add(DicomTag.Modality, "CT");
        ds.Add(DicomTag.Manufacturer, "Medical.Net Phantom");
        ds.Add(DicomTag.StudyDescription, kind == PhantomKind.Head ? "CT HEAD WITHOUT CONTRAST" : "CT CHEST WITHOUT CONTRAST");
        ds.Add(DicomTag.SeriesDescription, kind == PhantomKind.Head ? "Axial 5mm Brain" : "Axial 6mm Lung");
        ds.Add(DicomTag.BodyPartExamined, kind == PhantomKind.Head ? "HEAD" : "CHEST");
        ds.Add(DicomTag.SeriesNumber, "2");
        ds.Add(DicomTag.InstanceNumber, number.ToString());
        ds.Add(DicomTag.ImageType, "ORIGINAL", "PRIMARY", "AXIAL");
        ds.Add(DicomTag.SliceThickness, (decimal)thickness);
        ds.Add(DicomTag.SliceLocation, (decimal)Math.Round(location, 2));
        ds.Add(DicomTag.ImagePositionPatient, (decimal)(-size * spacing / 2), (decimal)(-size * spacing / 2), (decimal)Math.Round(location, 2));
        ds.Add(DicomTag.ImageOrientationPatient, 1m, 0m, 0m, 0m, 1m, 0m);
        ds.Add(DicomTag.PixelSpacing, (decimal)Math.Round(spacing, 4), (decimal)Math.Round(spacing, 4));
        ds.Add(DicomTag.KVP, 120m);
        ds.Add(DicomTag.RescaleIntercept, -1024m);
        ds.Add(DicomTag.RescaleSlope, 1m);
        ds.Add(DicomTag.RescaleType, "HU");
        ds.Add(DicomTag.WindowCenter, (decimal)preset.Center);
        ds.Add(DicomTag.WindowWidth, (decimal)preset.Width);
        ds.Add(DicomTag.SamplesPerPixel, (ushort)1);
        ds.Add(DicomTag.PhotometricInterpretation, PhotometricInterpretation.Monochrome2.Value);
        ds.Add(DicomTag.Rows, (ushort)size);
        ds.Add(DicomTag.Columns, (ushort)size);
        ds.Add(DicomTag.BitsAllocated, (ushort)16);
        ds.Add(DicomTag.BitsStored, (ushort)16);
        ds.Add(DicomTag.HighBit, (ushort)15);
        ds.Add(DicomTag.PixelRepresentation, (ushort)1);
        var pd = DicomPixelData.Create(ds, true);
        var bytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        pd.AddFrame(new MemoryByteBuffer(bytes));
        return new DicomFile(ds);
    }

    /// <summary>Deterministic DICOM UID under the UUID-derived root 2.25 (PS3.5 B.2).</summary>
    public static string Uid(string name)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        var big = new BigInteger(h.AsSpan(0, 16), isUnsigned: true, isBigEndian: true);
        return "2.25." + big.ToString();
    }
}
