using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GnuRadio;

/// <summary>
/// Single-precision complex sample, binary compatible with GNU Radio's <c>gr_complex</c>
/// (<c>std::complex&lt;float&gt;</c>): two interleaved 32-bit floats, real first.
/// Files and network streams written by GNU Radio can be read straight into spans of this type.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Complex32 :
    IEquatable<Complex32>,
    IAdditionOperators<Complex32, Complex32, Complex32>,
    ISubtractionOperators<Complex32, Complex32, Complex32>,
    IMultiplyOperators<Complex32, Complex32, Complex32>,
    IDivisionOperators<Complex32, Complex32, Complex32>,
    IUnaryNegationOperators<Complex32, Complex32>,
    IEqualityOperators<Complex32, Complex32, bool>,
    IAdditiveIdentity<Complex32, Complex32>,
    IMultiplicativeIdentity<Complex32, Complex32>
{
    public readonly float Real;
    public readonly float Imaginary;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Complex32(float real, float imaginary)
    {
        Real = real;
        Imaginary = imaginary;
    }

    public static Complex32 Zero => default;
    public static Complex32 One => new(1f, 0f);
    public static Complex32 ImaginaryOne => new(0f, 1f);
    public static Complex32 AdditiveIdentity => default;
    public static Complex32 MultiplicativeIdentity => One;

    public float MagnitudeSquared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Real * Real + Imaginary * Imaginary;
    }

    public float Magnitude => MathF.Sqrt(MagnitudeSquared);

    /// <summary>Phase angle in radians, in (-π, π].</summary>
    public float Phase => MathF.Atan2(Imaginary, Real);

    public Complex32 Conjugate()
    {
        return new Complex32(Real, -Imaginary);
    }

    /// <summary>e^(jθ) scaled by <paramref name="magnitude"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 FromPolar(float magnitude, float phase)
    {
        var (s, c) = MathF.SinCos(phase);
        return new Complex32(magnitude * c, magnitude * s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator +(Complex32 a, Complex32 b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator -(Complex32 a, Complex32 b) => new(a.Real - b.Real, a.Imaginary - b.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator -(Complex32 a) => new(-a.Real, -a.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(Complex32 a, Complex32 b) =>
        new(a.Real * b.Real - a.Imaginary * b.Imaginary, a.Real * b.Imaginary + a.Imaginary * b.Real);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(Complex32 a, float b) => new(a.Real * b, a.Imaginary * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator *(float a, Complex32 b) => new(a * b.Real, a * b.Imaginary);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 operator /(Complex32 a, float b) => new(a.Real / b, a.Imaginary / b);

    public static Complex32 operator /(Complex32 a, Complex32 b)
    {
        var d = b.MagnitudeSquared;
        return new Complex32((a.Real * b.Real + a.Imaginary * b.Imaginary) / d,
                             (a.Imaginary * b.Real - a.Real * b.Imaginary) / d);
    }

    public static bool operator ==(Complex32 left, Complex32 right) => left.Equals(right);
    public static bool operator !=(Complex32 left, Complex32 right) => !left.Equals(right);

    public static implicit operator Complex32(float real) => new(real, 0f);
    public static implicit operator Complex(Complex32 value) => new(value.Real, value.Imaginary);
    public static explicit operator Complex32(Complex value) => new((float)value.Real, (float)value.Imaginary);

    public bool Equals(Complex32 other) => Real.Equals(other.Real) && Imaginary.Equals(other.Imaginary);
    public override bool Equals(object? obj) => obj is Complex32 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Real, Imaginary);

    public override string ToString() =>
        Imaginary < 0 ? $"({Real:G6} - {-Imaginary:G6}j)" : $"({Real:G6} + {Imaginary:G6}j)";

    public void Deconstruct(out float real, out float imaginary)
    {
        real = Real;
        imaginary = Imaginary;
    }
}
