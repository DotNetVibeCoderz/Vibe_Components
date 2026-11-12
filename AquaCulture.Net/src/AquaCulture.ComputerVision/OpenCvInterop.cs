using System.Runtime.InteropServices;
using AquaCulture.Core.Hosting;
using OpenCvSharp;

namespace AquaCulture.ComputerVision;

/// <summary>
/// OpenCvSharp interop (camera capture, file decoding, advanced filtering). Add a native runtime package for your platform,
/// e.g. <c>OpenCvSharp4.runtime.win</c> or <c>OpenCvSharp4.official.runtime.linux-x64</c>.
/// </summary>
public static class OpenCvInterop
{
    /// <summary>Converts an 8-bit BGR <see cref="Mat"/> to <see cref="RgbImage"/>.</summary>
    public static RgbImage FromMat(Mat mat)
    {
        using var bgr = mat.Type() == MatType.CV_8UC3 ? mat.Clone() : mat.CvtColor(mat.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        int width = bgr.Width, height = bgr.Height;
        var buf = new byte[width * height * 3];
        if (bgr.IsContinuous()) Marshal.Copy(bgr.Data, buf, 0, buf.Length);
        else for (int y = 0; y < height; y++) Marshal.Copy(bgr.Ptr(y), buf, y * width * 3, width * 3);
        return RgbImage.FromBgr(width, height, buf);
    }

    /// <summary>Converts to an 8-bit BGR <see cref="Mat"/>.</summary>
    public static Mat ToMat(RgbImage img)
    {
        var mat = new Mat(img.Height, img.Width, MatType.CV_8UC3);
        var bgr = new byte[img.Data.Length];
        for (int i = 0; i < img.Width * img.Height; i++) { bgr[i * 3] = img.Data[i * 3 + 2]; bgr[i * 3 + 1] = img.Data[i * 3 + 1]; bgr[i * 3 + 2] = img.Data[i * 3]; }
        Marshal.Copy(bgr, 0, mat.Data, bgr.Length);
        return mat;
    }

    /// <summary>Loads an image file (JPEG, PNG, BMP, TIFF…) through OpenCV.</summary>
    public static RgbImage Load(string path)
    {
        using var m = Cv2.ImRead(path, ImreadModes.Color);
        if (m.Empty()) throw new FileNotFoundException("Could not read image.", path);
        return FromMat(m);
    }

    public static void Save(RgbImage img, string path)
    {
        using var m = ToMat(img);
        Cv2.ImWrite(path, m);
    }

    /// <summary>Grabs one frame from a camera or RTSP stream (e.g. an underwater camera).</summary>
    public static RgbImage? Grab(string source)
    {
        using var cap = int.TryParse(source, out var idx) ? new VideoCapture(idx) : new VideoCapture(source);
        using var frame = new Mat();
        return cap.Read(frame) && !frame.Empty() ? FromMat(frame) : null;
    }
}

public static class ComputerVisionBuilderExtensions
{
    public static AquaBuilder AddComputerVision(this AquaBuilder b) => b.TryAddModule("ComputerVision", _ => { });
}
