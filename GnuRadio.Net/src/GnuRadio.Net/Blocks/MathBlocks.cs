using System.Numerics;
using System.Runtime.InteropServices;
using GnuRadio.Kernels;

namespace GnuRadio.Blocks;

/// <summary>Sums N inputs element-wise (GNU Radio's <c>blocks.add_xx</c>).</summary>
public sealed class Add<T>(int inputs = 2, int vectorLength = 1)
    : SyncMultiInputBlock<T, T>("add", inputs, vectorLength)
    where T : unmanaged, IAdditionOperators<T, T, T>
{
    protected override void Work(WorkContext context, int items, Span<T> output)
    {
        var len = items * VectorLength;
        context.Input<T>(0)[..len].CopyTo(output);
        for (var p = 1; p < context.InputCount; p++)
        {
            var input = context.Input<T>(p)[..len];
            if (typeof(T) == typeof(float) || typeof(T) == typeof(Complex32))
            {
                var fo = MemoryMarshal.Cast<T, float>(output);
                Volk.Add(fo, MemoryMarshal.Cast<T, float>(input), fo);
            }
            else
            {
                for (var i = 0; i < len; i++) output[i] += input[i];
            }
        }
    }
}

/// <summary>Input 0 minus every other input (GNU Radio's <c>blocks.sub_xx</c>).</summary>
public sealed class Subtract<T>(int inputs = 2, int vectorLength = 1)
    : SyncMultiInputBlock<T, T>("sub", inputs, vectorLength)
    where T : unmanaged, ISubtractionOperators<T, T, T>
{
    protected override void Work(WorkContext context, int items, Span<T> output)
    {
        var len = items * VectorLength;
        context.Input<T>(0)[..len].CopyTo(output);
        for (var p = 1; p < context.InputCount; p++)
        {
            var input = context.Input<T>(p);
            for (var i = 0; i < len; i++) output[i] -= input[i];
        }
    }
}

/// <summary>Multiplies N inputs element-wise (GNU Radio's <c>blocks.multiply_xx</c>).</summary>
public sealed class Multiply<T>(int inputs = 2, int vectorLength = 1)
    : SyncMultiInputBlock<T, T>("multiply", inputs, vectorLength)
    where T : unmanaged, IMultiplyOperators<T, T, T>
{
    protected override void Work(WorkContext context, int items, Span<T> output)
    {
        var len = items * VectorLength;
        context.Input<T>(0)[..len].CopyTo(output);
        for (var p = 1; p < context.InputCount; p++)
        {
            var input = context.Input<T>(p)[..len];
            if (typeof(T) == typeof(Complex32))
            {
                var co = MemoryMarshal.Cast<T, Complex32>(output);
                Volk.Multiply(co, MemoryMarshal.Cast<T, Complex32>(input), co);
            }
            else if (typeof(T) == typeof(float))
            {
                var fo = MemoryMarshal.Cast<T, float>(output);
                Volk.Multiply(fo, MemoryMarshal.Cast<T, float>(input), fo);
            }
            else
            {
                for (var i = 0; i < len; i++) output[i] *= input[i];
            }
        }
    }
}

/// <summary>Input 0 times the conjugate of input 1 (GNU Radio's <c>blocks.multiply_conjugate_cc</c>).</summary>
public sealed class MultiplyConjugate() : SyncMultiInputBlock<Complex32, Complex32>("multiply_conjugate_cc", 2)
{
    protected override void Work(WorkContext context, int items, Span<Complex32> output) =>
        Volk.MultiplyConjugate(context.Input<Complex32>(0)[..items], context.Input<Complex32>(1)[..items], output);
}

