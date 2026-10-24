using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GnuRadio;

/// <summary>Describes one stream port: item type, vector length and name.</summary>
public sealed class PortSignature
{
    private readonly Func<int, IStreamBuffer> _bufferFactory;

    internal PortSignature(Type itemType, int itemSize, int vectorLength, string name, bool optional, Func<int, IStreamBuffer> bufferFactory)
    {
        ItemType = itemType;
        ItemSize = itemSize;
        VectorLength = vectorLength;
        Name = name;
        Optional = optional;
        _bufferFactory = bufferFactory;
    }

    public Type ItemType { get; }

    /// <summary>Size in bytes of one element (not of the whole vector).</summary>
    public int ItemSize { get; }

    public int VectorLength { get; }
    public string Name { get; }

    /// <summary>An optional output may stay unconnected; its items are discarded.</summary>
    public bool Optional { get; }

    internal IStreamBuffer CreateBuffer(int capacity) => _bufferFactory(capacity);

    public override string ToString() =>
        VectorLength == 1 ? $"{Name}:{ItemType.Name}" : $"{Name}:{ItemType.Name}[{VectorLength}]";
}

/// <summary>How a block forwards stream tags from its inputs to its outputs.</summary>
public enum TagPropagationPolicy
{
    /// <summary>Tags are not forwarded; the block handles them itself.</summary>
    None,

    /// <summary>Every input tag is copied to every output, offset scaled by <see cref="Block.RelativeRate"/>.</summary>
    AllToAll,

    /// <summary>Tags on input <c>i</c> are copied to output <c>i</c> only.</summary>
    OneToOne,
}

/// <summary>Per-block runtime counters, readable while the flowgraph runs.</summary>
public sealed class BlockStatistics
{
    internal long WorkCalls;
    internal long ItemsProduced;
    internal long ItemsConsumed;
    internal long WorkTicks;

    public long WorkCallCount => Interlocked.Read(ref WorkCalls);
    public long ProducedItems => Interlocked.Read(ref ItemsProduced);
    public long ConsumedItems => Interlocked.Read(ref ItemsConsumed);
    public TimeSpan TimeInWork => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref WorkTicks));
}

/// <summary>
/// Base class of every signal-processing block (GNU Radio's <c>gr::block</c>).
/// A block declares typed stream ports and message ports, and implements
/// <see cref="GeneralWork"/>, which reads from its inputs and writes to its outputs.
/// Most blocks derive from one of the simpler bases instead:
/// <see cref="SyncBlock{TIn,TOut}"/>, <see cref="DecimatorBlock{TIn,TOut}"/>,
/// <see cref="InterpolatorBlock{TIn,TOut}"/>, <see cref="SourceBlock{T}"/> or <see cref="SinkBlock{T}"/>.
/// </summary>
public abstract class Block
{
    private static int s_nextId;

