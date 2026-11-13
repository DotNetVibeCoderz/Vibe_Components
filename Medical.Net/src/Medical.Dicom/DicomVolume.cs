using System.Globalization;
using FellowOakDicom;
using Medical.Core;
using Medical.Native;

namespace Medical.Dicom;

/// <summary>
/// A CT / MR series as a 3D volume of rescaled values (Hounsfield units for CT), x fastest. Geometry follows the DICOM
/// patient coordinate system (LPS, millimetres): voxel (i, j, k) sits at <c>Origin + i·Sx·Row + j·Sy·Column + k·Sz·Slice</c>.
/// </summary>
public sealed class DicomVolume
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Depth { get; init; }
    /// <summary>Rescaled values (HU for CT), clamped to the 16-bit range; index <c>k·W·H + j·W + i</c>.</summary>
    public required short[] Voxels { get; init; }
    /// <summary>Voxel size in mm: column spacing (x), row spacing (y), slice spacing (z).</summary>
    public required Vector3d Spacing { get; init; }
    /// <summary>Patient position (LPS, mm) of the first voxel of the first slice.</summary>
    public required Vector3d Origin { get; init; }
    public Vector3d RowDirection { get; init; } = new(1, 0, 0);
    public Vector3d ColumnDirection { get; init; } = new(0, 1, 0);
    public Vector3d SliceDirection { get; init; } = new(0, 0, 1);
    /// <summary>SOP Instance UID of each slice, in volume order (for SEG / SR references).</summary>
    public IReadOnlyList<string> SliceSopInstanceUids { get; init; } = [];
    public string SopClassUid { get; init; } = DicomUID.CTImageStorage.UID;
    public string StudyInstanceUid { get; init; } = "";
    public string SeriesInstanceUid { get; init; } = "";
    public string? FrameOfReferenceUid { get; init; }
    /// <summary>The first slice's dataset (patient / study modules are copied from it into derived objects).</summary>
    public DicomDataset? Reference { get; init; }

    public short this[int i, int j, int k] => Voxels[(k * Height + j) * Width + i];

    /// <summary>Patient coordinates (mm) of fractional voxel coordinates.</summary>
    public Vector3d ToPatient(double i, double j, double k) =>
        Origin + RowDirection * (i * Spacing.X) + ColumnDirection * (j * Spacing.Y) + SliceDirection * (k * Spacing.Z);

    /// <summary>Fractional voxel coordinates of a patient point (inverse of <see cref="ToPatient"/>; handles non-orthogonal stacks).</summary>
    public Vector3d ToVoxel(Vector3d p)
    {
        var a = RowDirection * Spacing.X; var b = ColumnDirection * Spacing.Y; var c = SliceDirection * Spacing.Z;
        var d = p - Origin;
        double det = Vector3d.Dot(a, Vector3d.Cross(b, c));
        return new(Vector3d.Dot(d, Vector3d.Cross(b, c)) / det, Vector3d.Dot(a, Vector3d.Cross(d, c)) / det, Vector3d.Dot(a, Vector3d.Cross(b, d)) / det);
    }

    /// <summary>Voxel volume in mm³.</summary>
    public double VoxelVolumeMm3 => Spacing.X * Spacing.Y * Spacing.Z;

    /// <summary>
    /// Builds a volume from the instances of one series (any order; compressed syntaxes are decoded). Slices are sorted
    /// along the slice normal using Image Position (Patient); the slice spacing is the median gap between positions.
    /// </summary>
    public static DicomVolume FromSeries(IEnumerable<DicomFile> files)
    {
        var slices = files.Select(f => f.Dataset).ToList();
        if (slices.Count == 0) throw new ArgumentException("The series is empty.", nameof(files));
        var first = DicomImaging.Describe(slices[0]);
        double[] iop = slices[0].TryGetValues<double>(DicomTag.ImageOrientationPatient, out double[]? o) && o.Length == 6 ? o : [1, 0, 0, 0, 1, 0];
        var row = new Vector3d(iop[0], iop[1], iop[2]).Normalized();
        var col = new Vector3d(iop[3], iop[4], iop[5]).Normalized();
        var normal = Vector3d.Cross(row, col).Normalized();

        Vector3d Ipp(DicomDataset ds, int fallback) =>
            ds.TryGetValues<double>(DicomTag.ImagePositionPatient, out double[]? p) && p.Length == 3 ? new(p[0], p[1], p[2])
            : normal * (ds.TryGetSingleValue(DicomTag.SliceLocation, out double sl) ? sl : fallback * (first.SliceThickness ?? 1));

        var ordered = slices.Select((ds, idx) => (ds, pos: Ipp(ds, ds.TryGetSingleValue(DicomTag.InstanceNumber, out int n) ? n : idx)))
            .OrderBy(s => Vector3d.Dot(s.pos, normal)).ToList();
        foreach (var (ds, _) in ordered)
        {
            var info = DicomImaging.Describe(ds);
            if (info.Rows != first.Rows || info.Columns != first.Columns)
                throw new InvalidOperationException($"Slice {info.SopInstanceUid} is {info.Columns}×{info.Rows}; expected {first.Columns}×{first.Rows}.");
        }
        var gaps = ordered.Zip(ordered.Skip(1), (a, b) => Vector3d.Dot(b.pos - a.pos, normal)).Where(g => g > 1e-6).OrderBy(g => g).ToList();
        double sz = gaps.Count > 0 ? gaps[gaps.Count / 2] : first.SliceThickness ?? 1;
        Vector3d sliceDir = ordered.Count > 1 && (ordered[^1].pos - ordered[0].pos).Length > 1e-6 ? (ordered[^1].pos - ordered[0].pos).Normalized() : normal;

        int w = first.Columns, h = first.Rows, plane = w * h;
        var vox = new short[plane * ordered.Count];
        for (int k = 0; k < ordered.Count; k++)
        {
            var ds = DicomCodecs.Decompress(ordered[k].ds);
            var info = DicomImaging.Describe(ds);
            var dst = vox.AsSpan(k * plane, plane);
            if (info.Signed)
            {
                var raw = DicomImaging.FrameI16(ds);
                for (int i = 0; i < plane; i++) dst[i] = Clamp(raw[i] * info.RescaleSlope + info.RescaleIntercept);
            }
            else
            {
                var raw = DicomImaging.FrameU16(ds);
                for (int i = 0; i < plane; i++) dst[i] = Clamp(raw[i] * info.RescaleSlope + info.RescaleIntercept);
            }
        }
        return new DicomVolume
        {
            Width = w, Height = h, Depth = ordered.Count, Voxels = vox,
            Spacing = new(first.PixelSpacingColumn ?? 1, first.PixelSpacingRow ?? 1, sz),
            Origin = ordered[0].pos, RowDirection = row, ColumnDirection = col, SliceDirection = sliceDir,
            SliceSopInstanceUids = ordered.Select(s => s.ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "")).ToList(),
            SopClassUid = first.SopClassUid, StudyInstanceUid = first.StudyInstanceUid, SeriesInstanceUid = first.SeriesInstanceUid,
            FrameOfReferenceUid = slices[0].GetSingleValueOrDefault<string?>(DicomTag.FrameOfReferenceUID, null),
            Reference = ordered[0].ds,
        };
    }

    private static short Clamp(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return r < short.MinValue ? short.MinValue : r > short.MaxValue ? short.MaxValue : (short)r;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height}×{Depth} voxels, {Spacing.X:0.###}×{Spacing.Y:0.###}×{Spacing.Z:0.###} mm");
}

