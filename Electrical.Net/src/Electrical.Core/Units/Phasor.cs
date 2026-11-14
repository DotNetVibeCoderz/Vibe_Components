using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Core.Units;

/// <summary>Phasor helpers on <see cref="Complex"/> (angles in degrees unless stated).</summary>
public static class Phasor
{
    public static Complex FromPolarDeg(double magnitude, double angleDeg) =>
        Complex.FromPolarCoordinates(magnitude, angleDeg * Scalar.Deg2Rad);

    public static double AngleDeg(this Complex z) => z.Phase * Scalar.Rad2Deg;

    public static string ToPolarString(this Complex z, string format = "0.0000") =>
        $"{z.Magnitude.ToString(format)}∠{z.AngleDeg():0.00}°";

    /// <summary>Complex power S = V·I* .</summary>
    public static Complex Power(Complex v, Complex i) => v * Complex.Conjugate(i);
}

/// <summary>A three-phase set of phasors (a, b, c).</summary>
public readonly record struct ThreePhase(Complex A, Complex B, Complex C)
{
    public static ThreePhase Balanced(double magnitude, double angleDeg = 0) => new(
        Phasor.FromPolarDeg(magnitude, angleDeg),
        Phasor.FromPolarDeg(magnitude, angleDeg - 120),
        Phasor.FromPolarDeg(magnitude, angleDeg + 120));

    public Complex this[int phase] => phase switch { 0 => A, 1 => B, 2 => C, _ => throw new ArgumentOutOfRangeException(nameof(phase)) };

    /// <summary>Voltage unbalance factor (negative / positive sequence magnitude) per IEC 61000-4-30.</summary>
    public double UnbalanceFactor()
    {
        var s = SymmetricalComponents.FromPhases(this);
        return s.Positive.Magnitude < 1e-12 ? 0 : s.Negative.Magnitude / s.Positive.Magnitude;
    }
}

/// <summary>Sequence components (zero, positive, negative).</summary>
public readonly record struct SequenceSet(Complex Zero, Complex Positive, Complex Negative);

/// <summary>Fortescue transform between phase and sequence quantities.</summary>
public static class SymmetricalComponents
{
    /// <summary>The operator a = 1∠120°.</summary>
    public static readonly Complex A = Complex.FromPolarCoordinates(1, 2 * Math.PI / 3);
    public static readonly Complex A2 = A * A;

    public static SequenceSet FromPhases(ThreePhase p) => new(
        (p.A + p.B + p.C) / 3,
        (p.A + A * p.B + A2 * p.C) / 3,
        (p.A + A2 * p.B + A * p.C) / 3);

    public static ThreePhase ToPhases(SequenceSet s) => new(
        s.Zero + s.Positive + s.Negative,
        s.Zero + A2 * s.Positive + A * s.Negative,
        s.Zero + A * s.Positive + A2 * s.Negative);
}
