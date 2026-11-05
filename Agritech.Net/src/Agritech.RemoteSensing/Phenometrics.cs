using Agritech.Core.Data;

namespace Agritech.RemoteSensing;

/// <summary>Land-surface phenology metrics derived from an index time series.</summary>
public sealed record PhenologyMetrics(
    DateOnly? StartOfSeason, DateOnly? PeakDate, DateOnly? EndOfSeason,
    double PeakValue, double BaseValue, double Amplitude, double IntegratedValue, int SeasonLengthDays);

public static class Phenometrics
{
    /// <summary>
    /// Computes SOS/EOS using the amplitude-threshold method (TIMESAT-like) on a smoothed series.
    /// </summary>
    /// <param name="series">(date, index value) observations; NaNs (clouds) are skipped.</param>
    /// <param name="thresholdFraction">Fraction of amplitude above base defining season start/end (default 0.5).</param>
    public static PhenologyMetrics Compute(IEnumerable<(DateOnly Date, double Value)> series, double thresholdFraction = 0.5)
    {
        var obs = series.Where(s => !double.IsNaN(s.Value)).OrderBy(s => s.Date).ToList();
        if (obs.Count < 3) return new PhenologyMetrics(null, null, null, double.NaN, double.NaN, 0, 0, 0);

        var smooth = TimeSeriesExtensions.CenteredMovingAverage(obs.Select(o => o.Value).ToArray(), 3);
        int peak = Array.IndexOf(smooth, smooth.Max());
        double baseLeft = smooth[..(peak + 1)].Min();
        double baseRight = smooth[peak..].Min();
        double baseV = (baseLeft + baseRight) / 2;
        double amp = smooth[peak] - baseV;
        double thrL = baseLeft + thresholdFraction * (smooth[peak] - baseLeft);
        double thrR = baseRight + thresholdFraction * (smooth[peak] - baseRight);

        int sos = peak; while (sos > 0 && smooth[sos - 1] >= thrL) sos--;
        int eos = peak; while (eos < smooth.Length - 1 && smooth[eos + 1] >= thrR) eos++;

        double integral = 0;
        for (int i = sos; i < eos; i++)
            integral += (smooth[i] + smooth[i + 1]) / 2 * (obs[i + 1].Date.DayNumber - obs[i].Date.DayNumber);

        return new PhenologyMetrics(obs[sos].Date, obs[peak].Date, obs[eos].Date, Math.Round(smooth[peak], 4), Math.Round(baseV, 4),
            Math.Round(amp, 4), Math.Round(integral, 2), obs[eos].Date.DayNumber - obs[sos].Date.DayNumber);
    }
}