/// <summary>Standard reformatting planes, in radiological display orientation.</summary>
public enum VolumePlane
{
    /// <summary>Viewed from the feet: patient's right on the image left, anterior at the top.</summary>
    Axial,
    /// <summary>Viewed from the front: patient's right on the image left, head at the top.</summary>
    Coronal,
    /// <summary>Viewed from the patient's left: anterior on the image left, head at the top.</summary>
    Sagittal,
}

/// <summary>A reformatted image in rescaled units with its physical pixel size.</summary>
public sealed record VolumeImage(int Width, int Height, short[] Values, double PixelSpacingMm, string Description, KernelBackend Backend)
{
    /// <summary>Applies a VOI window (Rust kernel when available).</summary>
    public RenderedImage Render(WindowPreset window)
    {
        var gray = new byte[Values.Length];
        MedicalKernels.WindowLevel(Values, gray, 1, 0, window.Center, window.Width);
        return new RenderedImage(Width, Height, gray, window);
    }

    public byte[] ToPng(WindowPreset window) => Render(window).ToPng();
}

/// <summary>
/// Multiplanar reformation (MPR) and slab projections (MIP, MinIP, AvgIP) on <see cref="MedicalKernels.Slab"/> (Rust
/// kernel with a bit-identical managed twin). Output pixels are square with the finest in-plane spacing, so coronal and
/// sagittal reformats of thick-slice series keep correct proportions.
/// </summary>
public static class VolumeRendering
{
    private static (Vector3d U, Vector3d V, Vector3d N) Axes(VolumePlane plane) => plane switch
    {
        // LPS: +X left, +Y posterior, +Z head.
        VolumePlane.Axial => (new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),
        VolumePlane.Coronal => (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),
        _ => (new(0, 1, 0), new(0, 0, -1), new(1, 0, 0)),
    };

