namespace GnuRadio;

/// <summary>
/// The window of input and output items handed to <see cref="Block.GeneralWork"/>.
/// One instance per block is reused for every call, so work functions do not allocate.
/// Counts are in items; a vector port's span holds items × vector length elements.
/// </summary>
public sealed class WorkContext
{
    internal readonly Block Block;
    internal readonly IStreamBuffer[] InBuffers;
    internal readonly int[] InReaders;
    internal readonly IStreamBuffer?[] OutBuffers;
    internal readonly int[] InAvailable;
    internal readonly int[] OutSpace;
    internal readonly int[] Consumed;
    internal readonly int[] Produced;
    internal readonly bool[] InDone;
    private readonly IStreamBuffer[] _discard;

    internal WorkContext(Block block, IStreamBuffer[] inputs, int[] readers, IStreamBuffer?[] outputs)
    {
        Block = block;
        InBuffers = inputs;
        InReaders = readers;
        OutBuffers = outputs;
        InAvailable = new int[inputs.Length];
        InDone = new bool[inputs.Length];
        OutSpace = new int[outputs.Length];
        Consumed = new int[inputs.Length];
        Produced = new int[outputs.Length];

        // Unconnected optional outputs write into a private buffer nobody reads.
        _discard = new IStreamBuffer[outputs.Length];
        for (var o = 0; o < outputs.Length; o++)
            if (outputs[o] is null)
                _discard[o] = block.OutputSignature[o].CreateBuffer(8192);
    }

    public int InputCount => InBuffers.Length;
    public int OutputCount => OutBuffers.Length;

    /// <summary>Items available on input <paramref name="port"/> in this call.</summary>
    public int InputItems(int port) => InAvailable[port];

    /// <summary>Space, in items, available on output <paramref name="port"/> in this call.</summary>
    public int OutputItems(int port) => OutSpace[port];

    /// <summary>Smallest input count across all inputs.</summary>
    public int MinInputItems()
    {
        var n = int.MaxValue;
        foreach (var a in InAvailable) n = Math.Min(n, a);
        return InAvailable.Length == 0 ? 0 : n;
    }

    /// <summary>Smallest output space across all outputs.</summary>
    public int MinOutputItems()
    {
        var n = int.MaxValue;
        foreach (var a in OutSpace) n = Math.Min(n, a);
        return OutSpace.Length == 0 ? 0 : n;
    }

    /// <summary>True once the upstream writer finished: what is visible now is all that will ever arrive.</summary>
    public bool IsInputDone(int port) => InDone[port];

    public ReadOnlySpan<T> Input<T>(int port) where T : unmanaged =>
        ((StreamBuffer<T>)InBuffers[port]).ReadSpan(InReaders[port], InAvailable[port]);

    public Span<T> Output<T>(int port) where T : unmanaged =>
        ((StreamBuffer<T>)OutputBuffer(port)).WriteSpan(OutSpace[port]);

    public void Consume(int port, int items)
    {
        if ((uint)items > (uint)(InAvailable[port] - Consumed[port]))
            throw new InvalidOperationException($"{Block.Alias} consumed {items} items on input {port} but only {InAvailable[port]} were available.");
        Consumed[port] += items;
    }

    public void ConsumeEach(int items)
    {
        for (var i = 0; i < InBuffers.Length; i++) Consume(i, items);
    }

    public void Produce(int port, int items)
    {
        if ((uint)items > (uint)(OutSpace[port] - Produced[port]))
            throw new InvalidOperationException($"{Block.Alias} produced {items} items on output {port} but only {OutSpace[port]} fit.");
        Produced[port] += items;
    }

    public void ProduceEach(int items)
    {
        for (var o = 0; o < OutBuffers.Length; o++) Produce(o, items);
    }

    /// <summary>Absolute count of items consumed so far on <paramref name="port"/> (GNU Radio's <c>nitems_read</c>).</summary>
    public long ItemsRead(int port) => InBuffers[port].ItemsRead(InReaders[port]);

    /// <summary>Absolute count of items produced so far on <paramref name="port"/> (GNU Radio's <c>nitems_written</c>).</summary>
    public long ItemsWritten(int port) => OutputBuffer(port).ItemsWritten;

    /// <summary>Adds tags on <paramref name="port"/> whose absolute offsets fall in [start, end).</summary>
    public void GetTags(int port, long start, long end, List<Tag> into) => InBuffers[port].GetTags(start, end, into);

    /// <summary>Tags in the items currently visible on input <paramref name="port"/>.</summary>
    public void GetTags(int port, List<Tag> into)
    {
        var start = ItemsRead(port);
        InBuffers[port].GetTags(start, start + InAvailable[port], into);
    }

    /// <summary>Attaches a tag at absolute output offset <paramref name="offset"/>.</summary>
    public void AddTag(int port, long offset, string key, Pmt value) =>
        OutputBuffer(port).AddTag(new Tag(offset, key, value, Block.Alias));

    private IStreamBuffer OutputBuffer(int port) => OutBuffers[port] ?? _discard[port];

    internal void Prepare()
    {
        Array.Clear(Consumed);
        Array.Clear(Produced);
        for (var o = 0; o < OutBuffers.Length; o++)
            OutSpace[o] = OutputBuffer(o).WritableContiguous();
    }

    internal void Commit()
    {
        for (var o = 0; o < OutBuffers.Length; o++)
            if (Produced[o] > 0)
                OutputBuffer(o).Produce(Produced[o]);
        for (var i = 0; i < InBuffers.Length; i++)
            if (Consumed[i] > 0)
                InBuffers[i].Consume(InReaders[i], Consumed[i]);
    }
}
