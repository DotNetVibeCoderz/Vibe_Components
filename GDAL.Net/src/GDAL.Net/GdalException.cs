namespace GDAL.Net;

/// <summary>Raised when a GDAL/OGR/OSR call fails. Carries GDAL's own error message and error number.</summary>
public class GdalException : Exception
{
    public GdalException(string message, int errorNumber = 0) : base(message)
    {
        ErrorNumber = errorNumber;
    }

    public GdalException(string message, Exception inner) : base(message, inner) { }

    /// <summary>GDAL CPLErrorNum (CPLE_*) or OGRErr code.</summary>
    public int ErrorNumber { get; }
}

/// <summary>Progress report of a long running GDAL operation.</summary>
/// <param name="Fraction">Completion in [0, 1].</param>
/// <param name="Message">Optional message emitted by GDAL.</param>
public readonly record struct GdalProgress(double Fraction, string? Message)
{
    public double Percent => Fraction * 100.0;
}

/// <summary>A message emitted by GDAL's error/log handler.</summary>
public readonly record struct GdalLogMessage(GdalLogLevel Level, int ErrorNumber, string Message);

public enum GdalLogLevel
{
    None = 0,
    Debug = 1,
    Warning = 2,
    Failure = 3,
    Fatal = 4,
}
