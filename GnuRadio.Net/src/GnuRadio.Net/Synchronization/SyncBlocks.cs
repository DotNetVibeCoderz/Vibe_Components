using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Digital;

namespace GnuRadio.Synchronization;

/// <summary>Cubic Lagrange (Farrow) fractional-delay interpolation.</summary>
public static class Interpolator
{
    /// <summary>Value at position <c>index + mu</c> (0 ≤ mu &lt; 1) using samples index−1 … index+2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 Cubic(ReadOnlySpan<Complex32> x, int index, float mu)
    {
        var (c0, c1, c2, c3) = Coefficients(mu);
        return x[index - 1] * c0 + x[index] * c1 + x[index + 1] * c2 + x[index + 2] * c3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cubic(ReadOnlySpan<float> x, int index, float mu)
    {
        var (c0, c1, c2, c3) = Coefficients(mu);
        return x[index - 1] * c0 + x[index] * c1 + x[index + 1] * c2 + x[index + 2] * c3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (float, float, float, float) Coefficients(float mu)
    {
        var m1 = mu - 1;
        var m2 = mu - 2;
        var p1 = mu + 1;
        return (-mu * m1 * m2 / 6f, p1 * m1 * m2 / 2f, -p1 * mu * m2 / 2f, p1 * mu * m1 / 6f);
    }
}

/// <summary>
/// Costas loop carrier recovery for BPSK (order 2), QPSK (4) and 8PSK (8) (GNU Radio's <c>digital.costas_loop_cc</c>).
/// Output 0 is the de-rotated signal; optional output 1 is the loop frequency in rad/sample.
/// </summary>
public sealed class CostasLoop : Block
{
    private readonly ControlLoop _loop;

    public CostasLoop(double loopBandwidth, int order) : base("costas_loop_cc")
    {
        if (order is not (2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(order), "Order must be 2, 4 or 8.");
        Order = order;
        _loop = new ControlLoop(loopBandwidth, 1, -1);
        AddInput<Complex32>();
        AddOutput<Complex32>();
        AddOutput<float>(name: "frequency", optional: true);
    }

    public int Order { get; }
    public double Frequency => _loop.Frequency;
    public double Phase => _loop.Phase;

    public double LoopBandwidth
    {
        get => _loop.LoopBandwidth;
        set => _loop.LoopBandwidth = value;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.MinOutputItems());
        var input = context.Input<Complex32>(0);
        var output = context.Output<Complex32>(0);
        var freq = context.Output<float>(1);
        for (var i = 0; i < n; i++)
        {
            var y = input[i] * Complex32.FromPolar(1f, (float)-_loop.Phase);
            output[i] = y;
            var error = Math.Clamp(PhaseError(y), -1f, 1f);
            _loop.Advance(error);
            freq[i] = (float)_loop.Frequency;
        }
        context.Consume(0, n);
        context.ProduceEach(n);
        return WorkResult.Ok;
    }

    private float PhaseError(Complex32 y)
    {
        switch (Order)
        {
            case 2:
                return y.Real * y.Imaginary;
            case 4:
                return MathF.Sign(y.Real) * y.Imaginary - MathF.Sign(y.Imaginary) * y.Real;
            default:
            {
                const float k = 0.41421356f;
                return MathF.Abs(y.Real) >= MathF.Abs(y.Imaginary)
                    ? MathF.Sign(y.Real) * y.Imaginary - MathF.Sign(y.Imaginary) * y.Real * k
                    : MathF.Sign(y.Real) * y.Imaginary * k - MathF.Sign(y.Imaginary) * y.Real;
            }
        }
    }
}

/// <summary>
/// Decision-directed carrier recovery for any constellation (as inside GNU Radio's
/// <c>digital.constellation_receiver_cb</c>): phase error = arg(y · conj(decision)).
/// </summary>
public sealed class DecisionDirectedCarrierRecovery(Constellation constellation, double loopBandwidth)
    : SyncBlock<Complex32, Complex32>("constellation_receiver_cc")
{
    private readonly ControlLoop _loop = new(loopBandwidth, 1, -1);

    public double Frequency => _loop.Frequency;

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var y = input[i] * Complex32.FromPolar(1f, (float)-_loop.Phase);
            output[i] = y;
            var d = constellation.Points[constellation.Decide(y)];
            _loop.Advance((y * d.Conjugate()).Phase);
        }
    }
}

/// <summary>
/// Symbol timing recovery with a Gardner timing-error detector, PI loop and cubic interpolation
/// (GNU Radio's <c>digital.symbol_sync_cc</c> with <c>TED_GARDNER</c>). Outputs one sample per symbol.
/// </summary>
public sealed class SymbolSync : Block
{
    private readonly double _nominal;
    private readonly double _maxDeviation;
    private readonly double _alpha;
    private readonly double _beta;
    private readonly int _history;
    private Complex32[] _work;
    private double _t;
    private double _integrator;
    private Complex32 _previous;

