using System.Diagnostics;

namespace GnuRadio.Blocks;

/// <summary>Passes the first <see cref="Count"/> items, then finishes (GNU Radio's <c>blocks.head</c>).</summary>
public sealed class Head<T> : Block where T : unmanaged
{
    private long _passed;

    public Head(long count, int vectorLength = 1) : base("head")
    {
        Count = count;
        VectorLength = vectorLength;
        AddInput<T>(vectorLength);
        AddOutput<T>(vectorLength);
    }

    public long Count { get; }
    public int VectorLength { get; }

    public void Reset() => _passed = 0;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        if (_passed >= Count) return WorkResult.Done;
        var n = (int)Math.Min(Count - _passed, Math.Min(context.InputItems(0), context.OutputItems(0)));
        context.Input<T>(0)[..(n * VectorLength)].CopyTo(context.Output<T>(0));
        context.Consume(0, n);
        context.Produce(0, n);
        _passed += n;
        return _passed >= Count ? WorkResult.Done : WorkResult.Ok;
    }
}

/// <summary>Drops the first <see cref="Count"/> items, then passes everything (GNU Radio's <c>blocks.skiphead</c>).</summary>
public sealed class SkipHead<T> : Block where T : unmanaged
{
    private long _skipped;

    public SkipHead(long count) : base("skiphead")
    {
        Count = count;
        AddInput<T>();
        AddOutput<T>();
    }

    public long Count { get; }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var available = context.InputItems(0);
        if (_skipped < Count)
        {
            var skip = (int)Math.Min(Count - _skipped, available);
            context.Consume(0, skip);
            _skipped += skip;
            return WorkResult.Ok;
        }
        var n = Math.Min(available, context.OutputItems(0));
        context.Input<T>(0)[..n].CopyTo(context.Output<T>(0));
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Limits the item rate to <see cref="SampleRate"/> items per second (GNU Radio's <c>blocks.throttle</c>).
/// Use it in simulations without hardware so the CPU is not saturated and UIs update in real time.
/// </summary>
public sealed class Throttle<T> : Block where T : unmanaged
{
    private long _startTicks;
    private long _total;
    private volatile float _rate;

    public Throttle(double sampleRate, int vectorLength = 1) : base("throttle")
    {
        _rate = (float)sampleRate;
        VectorLength = vectorLength;
        AddInput<T>(vectorLength);
        AddOutput<T>(vectorLength);
    }

    public int VectorLength { get; }

    public double SampleRate
    {
        get => _rate;
        set
        {
            _rate = (float)value;
            _startTicks = Stopwatch.GetTimestamp();
            _total = 0;
        }
    }

    public override void Start()
    {
        _startTicks = Stopwatch.GetTimestamp();
        _total = 0;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;
        var allowed = (long)(elapsed * _rate) - _total;
        // Pass at most ~20 ms worth per call so downstream sees a smooth stream.
        var chunk = Math.Max(1, (int)(_rate * 0.02));
        var n = (int)Math.Min(Math.Min(allowed, chunk), Math.Min(context.InputItems(0), context.OutputItems(0)));
        if (n <= 0)
        {
            var wait = (_total + Math.Min(chunk, Math.Max(1, context.InputItems(0)))) / _rate - elapsed;
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.05)));
            return WorkResult.Ok;
        }
        context.Input<T>(0)[..(n * VectorLength)].CopyTo(context.Output<T>(0));
        context.Consume(0, n);
        context.Produce(0, n);
        _total += n;
        return WorkResult.Ok;
    }
}

/// <summary>Delays the stream by <see cref="DelayItems"/> items, inserting zeros first (GNU Radio's <c>blocks.delay</c>).</summary>
public sealed class Delay<T> : Block where T : unmanaged
{
    private int _pending;

    public Delay(int delay) : base("delay")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay);
        _pending = delay;
        DelayItems = delay;
        AddInput<T>();
        AddOutput<T>();
    }

    public int DelayItems { get; }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var output = context.Output<T>(0);
        if (_pending > 0)
        {
            var z = Math.Min(_pending, context.OutputItems(0));
            output[..z].Clear();
            context.Produce(0, z);
            _pending -= z;
            return WorkResult.Ok;
        }
        var n = Math.Min(context.InputItems(0), context.OutputItems(0));
        context.Input<T>(0)[..n].CopyTo(output);
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Keeps one item out of every <c>n</c> (GNU Radio's <c>blocks.keep_one_in_n</c>).</summary>
public sealed class KeepOneInN<T>(int n) : DecimatorBlock<T, T>("keep_one_in_n", n) where T : unmanaged
{
    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        for (var i = 0; i < output.Length; i++) output[i] = input[i * Decimation + Decimation - 1];
    }
}

