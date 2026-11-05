namespace Agritech.Core;

/// <summary>Unit conversions commonly needed in agronomy.</summary>
public static class Units
{
    public const double M2PerHectare = 10_000;
    public const double M2PerAcre = 4046.8564224;
    public const double KgPerTonne = 1000;

    public static double CelsiusToFahrenheit(double c) => c * 9 / 5 + 32;
    public static double FahrenheitToCelsius(double f) => (f - 32) * 5 / 9;
    public static double CelsiusToKelvin(double c) => c + 273.15;
    public static double HectaresToAcres(double ha) => ha * M2PerHectare / M2PerAcre;
    public static double AcresToHectares(double ac) => ac * M2PerAcre / M2PerHectare;
    public static double KgPerHaToLbPerAcre(double kgHa) => kgHa * 0.892179;
    /// <summary>Millimetres of water over one hectare to cubic metres (1 mm·ha = 10 m³).</summary>
    public static double MmToCubicMetresPerHa(double mm, double hectares = 1) => mm * 10 * hectares;
    public static double KmhToMs(double kmh) => kmh / 3.6;
    /// <summary>Converts wind speed measured at height z (m) to 2 m (FAO-56 eq. 47).</summary>
    public static double WindAt2m(double uz, double zMetres) => uz * 4.87 / Math.Log(67.8 * zMetres - 5.42);
    /// <summary>Radiation: W/m² average over a day to MJ/m²/day.</summary>
    public static double WattsToMjPerDay(double wm2) => wm2 * 0.0864;
    /// <summary>Elemental to oxide forms: P → P2O5 and K → K2O.</summary>
    public static double PToP2O5(double p) => p * 2.2914;
    public static double KToK2O(double k) => k * 1.2046;
    public static double P2O5ToP(double p2o5) => p2o5 / 2.2914;
    public static double K2OToK(double k2o) => k2o / 1.2046;
}
