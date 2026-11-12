namespace AquaCulture.Core;

/// <summary>
/// Unit conventions used across AquaCulture.Net: °C, mg/L, ppt (g/kg) salinity, g for individual weight, kg for biomass and feed,
/// m² / m³ for pond geometry, days for culture time, IDR (or any currency) per kg for prices.
/// </summary>
public static class Units
{
    public const double KelvinOffset = 273.15;
    public static double CToF(double c) => c * 9 / 5 + 32;
    public static double FToC(double f) => (f - 32) * 5 / 9;
    public static double CToK(double c) => c + KelvinOffset;
    public static double HectareToM2(double ha) => ha * 10_000;
    public static double M2ToHectare(double m2) => m2 / 10_000;
    /// <summary>Electrical conductivity (µS/cm) to TDS (mg/L) with the usual 0.64 factor.</summary>
    public static double EcToTds(double ecUsCm, double factor = 0.64) => ecUsCm * factor;
    /// <summary>Approximate practical salinity (ppt) from EC (µS/cm at 25 °C), seawater-dominated ions.</summary>
    public static double EcToSalinity(double ecUsCm) => ecUsCm <= 0 ? 0 : 0.4665 * Math.Pow(ecUsCm / 1000.0, 1.0878);
    /// <summary>Barometric pressure (kPa) at an altitude (m), standard atmosphere.</summary>
    public static double PressureAtAltitudeKPa(double altitudeM) => 101.325 * Math.Pow(1 - 2.25577e-5 * altitudeM, 5.25588);
    /// <summary>Total ammonia nitrogen to total ammonia (NH₃+NH₄⁺ as NH₃).</summary>
    public static double TanToNh3(double tan) => tan * 17.031 / 14.007;
    /// <summary>Nitrite-N to nitrite (NO₂⁻).</summary>
    public static double NitriteNToNitrite(double no2n) => no2n * 46.005 / 14.007;
    public static double KwhCost(double kw, double hours, double pricePerKwh) => kw * hours * pricePerKwh;
}
