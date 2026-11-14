using System.Numerics;
using System.Runtime.CompilerServices;

namespace Electrical.Core.Numerics;

/// <summary>Helpers shared by the generic (double / Complex) linear algebra kernels.</summary>
public static class Scalar
{
    /// <summary>Magnitude |x| for double or <see cref="Complex"/> without boxing (JIT specializes per T).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Magnitude<T>(T x) where T : INumberBase<T>
    {
        if (typeof(T) == typeof(double)) return Math.Abs(Unsafe.As<T, double>(ref x));
        if (typeof(T) == typeof(Complex)) return Unsafe.As<T, Complex>(ref x).Magnitude;
        if (typeof(T) == typeof(float)) return Math.Abs(Unsafe.As<T, float>(ref x));
        throw new NotSupportedException($"Scalar type {typeof(T).Name} is not supported (use double, float or Complex).");
    }

    /// <summary>Wraps an angle in radians to (-π, π].</summary>
    public static double WrapAngle(double rad)
    {
        rad %= 2 * Math.PI;
        if (rad <= -Math.PI) rad += 2 * Math.PI;
        else if (rad > Math.PI) rad -= 2 * Math.PI;
        return rad;
    }

    public const double Deg2Rad = Math.PI / 180.0;
    public const double Rad2Deg = 180.0 / Math.PI;
}