    public SymbolSync(double samplesPerSymbol, double loopBandwidth = 0.045, double damping = 1.0,
        double tedGain = 2.0, double maxDeviation = 0.015)
        : base("symbol_sync_cc")
    {
        if (samplesPerSymbol < 2) throw new ArgumentOutOfRangeException(nameof(samplesPerSymbol), "Gardner needs at least 2 samples per symbol.");
        _nominal = samplesPerSymbol;
        _maxDeviation = maxDeviation * samplesPerSymbol;
        var denom = 1 + 2 * damping * loopBandwidth + loopBandwidth * loopBandwidth;
        _alpha = 4 * damping * loopBandwidth / denom * samplesPerSymbol / tedGain;
        _beta = 4 * loopBandwidth * loopBandwidth / denom * samplesPerSymbol / tedGain;
        _history = (int)Math.Ceiling(2 * samplesPerSymbol) + 4;
        _work = new Complex32[_history];
        _t = _history;
        RelativeRate = 1.0 / samplesPerSymbol;
        AddInput<Complex32>();
        AddOutput<Complex32>();
        AddOutput<float>(name: "error", optional: true);
    }

    /// <summary>Current samples-per-symbol estimate.</summary>
    public double SamplesPerSymbol => _nominal + _integrator;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var input = context.Input<Complex32>(0);
        var output = context.Output<Complex32>(0);
        var errors = context.Output<float>(1);
        var space = context.MinOutputItems();
        var h = _history;
        var need = h + input.Length;
        if (_work.Length < need)
        {
            var w = new Complex32[Math.Max(need, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input.CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, need);

        var produced = 0;
        var omega = _nominal + _integrator;
        while (produced < space && (int)_t + 2 < need)
        {
            var i = (int)_t;
            var y = Interpolator.Cubic(work, i, (float)(_t - i));
            var tm = _t - omega / 2;
            var im = (int)tm;
            var mid = Interpolator.Cubic(work, im, (float)(tm - im));
            var d = _previous - y;
            var e = Math.Clamp(d.Real * mid.Real + d.Imaginary * mid.Imaginary, -1f, 1f);
            _previous = y;
            output[produced] = y;
            errors[produced] = e;
            produced++;

            _integrator = Math.Clamp(_integrator + _beta * e, -_maxDeviation, _maxDeviation);
            omega = _nominal + _integrator;
            _t += omega + _alpha * e;
        }

        // Keep the last h samples as history for the next call, unless we stopped for lack of output space.
        var keepFrom = Math.Min(need - h, Math.Max(0, (int)_t - h + 2));
        var consumed = Math.Clamp(keepFrom, 0, input.Length);
        work.Slice(consumed, h).CopyTo(_work);
        _t -= consumed;
        context.Consume(0, consumed);
        context.ProduceEach(produced);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Mueller &amp; Müller clock recovery for real signals (GNU Radio's <c>digital.clock_recovery_mm_ff</c>).
/// Typical gains: <c>gainMu = 0.175</c>, <c>gainOmega = 0.25·gainMu²</c>.
/// </summary>
public sealed class ClockRecoveryMm : Block
{
    private readonly double _omegaMid;
    private readonly double _omegaLimit;
    private readonly int _history = 4;
    private float[] _work;
    private double _t;
    private double _omega;
    private float _last;

    public ClockRecoveryMm(double omega, double gainOmega = -1, double mu = 0.5, double gainMu = 0.175, double omegaRelativeLimit = 0.005)
        : base("clock_recovery_mm_ff")
    {
        _omega = _omegaMid = omega;
        GainMu = gainMu;
        GainOmega = gainOmega < 0 ? 0.25 * gainMu * gainMu : gainOmega;
        _omegaLimit = omegaRelativeLimit * omega;
        _work = new float[_history];
        _t = _history + mu;
        RelativeRate = 1.0 / omega;
        AddInput<float>();
        AddOutput<float>();
    }

    public double GainMu { get; set; }
    public double GainOmega { get; set; }
    public double Omega => _omega;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var input = context.Input<float>(0);
        var output = context.Output<float>(0);
        var h = _history;
        var need = h + input.Length;
        if (_work.Length < need)
        {
            var w = new float[Math.Max(need, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input.CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, need);
        var produced = 0;
        while (produced < output.Length && (int)_t + 2 < need)
        {
            var i = (int)_t;
            var y = Interpolator.Cubic(work, i, (float)(_t - i));
            var mm = MathF.Sign(_last) * y - MathF.Sign(y) * _last;
            _last = y;
            output[produced++] = y;
            _omega = _omegaMid + Math.Clamp(_omega + GainOmega * mm - _omegaMid, -_omegaLimit, _omegaLimit);
            _t += _omega + GainMu * mm;
        }
        var consumed = Math.Clamp((int)_t - h + 2, 0, input.Length);
        work.Slice(consumed, h).CopyTo(_work);
        _t -= consumed;
        context.Consume(0, consumed);
        context.Produce(0, produced);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Finds an access code in a hard-bit stream (one bit per byte) and tags the bit that follows it
/// (GNU Radio's <c>digital.correlate_access_code_tag_bb</c>). Up to <c>threshold</c> bit errors are tolerated.
/// </summary>
public sealed class CorrelateAccessCodeTag : Block
{
    private readonly ulong _code;
    private readonly ulong _mask;
    private readonly int _threshold;
    private ulong _register;

    public CorrelateAccessCodeTag(string accessCode, int threshold, string tagName = "syncword")
        : base("correlate_access_code_tag_bb")
    {
        var bits = Bits.FromString(accessCode);
        if (bits.Length is 0 or > 64) throw new ArgumentException("Access code must be 1..64 bits.", nameof(accessCode));
        foreach (var b in bits) _code = (_code << 1) | b;
        _mask = bits.Length == 64 ? ulong.MaxValue : (1UL << bits.Length) - 1;
        _threshold = threshold;
        TagName = tagName;
        CodeLength = bits.Length;
        AddInput<byte>();
        AddOutput<byte>();
    }

    public string TagName { get; }
    public int CodeLength { get; }
    public long Matches { get; private set; }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.OutputItems(0));
        var input = context.Input<byte>(0)[..n];
        input.CopyTo(context.Output<byte>(0));
        var start = context.ItemsWritten(0);
        for (var i = 0; i < n; i++)
        {
            _register = ((_register << 1) | (uint)(input[i] & 1)) & _mask;
            var distance = (int)ulong.PopCount(_register ^ _code);
            if (distance <= _threshold)
            {
                Matches++;
                context.AddTag(0, start + i + 1, TagName, distance);
            }
        }
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Correlates complex samples against a known sequence and tags detections (GNU Radio's
/// <c>digital.corr_est_cc</c>). The stream is delayed by the sequence length so the tag
/// <c>corr_start</c> lands on the first sample of the sequence; its value is the detection phase.
/// Optional output 1 carries the normalised correlation magnitude (0…1).
/// </summary>
public sealed class CorrelationEstimator : Block
{
    private readonly Complex32[] _taps;
    private readonly int _length;
    private readonly int _delay;
    private readonly float _energy;
    private Complex32[] _work;
    private float _windowPower;
    private long _pendingPeak = -1;
    private float _pendingMetric;
    private float _pendingPhase;

    public CorrelationEstimator(ReadOnlySpan<Complex32> sequence, float threshold = 0.8f) : base("corr_est_cc")
    {
        _length = sequence.Length;
        _taps = new Complex32[_length];
        for (var i = 0; i < _length; i++) _taps[i] = sequence[i].Conjugate();
        foreach (var s in sequence) _energy += s.MagnitudeSquared;
        // Delaying by 2L leaves room to place the tag after the peak has been confirmed.
        _delay = 2 * _length;
        Threshold = threshold;
        _work = new Complex32[_delay];
        AddInput<Complex32>();
        AddOutput<Complex32>();
        AddOutput<float>(name: "corr", optional: true);
    }

    public float Threshold { get; set; }
    public long Detections { get; private set; }

    /// <summary>Samples of delay between input and output.</summary>
    public int Delay => _delay;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.MinOutputItems());
        if (n == 0) return WorkResult.Ok;
        var input = context.Input<Complex32>(0)[..n];
        var output = context.Output<Complex32>(0);
        var corrOut = context.Output<float>(1);
        var h = _delay;
        var l = _length;
        if (_work.Length < h + n)
        {
            var w = new Complex32[Math.Max(h + n, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input.CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, h + n);
        var outStart = context.ItemsWritten(0);
        for (var i = 0; i < n; i++)
        {
            output[i] = work[i];
            var current = i + h;
            _windowPower += work[current].MagnitudeSquared - work[current - l].MagnitudeSquared;
            if (_windowPower < 0) _windowPower = 0;
            var c = Kernels.Volk.Dot(work.Slice(current - l + 1, l), _taps);
            var metric = Math.Min(c.MagnitudeSquared / (_energy * Math.Max(_windowPower, 1e-12f)), 1f);
            corrOut[i] = metric;

            // Report the maximum of each excursion above the threshold, at the sequence's first sample.
            if (metric > Threshold)
            {
                if (_pendingPeak < 0 || metric > _pendingMetric)
                {
                    _pendingPeak = outStart + current - l + 1;
                    _pendingMetric = metric;
                    _pendingPhase = c.Phase;
                }
            }
            else if (_pendingPeak >= 0)
            {
                context.AddTag(0, _pendingPeak, "corr_start", Pmt.Dict(("phase", (double)_pendingPhase), ("metric", (double)_pendingMetric)));
                Detections++;
                _pendingPeak = -1;
            }
        }
        work.Slice(n, h).CopyTo(_work);
        context.Consume(0, n);
        context.ProduceEach(n);
        return WorkResult.Ok;
    }
}