    private readonly List<PortSignature> _inputs = [];
    private readonly List<PortSignature> _outputs = [];
    private readonly Dictionary<string, Action<Pmt>> _messageHandlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _messageOutputs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Port, Pmt Message)> _messageQueue = new();
    internal readonly Dictionary<string, List<(Block Block, string Port)>> MessageSubscribers = new(StringComparer.Ordinal);
    internal readonly ManualResetEventSlim Signal = new(false, spinCount: 20);

    protected Block(string name)
    {
        Name = name;
        Id = Interlocked.Increment(ref s_nextId);
        Alias = $"{name}{Id}";
    }

    /// <summary>Block type name, for example <c>fir_filter_ccf</c>.</summary>
    public string Name { get; }

    /// <summary>Process-unique numeric id.</summary>
    public int Id { get; }

    /// <summary>Instance name shown in diagnostics and diagrams; defaults to name + id.</summary>
    public string Alias { get; set; }

    public IReadOnlyList<PortSignature> InputSignature => _inputs;
    public IReadOnlyList<PortSignature> OutputSignature => _outputs;
    public IReadOnlyCollection<string> MessageInputs => _messageHandlers.Keys;
    public IReadOnlyCollection<string> MessageOutputs => _messageOutputs;

    /// <summary>Output items per input item, used to scale tag offsets (GNU Radio's <c>relative_rate</c>).</summary>
    public double RelativeRate { get; protected set; } = 1.0;

    public TagPropagationPolicy TagPropagation { get; protected set; } = TagPropagationPolicy.AllToAll;

    /// <summary>
    /// Upper bound on items produced per <see cref="GeneralWork"/> call; lower values reduce latency
    /// (GNU Radio's <c>set_max_noutput_items</c>). Zero means unlimited.
    /// </summary>
    public int MaxOutputItems { get; set; }

    /// <summary>Smallest number of input items the block needs before it can run.</summary>
    protected internal virtual int MinimumInputItems => 1;

    /// <summary>Outputs are produced in multiples of this many items.</summary>
    protected internal virtual int OutputMultiple => 1;

    public BlockStatistics Statistics { get; } = new();

    /// <summary>True when the block takes part in streaming (has at least one stream port).</summary>
    public bool HasStreamPorts => _inputs.Count > 0 || _outputs.Count > 0;

    protected PortSignature AddInput<T>(int vectorLength = 1, string? name = null) where T : unmanaged
    {
        var port = new PortSignature(typeof(T), Unsafe.SizeOf<T>(), vectorLength, name ?? $"in{_inputs.Count}", false,
            capacity => new StreamBuffer<T>(capacity, vectorLength));
        _inputs.Add(port);
        return port;
    }

    protected PortSignature AddOutput<T>(int vectorLength = 1, string? name = null, bool optional = false) where T : unmanaged
    {
        var port = new PortSignature(typeof(T), Unsafe.SizeOf<T>(), vectorLength, name ?? $"out{_outputs.Count}", optional,
            capacity => new StreamBuffer<T>(capacity, vectorLength));
        _outputs.Add(port);
        return port;
    }

    /// <summary>Declares a message input; <paramref name="handler"/> runs on the block's own thread.</summary>
    protected void RegisterMessageInput(string port, Action<Pmt> handler) => _messageHandlers[port] = handler;

    protected void RegisterMessageOutput(string port) => _messageOutputs.Add(port);

    /// <summary>Raised on every message published by this block, whether or not anything is connected.</summary>
    public event Action<Block, string, Pmt>? MessagePublished;

    /// <summary>Sends <paramref name="message"/> to every block subscribed to output <paramref name="port"/>.</summary>
    protected void PublishMessage(string port, Pmt message)
    {
        if (!_messageOutputs.Contains(port))
            throw new InvalidOperationException($"{Alias} has no message output '{port}'.");
        if (MessageSubscribers.TryGetValue(port, out var subs))
            foreach (var (block, inPort) in subs)
                block.PostMessage(inPort, message);
        MessagePublished?.Invoke(this, port, message);
    }

    /// <summary>
    /// Queues a message for the input <paramref name="port"/>. Safe from any thread, so UIs use it for
    /// runtime control. When the flowgraph is not running the message is handled immediately.
    /// </summary>
    public void PostMessage(string port, Pmt message)
    {
        if (!_messageHandlers.ContainsKey(port))
            throw new InvalidOperationException($"{Alias} has no message input '{port}'.");
        if (IsRunning)
        {
            _messageQueue.Enqueue((port, message));
            Wake();
        }
        else
        {
            _messageHandlers[port](message);
        }
    }

    internal bool IsRunning { get; set; }

    internal bool HasPendingMessages => !_messageQueue.IsEmpty;

    internal void DeliverMessages()
    {
        while (_messageQueue.TryDequeue(out var item))
            _messageHandlers[item.Port](item.Message);
    }

    internal void Wake() => Signal.Set();

    /// <summary>Called on the scheduler thread before the first <see cref="GeneralWork"/>.</summary>
    public virtual void Start()
    {
    }

    /// <summary>Called on the scheduler thread after the block finished or the flowgraph stopped.</summary>
    public virtual void Stop()
    {
    }

    /// <summary>
    /// Processes as many items as the context allows. Read with <see cref="WorkContext.Input{T}"/>,
    /// write with <see cref="WorkContext.Output{T}"/>, then report what was used with
    /// <see cref="WorkContext.Consume"/> and <see cref="WorkContext.Produce"/>.
    /// Return <see cref="WorkResult.Done"/> when the block will never produce again.
    /// </summary>
    protected internal abstract WorkResult GeneralWork(WorkContext context);

    public override string ToString() => Alias;
}

/// <summary>Return value of <see cref="Block.GeneralWork"/>.</summary>
public enum WorkResult
{
    Ok,
    Done,
}