    /// <summary>A single plane; <paramref name="position"/> is the fraction (0 … 1) along the plane normal.</summary>
    public static VolumeImage Mpr(DicomVolume volume, VolumePlane plane, double position = 0.5) =>
        Project(volume, plane, SlabMode.Mean, position, 0);

    /// <summary>
    /// A slab projection <paramref name="thicknessMm"/> thick centred at <paramref name="position"/> (0 … 1 along the
    /// normal). Thickness 0 is a plain MPR; <c>double.PositiveInfinity</c> projects through the whole volume.
    /// </summary>
    public static VolumeImage Project(DicomVolume volume, VolumePlane plane, SlabMode mode, double position = 0.5, double thicknessMm = double.PositiveInfinity)
    {
        var (u, v, n) = Axes(plane);
        return Resample(volume, u, v, n, position, thicknessMm, mode, plane.ToString());
    }

    /// <summary>
    /// Oblique reformat/projection: <paramref name="u"/> and <paramref name="v"/> are the image right and down directions
    /// in patient space (made orthonormal), the slab runs along their cross product.
    /// </summary>
    public static VolumeImage Oblique(DicomVolume volume, Vector3d u, Vector3d v, double position = 0.5, double thicknessMm = 0, SlabMode mode = SlabMode.Mean)
    {
        u = u.Normalized();
        v = (v - u * Vector3d.Dot(v, u)).Normalized();
        return Resample(volume, u, v, Vector3d.Cross(u, v).Normalized(), position, thicknessMm, mode, "Oblique");
    }

    private static VolumeImage Resample(DicomVolume vol, Vector3d u, Vector3d v, Vector3d n, double position, double thicknessMm, SlabMode mode, string name)
    {
        // Bounding box of the volume corners projected onto the output axes.
        var corners = new List<Vector3d>(8);
        foreach (int i in new[] { 0, vol.Width - 1 })
            foreach (int j in new[] { 0, vol.Height - 1 })
                foreach (int k in new[] { 0, vol.Depth - 1 })
                    corners.Add(vol.ToPatient(i, j, k));
        double Min(Vector3d axis) => corners.Min(c => Vector3d.Dot(c, axis));
        double Max(Vector3d axis) => corners.Max(c => Vector3d.Dot(c, axis));
        double pix = Math.Min(vol.Spacing.X, vol.Spacing.Y);
        int width = Math.Max(1, (int)Math.Floor((Max(u) - Min(u)) / pix + 1e-9) + 1);
        int height = Math.Max(1, (int)Math.Floor((Max(v) - Min(v)) / pix + 1e-9) + 1);
        // One sample per voxel layer along the dominant axis of the slab direction, so projections along an acquisition
        // axis sample the acquired voxels themselves (no interpolation hiding a maximum or minimum).
        var perMm = vol.ToVoxel(vol.Origin + n) - vol.ToVoxel(vol.Origin);
        double step = 1 / Math.Max(Math.Abs(perMm.X), Math.Max(Math.Abs(perMm.Y), Math.Abs(perMm.Z)));
        double nMin = Min(n), nMax = Max(n);
        bool whole = double.IsPositiveInfinity(thicknessMm);
        double center = whole ? (nMin + nMax) / 2 : nMin + Math.Round(Math.Clamp(position, 0, 1) * (nMax - nMin) / step) * step;
        double half = whole ? (nMax - nMin) / 2 : Math.Max(0, thicknessMm) / 2;
        int halfSteps = (int)Math.Floor(half / step + 1e-9);
        int steps = whole ? (int)Math.Floor((nMax - nMin) / step + 1e-9) + 1 : 2 * halfSteps + 1;
        double start = whole ? nMin : center - halfSteps * step;
        half = (steps - 1) * step / 2;

        // Patient start point, then everything into voxel coordinates for the kernel.
        var p0 = u * Min(u) + v * Min(v) + n * start;
        var o = vol.ToVoxel(p0);
        Vector3d Dir(Vector3d d, double mm) => vol.ToVoxel(p0 + d * mm) - o;
        var g = new SlabGeometry(o, Dir(u, pix), Dir(v, pix), Dir(n, step), steps, width, height);
        var output = new short[width * height];
        MedicalKernels.Slab(vol.Voxels, vol.Width, vol.Height, vol.Depth, g, mode, -1024, output);
        string what = steps == 1 ? "MPR" : mode switch { SlabMode.Max => "MIP", SlabMode.Min => "MinIP", _ => "AvgIP" };
        string desc = string.Create(CultureInfo.InvariantCulture, $"{name} {what}{(steps > 1 ? $" {2 * half:0.#} mm" : "")}, {width}×{height} @ {pix:0.###} mm");
        return new VolumeImage(width, height, output, pix, desc, MedicalKernels.SlabBackend);
    }
}