/// <summary>Multiplies by a constant (GNU Radio's <c>blocks.multiply_const_xx</c>). The constant can change while running.</summary>
public sealed class MultiplyConst<T>(T constant, int vectorLength = 1)
    : SyncBlock<T, T>("multiply_const", vectorLength, vectorLength)
    where T : unmanaged, IMultiplyOperators<T, T, T>
{
    private readonly Lock _lock = new();
    private T _constant = constant;

    public T Constant
    {
        get
        {
            lock (_lock) return _constant;
        }
        set
        {
            lock (_lock) _constant = value;
        }
    }

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        var k = Constant;
        if (typeof(T) == typeof(float))
        {
            Volk.Scale(MemoryMarshal.Cast<T, float>(input), (float)(object)k, MemoryMarshal.Cast<T, float>(output));
            return;
        }
        if (typeof(T) == typeof(Complex32))
        {
            var c = (Complex32)(object)k;
            if (c.Imaginary == 0)
            {
                Volk.Scale(MemoryMarshal.Cast<T, Complex32>(input), c.Real, MemoryMarshal.Cast<T, Complex32>(output));
                return;
            }
        }
        for (var i = 0; i < input.Length; i++) output[i] = input[i] * k;
    }
}

/// <summary>Adds a constant (GNU Radio's <c>blocks.add_const_xx</c>).</summary>
public sealed class AddConst<T>(T constant, int vectorLength = 1)
    : SyncBlock<T, T>("add_const", vectorLength, vectorLength)
    where T : unmanaged, IAdditionOperators<T, T, T>
{
    public T Constant { get; set; } = constant;

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        var k = Constant;
        for (var i = 0; i < input.Length; i++) output[i] = input[i] + k;
    }
}

/// <summary>Complex conjugate (GNU Radio's <c>blocks.conjugate_cc</c>).</summary>
public sealed class Conjugate() : SyncBlock<Complex32, Complex32>("conjugate_cc")
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i].Conjugate();
    }
}

/// <summary>|x| (GNU Radio's <c>blocks.complex_to_mag</c>).</summary>
public sealed class ComplexToMag(int vectorLength = 1) : SyncBlock<Complex32, float>("complex_to_mag", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output) => Volk.Magnitude(input, output);
}

/// <summary>|x|² (GNU Radio's <c>blocks.complex_to_mag_squared</c>).</summary>
public sealed class ComplexToMagSquared(int vectorLength = 1) : SyncBlock<Complex32, float>("complex_to_mag_squared", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output) => Volk.MagnitudeSquared(input, output);
}

/// <summary>arg(x) in radians (GNU Radio's <c>blocks.complex_to_arg</c>).</summary>
public sealed class ComplexToArg(int vectorLength = 1) : SyncBlock<Complex32, float>("complex_to_arg", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output) => Volk.Phase(input, output);
}

/// <summary>Real part (GNU Radio's <c>blocks.complex_to_real</c>).</summary>
public sealed class ComplexToReal(int vectorLength = 1) : SyncBlock<Complex32, float>("complex_to_real", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i].Real;
    }
}

/// <summary>Imaginary part (GNU Radio's <c>blocks.complex_to_imag</c>).</summary>
public sealed class ComplexToImag(int vectorLength = 1) : SyncBlock<Complex32, float>("complex_to_imag", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i].Imaginary;
    }
}

/// <summary>Splits complex into real (output 0) and imaginary (output 1) (GNU Radio's <c>blocks.complex_to_float</c>).</summary>
public sealed class ComplexToFloat : Block
{
    public ComplexToFloat() : base("complex_to_float")
    {
        AddInput<Complex32>();
        AddOutput<float>(name: "re");
        AddOutput<float>(name: "im", optional: true);
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.MinOutputItems());
        var input = context.Input<Complex32>(0);
        var re = context.Output<float>(0);
        var im = context.Output<float>(1);
        for (var i = 0; i < n; i++)
        {
            re[i] = input[i].Real;
            im[i] = input[i].Imaginary;
        }
        context.Consume(0, n);
        context.ProduceEach(n);
        return WorkResult.Ok;
    }
}