/// <summary>Groups <c>vlen</c> consecutive items into one vector item (GNU Radio's <c>blocks.stream_to_vector</c>).</summary>
public sealed class StreamToVector<T> : Block where T : unmanaged
{
    public StreamToVector(int vectorLength) : base("stream_to_vector")
    {
        VectorLength = vectorLength;
        RelativeRate = 1.0 / vectorLength;
        AddInput<T>();
        AddOutput<T>(vectorLength);
    }

    public int VectorLength { get; }

    protected internal override int MinimumInputItems => VectorLength;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0) / VectorLength, context.OutputItems(0));
        if (n == 0) return WorkResult.Ok;
        context.Input<T>(0)[..(n * VectorLength)].CopyTo(context.Output<T>(0));
        context.Consume(0, n * VectorLength);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Splits vector items back into a stream (GNU Radio's <c>blocks.vector_to_stream</c>).</summary>
public sealed class VectorToStream<T> : Block where T : unmanaged
{
    public VectorToStream(int vectorLength) : base("vector_to_stream")
    {
        VectorLength = vectorLength;
        RelativeRate = vectorLength;
        AddInput<T>(vectorLength);
        AddOutput<T>();
    }

    public int VectorLength { get; }

    protected internal override int OutputMultiple => VectorLength;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.OutputItems(0) / VectorLength);
        if (n == 0) return WorkResult.Ok;
        context.Input<T>(0)[..(n * VectorLength)].CopyTo(context.Output<T>(0));
        context.Consume(0, n);
        context.Produce(0, n * VectorLength);
        return WorkResult.Ok;
    }
}

/// <summary>Round-robin merge of N inputs into one stream (GNU Radio's <c>blocks.interleave</c>).</summary>
public sealed class Interleave<T> : Block where T : unmanaged
{
    public Interleave(int inputs) : base("interleave")
    {
        for (var i = 0; i < inputs; i++) AddInput<T>();
        AddOutput<T>();
        Inputs = inputs;
        RelativeRate = inputs;
    }

    public int Inputs { get; }

    protected internal override int OutputMultiple => Inputs;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.MinInputItems(), context.OutputItems(0) / Inputs);
        if (n == 0) return WorkResult.Ok;
        var output = context.Output<T>(0);
        for (var p = 0; p < Inputs; p++)
        {
            var input = context.Input<T>(p);
            for (var i = 0; i < n; i++) output[i * Inputs + p] = input[i];
        }
        context.ConsumeEach(n);
        context.Produce(0, n * Inputs);
        return WorkResult.Ok;
    }
}

/// <summary>Round-robin split of one stream into N outputs (GNU Radio's <c>blocks.deinterleave</c>).</summary>
public sealed class Deinterleave<T> : Block where T : unmanaged
{
    public Deinterleave(int outputs) : base("deinterleave")
    {
        AddInput<T>();
        for (var o = 0; o < outputs; o++) AddOutput<T>();
        Outputs = outputs;
        RelativeRate = 1.0 / outputs;
    }

    public int Outputs { get; }

    protected internal override int MinimumInputItems => Outputs;

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0) / Outputs, context.MinOutputItems());
        if (n == 0) return WorkResult.Ok;
        var input = context.Input<T>(0);
        for (var p = 0; p < Outputs; p++)
        {
            var output = context.Output<T>(p);
            for (var i = 0; i < n; i++) output[i] = input[i * Outputs + p];
        }
        context.Consume(0, n * Outputs);
        context.ProduceEach(n);
        return WorkResult.Ok;
    }
}

/// <summary>Copies input to output; when <see cref="Enabled"/> is false items are dropped (GNU Radio's <c>blocks.copy</c>).</summary>
public sealed class Copy<T> : Block where T : unmanaged
{
    private readonly int _vlen;
    private volatile bool _enabled = true;

    public Copy(int vectorLength = 1) : base("copy")
    {
        _vlen = vectorLength;
        AddInput<T>(vectorLength);
        AddOutput<T>(vectorLength);
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var available = context.InputItems(0);
        if (!_enabled)
        {
            context.Consume(0, available);
            return WorkResult.Ok;
        }
        var n = Math.Min(available, context.OutputItems(0));
        context.Input<T>(0)[..(n * _vlen)].CopyTo(context.Output<T>(0));
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}
