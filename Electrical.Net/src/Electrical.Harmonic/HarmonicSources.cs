namespace Electrical.Harmonic;

/// <summary>One harmonic component of a source spectrum.</summary>
/// <param name="Order">Harmonic order h.</param>
/// <param name="MagnitudePercent">Magnitude [% of the fundamental current].</param>
/// <param name="PhaseDeg">Phase angle [degrees] relative to the fundamental at 0° (cosine reference).</param>
public readonly record struct HarmonicComponent(int Order, double MagnitudePercent, double PhaseDeg);

/// <summary>Current spectrum of a nonlinear load (harmonic source).</summary>
public sealed record HarmonicSourceSpectrum(string Name, IReadOnlyList<HarmonicComponent> Components)
{
    /// <summary>THD of the spectrum [%].</summary>
    public double ThdPercent => Math.Sqrt(Components.Where(c => c.Order >= 2).Sum(c => c.MagnitudePercent * c.MagnitudePercent));

    /// <summary>Spectrum object for a fundamental current [A or pu].</summary>
    public HarmonicSpectrum ToSpectrum(double fundamentalHz, double fundamentalCurrent) =>
        HarmonicSpectrum.FromPercent(fundamentalHz, fundamentalCurrent, Components.Select(c => (c.Order, c.MagnitudePercent, c.PhaseDeg)));
}

/// <summary>
/// Typical harmonic current spectra of common nonlinear loads. The ideal converter spectra are theoretical (I_h = I_1/h);
/// the others are representative published "typical" values for screening studies, not a substitute for measured data.
/// </summary>
public static class TypicalSpectra
{
    private static HarmonicSourceSpectrum Ideal(string name, int pulses, int maxOrder)
    {
        var list = new List<HarmonicComponent>();
        for (int k = 1; k * pulses - 1 <= maxOrder; k++)
        {
            // i(t) ∝ cos ωt − cos5ωt/5 + cos7ωt/7 − cos11ωt/11 + cos13ωt/13 …  (6-pulse);  12-pulse keeps h = 12k ± 1
            int lo = k * pulses - 1, hi = k * pulses + 1;
            list.Add(new HarmonicComponent(lo, 100.0 / lo, 180));
            if (hi <= maxOrder) list.Add(new HarmonicComponent(hi, 100.0 / hi, 0));
        }
        return new HarmonicSourceSpectrum(name, list);
    }

    /// <summary>Ideal six-pulse converter (square-wave current, I_h = I_1/h for h = 6k ± 1).</summary>
    public static HarmonicSourceSpectrum SixPulseIdeal(int maxOrder = 49) => Ideal("6-pulse converter (ideal)", 6, maxOrder);

    /// <summary>Ideal twelve-pulse converter (h = 12k ± 1).</summary>
    public static HarmonicSourceSpectrum TwelvePulseIdeal(int maxOrder = 49) => Ideal("12-pulse converter (ideal)", 12, maxOrder);

    /// <summary>Typical six-pulse thyristor/diode rectifier with commutation overlap (THD ≈ 26 %).</summary>
    public static HarmonicSourceSpectrum SixPulseRectifier() => new("6-pulse rectifier (typical)",
    [
        new(5, 19.1, 180), new(7, 13.1, 0), new(11, 7.2, 180), new(13, 5.6, 0), new(17, 3.3, 180), new(19, 2.4, 0),
        new(23, 1.2, 180), new(25, 0.8, 0),
    ]);

    /// <summary>Typical PWM variable-frequency drive with a six-pulse diode front end and DC-link choke (THD ≈ 34 %).</summary>
    public static HarmonicSourceSpectrum PwmDriveWithChoke() => new("PWM VFD with DC choke (typical)",
    [
        new(5, 30.0, 180), new(7, 12.0, 0), new(11, 7.0, 180), new(13, 5.0, 0), new(17, 3.0, 180), new(19, 2.5, 0),
        new(23, 1.5, 180), new(25, 1.2, 0),
    ]);

    /// <summary>Typical PWM drive without line reactor or choke (THD ≈ 85 %).</summary>
    public static HarmonicSourceSpectrum PwmDriveNoChoke() => new("PWM VFD without choke (typical)",
    [
        new(5, 70.0, 180), new(7, 40.0, 0), new(11, 10.0, 180), new(13, 7.0, 0), new(17, 5.0, 180), new(19, 4.0, 0),
        new(23, 2.5, 180), new(25, 2.0, 0),
    ]);

    /// <summary>Typical single-phase LED driver without active PFC (strong triplen content, THD ≈ 95 %).</summary>
    public static HarmonicSourceSpectrum LedDriver() => new("LED driver, no PFC (typical)",
    [
        new(3, 75.0, 180), new(5, 45.0, 0), new(7, 22.0, 180), new(9, 12.0, 0), new(11, 10.0, 180), new(13, 8.0, 0), new(15, 6.0, 180),
    ]);

    public static IReadOnlyList<HarmonicSourceSpectrum> All() =>
        [SixPulseIdeal(), TwelvePulseIdeal(), SixPulseRectifier(), PwmDriveWithChoke(), PwmDriveNoChoke(), LedDriver()];
}
