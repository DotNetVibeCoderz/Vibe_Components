using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.NativeCodec;

namespace Medical.Dicom;

/// <summary>Transfer syntaxes Medical.Net can write (all can be read).</summary>
public enum DicomCompression
{
    /// <summary>Explicit VR Little Endian (uncompressed).</summary>
    None,
    /// <summary>RLE Lossless.</summary>
    Rle,
    /// <summary>JPEG Lossless, Non-Hierarchical, First-Order Prediction (Process 14, SV1).</summary>
    JpegLossless,
    /// <summary>JPEG-LS Lossless.</summary>
    JpegLsLossless,
    /// <summary>JPEG-LS near-lossless (bounded error, see <see cref="DicomCodecs.Transcode"/>).</summary>
    JpegLsNearLossless,
    /// <summary>JPEG 2000 Lossless Only.</summary>
    Jpeg2000Lossless,
    /// <summary>JPEG 2000 (lossy).</summary>
    Jpeg2000,
    /// <summary>High-Throughput JPEG 2000 Lossless Only.</summary>
    HtJpeg2000Lossless,
}

/// <summary>
/// Compressed transfer syntaxes through the fo-dicom native codecs (<c>fo-dicom.Codecs</c>: libijg, CharLS, OpenJPEG,
/// OpenJPH; Windows / Linux / macOS, x64 and Arm64). <see cref="DicomImaging"/> decompresses encapsulated pixel data on
/// demand, so rendering, statistics, volumes and AI accept JPEG / JPEG-LS / JPEG 2000 / RLE series directly.
/// </summary>
public static class DicomCodecs
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>
    /// Makes sure fo-dicom uses the native transcoder manager. Leaves an existing setup alone when it can already
    /// decode JPEG-LS (e.g. the host registered <c>AddTranscoderManager&lt;NativeTranscoderManager&gt;()</c> itself).
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            if (!CanEncodeJpegLs())
                new DicomSetupBuilder()
                    .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<NativeTranscoderManager>())
                    .SkipValidation()
                    .Build();
            _registered = true;
        }
    }

    /// <summary>True when the native codecs load on this machine (Windows also needs the VC++ 14 runtime).</summary>
    public static bool IsAvailable
    {
        get
        {
            try { EnsureRegistered(); return CanEncodeJpegLs(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Probes the active fo-dicom transcoder with a 32×32 image (CharLS needs room for its header) (fo-dicom keeps its service provider internal).</summary>
    private static bool CanEncodeJpegLs()
    {
        try
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SamplesPerPixel, (ushort)1 }, { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
                { DicomTag.Rows, (ushort)32 }, { DicomTag.Columns, (ushort)32 }, { DicomTag.BitsAllocated, (ushort)16 },
                { DicomTag.BitsStored, (ushort)16 }, { DicomTag.HighBit, (ushort)15 }, { DicomTag.PixelRepresentation, (ushort)0 },
            };
            var pd = FellowOakDicom.Imaging.DicomPixelData.Create(ds, true);
            pd.AddFrame(new FellowOakDicom.IO.Buffer.MemoryByteBuffer(new byte[32 * 32 * 2]));
            var enc = new DicomTranscoder(ds.InternalTransferSyntax, DicomTransferSyntax.JPEGLSLossless).Transcode(ds);
            return enc.InternalTransferSyntax == DicomTransferSyntax.JPEGLSLossless;
        }
        catch (Exception) { return false; }
    }

    public static DicomTransferSyntax SyntaxFor(DicomCompression c) => c switch
    {
        DicomCompression.Rle => DicomTransferSyntax.RLELossless,
        DicomCompression.JpegLossless => DicomTransferSyntax.JPEGProcess14SV1,
        DicomCompression.JpegLsLossless => DicomTransferSyntax.JPEGLSLossless,
        DicomCompression.JpegLsNearLossless => DicomTransferSyntax.JPEGLSNearLossless,
        DicomCompression.Jpeg2000Lossless => DicomTransferSyntax.JPEG2000Lossless,
        DicomCompression.Jpeg2000 => DicomTransferSyntax.JPEG2000Lossy,
        DicomCompression.HtJpeg2000Lossless => DicomTransferSyntax.Lookup(DicomUID.Parse("1.2.840.10008.1.2.4.201")),
        _ => DicomTransferSyntax.ExplicitVRLittleEndian,
    };

    /// <summary>
    /// Transcodes a file. <paramref name="quality"/> applies to lossy syntaxes: the JPEG 2000 compression ratio target
    /// (default 10:1) or the JPEG-LS near-lossless allowed error in stored values (default 2).
    /// </summary>
    public static DicomFile Transcode(DicomFile file, DicomCompression target, int? quality = null)
    {
        EnsureRegistered();
        DicomCodecParams? p = target switch
        {
            DicomCompression.Jpeg2000 => new FellowOakDicom.Imaging.NativeCodec.DicomJpeg2000Params { Irreversible = true, Rate = quality ?? 10 },
            DicomCompression.JpegLsNearLossless => new DicomJpegLsCodec.DicomJpegLsParams { AllowedError = quality ?? 2 },
            _ => null,
        };
        var transcoder = new DicomTranscoder(file.Dataset.InternalTransferSyntax, SyntaxFor(target), outputCodecParams: p);
        return transcoder.Transcode(file);
    }

    /// <summary>Returns the dataset unchanged when it is not encapsulated, otherwise an Explicit VR Little Endian copy.</summary>
    public static DicomDataset Decompress(DicomDataset ds)
    {
        if (!ds.InternalTransferSyntax.IsEncapsulated) return ds;
        EnsureRegistered();
        return new DicomTranscoder(ds.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(ds);
    }

    /// <inheritdoc cref="Decompress(DicomDataset)"/>
    public static DicomFile Decompress(DicomFile file) =>
        file.Dataset.InternalTransferSyntax.IsEncapsulated ? Transcode(file, DicomCompression.None) : file;

    /// <summary>Size of the encoded pixel data in bytes (all frames, fragments included).</summary>
    public static long PixelDataBytes(DicomDataset ds)
    {
        if (!ds.TryGetValue<FellowOakDicom.IO.Buffer.IByteBuffer>(DicomTag.PixelData, 0, out _) && !ds.Contains(DicomTag.PixelData)) return 0;
        var item = ds.GetDicomItem<DicomItem>(DicomTag.PixelData);
        return item switch
        {
            DicomFragmentSequence fs => fs.Fragments.Sum(f => (long)f.Size),
            DicomElement e => e.Buffer.Size,
            _ => 0,
        };
    }
}
