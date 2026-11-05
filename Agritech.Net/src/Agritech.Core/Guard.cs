using System.Runtime.CompilerServices;

namespace Agritech.Core;

/// <summary>Lightweight argument validation helpers.</summary>
public static class Guard
{
    public static double InRange(double value, double min, double max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (double.IsNaN(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Value must be within [{min}, {max}].");
        return value;
    }

    public static double NonNegative(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (double.IsNaN(value) || value < 0) throw new ArgumentOutOfRangeException(name, value, "Value must be non-negative.");
        return value;
    }

    public static double Positive(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (double.IsNaN(value) || value <= 0) throw new ArgumentOutOfRangeException(name, value, "Value must be positive.");
        return value;
    }

    public static void SameLength(int a, int b, string? message = null)
    {
        if (a != b) throw new ArgumentException(message ?? $"Length mismatch ({a} vs {b}).");
    }
}