/// <summary>Combines real (input 0) and imaginary (input 1) into complex (GNU Radio's <c>blocks.float_to_complex</c>).</summary>
public sealed class FloatToComplex() : SyncMultiInputBlock<float, Complex32>("float_to_complex", 2)
{
    protected override void Work(WorkContext context, int items, Span<Complex32> output)
    {
        var re = context.Input<float>(0);
        var im = context.Input<float>(1);
        for (var i = 0; i < items; i++) output[i] = new Complex32(re[i], im[i]);
    }
}

/// <summary>Real float to complex with zero imaginary part.</summary>
public sealed class FloatToComplexReal() : SyncBlock<float, Complex32>("float_to_complex_real")
{
    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = new Complex32(input[i], 0);
    }
}

/// <summary>n·log10(x) + k, typically 10·log10 for power in dB (GNU Radio's <c>blocks.nlog10_ff</c>).</summary>
public sealed class NLog10(float n = 10f, float k = 0f, int vectorLength = 1) : SyncBlock<float, float>("nlog10_ff", vectorLength, vectorLength)
{
    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = n * MathF.Log10(input[i] + 1e-20f) + k;
    }
}

/// <summary>Hysteresis comparator: 1 above <c>high</c>, 0 below <c>low</c> (GNU Radio's <c>blocks.threshold_ff</c>).</summary>
public sealed class Threshold(float low, float high, float initial = 0) : SyncBlock<float, float>("threshold_ff")
{
    private float _state = initial;

    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] > high) _state = 1;
            else if (input[i] < low) _state = 0;
            output[i] = _state;
        }
    }
}

/// <summary>Sums every <c>n</c> items into one (GNU Radio's <c>blocks.integrate_xx</c>).</summary>
public sealed class Integrate<T>(int n) : DecimatorBlock<T, T>("integrate", n)
    where T : unmanaged, IAdditionOperators<T, T, T>, IAdditiveIdentity<T, T>
{
    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        for (var o = 0; o < output.Length; o++)
        {
            var sum = T.AdditiveIdentity;
            foreach (var v in input.Slice(o * Decimation, Decimation)) sum += v;
            output[o] = sum;
        }
    }
}

/// <summary>Running root-mean-square with single-pole smoothing (GNU Radio's <c>blocks.rms_ff/cf</c>).</summary>
public sealed class Rms<T> : SyncBlock<T, float> where T : unmanaged
{
    private readonly float _alpha;
    private float _avg;

    public Rms(float alpha = 0.0001f) : base("rms")
    {
        ItemTypes.RequireFloatOrComplex<T>("Rms");
        _alpha = alpha;
    }

    protected override void Work(ReadOnlySpan<T> input, Span<float> output)
    {
        var isComplex = typeof(T) == typeof(Complex32);
        var c = isComplex ? MemoryMarshal.Cast<T, Complex32>(input) : default;
        var f = isComplex ? default : MemoryMarshal.Cast<T, float>(input);
        for (var i = 0; i < output.Length; i++)
        {
            var p = isComplex ? c[i].MagnitudeSquared : f[i] * f[i];
            _avg = _alpha * p + (1 - _alpha) * _avg;
            output[i] = MathF.Sqrt(_avg);
        }
    }
}

/// <summary>Frequency shift by a rotating phasor (GNU Radio's <c>blocks.rotator_cc</c>).</summary>
public sealed class Rotator(double phaseIncrement) : SyncBlock<Complex32, Complex32>("rotator_cc")
{
    private Complex32 _phase = Complex32.One;
    private Complex32 _increment = Complex32.FromPolar(1f, (float)phaseIncrement);

    /// <summary>Radians per sample; 2π·f/fs for a shift of f Hz.</summary>
    public double PhaseIncrement
    {
        get => _increment.Phase;
        set => _increment = Complex32.FromPolar(1f, (float)value);
    }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output) =>
        Volk.Rotate(input, _increment, ref _phase, output);
}
