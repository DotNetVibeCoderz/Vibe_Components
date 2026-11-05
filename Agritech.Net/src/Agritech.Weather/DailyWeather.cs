namespace Agritech.Weather;

/// <summary>Daily weather record. Missing optional variables are estimated by FAO-56 procedures when needed.</summary>
public sealed record DailyWeather(
    DateOnly Date,
    double TminC,
    double TmaxC,
    double RainMm,
    double? RhMeanPct = null,
    double? Wind2mMs = null,
    double? SolarRadMjM2 = null,
    double? Et0Mm = null)
{
    public double TmeanC => (TminC + TmaxC) / 2;
    public int DayOfYear => Date.DayOfYear;
}

/// <summary>Location metadata required for ET0 computations.</summary>
public readonly record struct WeatherStation(double LatitudeDeg, double ElevationM, string Name = "station");
