namespace GnuRadio;

/// <summary>
/// One input, one output, one output item per input item (GNU Radio's <c>sync_block</c>).
/// Implement <see cref="Work"/>; the base handles counts, consumption and production.
/// </summary>
public abstract class SyncBlock<TIn, TOut> : Block
    where TIn : unmanaged
    where TOut : unmanaged
{
    protected SyncBlock(string name, int inputVectorLength = 1, int outputVectorLength = 1) : base(name)
    {
        AddInput<TIn>(inputVectorLength);
        AddOutput<TOut>(outputVectorLength);
        InputVectorLength = inputVectorLength;
        OutputVectorLength = outputVectorLength;
    }

    protected int InputVectorLength { get; }
    protected int OutputVectorLength { get; }

    /// <summary>Processes <c>input.Length / InputVectorLength</c> items into the same number of output items.</summary>
    protected abstract void Work(ReadOnlySpan<TIn> input, Span<TOut> output);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.OutputItems(0));
        if (n == 0) return WorkResult.Ok;
        Work(context.Input<TIn>(0)[..(n * InputVectorLength)], context.Output<TOut>(0)[..(n * OutputVectorLength)]);
        context.Consume(0, n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Several inputs of the same type, one output, 1:1 rate (for adders, multipliers, interleavers).
/// </summary>
public abstract class SyncMultiInputBlock<TIn, TOut> : Block
    where TIn : unmanaged
    where TOut : unmanaged
{
    protected SyncMultiInputBlock(string name, int inputs, int vectorLength = 1) : base(name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputs, 1);
        for (var i = 0; i < inputs; i++) AddInput<TIn>(vectorLength);
        AddOutput<TOut>(vectorLength);
        VectorLength = vectorLength;
    }

    protected int VectorLength { get; }

    /// <summary>Processes <paramref name="items"/> items from every input of <paramref name="context"/>.</summary>
    protected abstract void Work(WorkContext context, int items, Span<TOut> output);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.MinInputItems(), context.OutputItems(0));
        if (n == 0) return WorkResult.Ok;
        Work(context, n, context.Output<TOut>(0)[..(n * VectorLength)]);
        context.ConsumeEach(n);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Consumes <see cref="Decimation"/> input items per output item.</summary>
public abstract class DecimatorBlock<TIn, TOut> : Block
    where TIn : unmanaged
    where TOut : unmanaged
{
    protected DecimatorBlock(string name, int decimation) : base(name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(decimation, 1);
        Decimation = decimation;
        RelativeRate = 1.0 / decimation;
        AddInput<TIn>();
        AddOutput<TOut>();
    }

    public int Decimation { get; }

    protected internal override int MinimumInputItems => Decimation;

    /// <summary><paramref name="input"/> holds exactly <c>output.Length × Decimation</c> items.</summary>
    protected abstract void Work(ReadOnlySpan<TIn> input, Span<TOut> output);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0) / Decimation, context.OutputItems(0));
        if (n == 0) return WorkResult.Ok;
        Work(context.Input<TIn>(0)[..(n * Decimation)], context.Output<TOut>(0)[..n]);
        context.Consume(0, n * Decimation);
        context.Produce(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Produces <see cref="Interpolation"/> output items per input item.</summary>
public abstract class InterpolatorBlock<TIn, TOut> : Block
    where TIn : unmanaged
    where TOut : unmanaged
{
    protected InterpolatorBlock(string name, int interpolation) : base(name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interpolation, 1);
        Interpolation = interpolation;
        RelativeRate = interpolation;
        AddInput<TIn>();
        AddOutput<TOut>();
    }

    public int Interpolation { get; }

    protected internal override int OutputMultiple => Interpolation;

    /// <summary><paramref name="output"/> holds exactly <c>input.Length × Interpolation</c> items.</summary>
    protected abstract void Work(ReadOnlySpan<TIn> input, Span<TOut> output);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.InputItems(0), context.OutputItems(0) / Interpolation);
        if (n == 0) return WorkResult.Ok;
        Work(context.Input<TIn>(0)[..n], context.Output<TOut>(0)[..(n * Interpolation)]);
        context.Consume(0, n);
        context.Produce(0, n * Interpolation);
        return WorkResult.Ok;
    }
}

/// <summary>A block with one output and no inputs.</summary>
public abstract class SourceBlock<T> : Block where T : unmanaged
{
    protected SourceBlock(string name, int vectorLength = 1) : base(name)
    {
        AddOutput<T>(vectorLength);
        VectorLength = vectorLength;
    }

    protected int VectorLength { get; }

    /// <summary>
    /// Fills up to <c>output.Length / VectorLength</c> items. Returns the number of items written,
    /// or -1 when the source is exhausted.
    /// </summary>
    protected abstract int Generate(Span<T> output);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var space = context.OutputItems(0);
        if (space == 0) return WorkResult.Ok;
        var produced = Generate(context.Output<T>(0)[..(space * VectorLength)]);
        if (produced < 0) return WorkResult.Done;
        context.Produce(0, produced);
        return WorkResult.Ok;
    }
}

/// <summary>A block with one input and no outputs.</summary>
public abstract class SinkBlock<T> : Block where T : unmanaged
{
    protected SinkBlock(string name, int vectorLength = 1) : base(name)
    {
        AddInput<T>(vectorLength);
        VectorLength = vectorLength;
    }

    protected int VectorLength { get; }

    /// <summary>Handles <c>input.Length / VectorLength</c> items. Return false to stop the flowgraph branch.</summary>
    protected abstract bool Consume(ReadOnlySpan<T> input);

    protected internal sealed override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        if (n == 0) return WorkResult.Ok;
        var more = Consume(context.Input<T>(0)[..(n * VectorLength)]);
        context.Consume(0, n);
        return more ? WorkResult.Ok : WorkResult.Done;
    }
}

/// <summary>A block that only has message ports; it reacts to messages and never streams.</summary>
public abstract class MessageBlock : Block
{
    protected MessageBlock(string name) : base(name)
    {
    }

    protected internal sealed override WorkResult GeneralWork(WorkContext context) => WorkResult.Ok;
}
