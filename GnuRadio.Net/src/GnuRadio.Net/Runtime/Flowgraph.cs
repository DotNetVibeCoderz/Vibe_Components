using System.Text;

namespace GnuRadio;

/// <summary>How a <see cref="Flowgraph"/> executes its blocks.</summary>
public enum SchedulerKind
{
    /// <summary>One dedicated thread per block, like GNU Radio's TPB scheduler. The default.</summary>
    ThreadPerBlock,

    /// <summary>All blocks on the calling thread, round-robin. Deterministic; used by tests and WebAssembly.</summary>
    SingleThreaded,
}

/// <summary>
/// The top-level container of a signal-processing graph (GNU Radio's <c>top_block</c>).
/// Connect blocks, then <see cref="Start"/>/<see cref="Stop"/>/<see cref="Wait()"/> or <see cref="Run"/>.
/// </summary>
/// <example>
/// <code>
/// var fg = new Flowgraph("tone");
/// var src = new SignalSource&lt;float&gt;(48_000, Waveform.Sine, 1_000, 0.5f);
/// var head = new Head&lt;float&gt;(48_000);
/// var sink = new VectorSink&lt;float&gt;();
/// fg.Connect(src, head, sink);
/// fg.Run();
/// </code>
/// </example>
public sealed class Flowgraph : IDisposable
{
    private readonly List<Edge> _edges = [];
    private readonly List<MessageEdge> _messageEdges = [];
    private readonly HashSet<Block> _looseBlocks = [];
    private readonly Lock _lock = new();
    private Scheduler? _scheduler;

    public Flowgraph(string name = "flowgraph")
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Target size of each stream buffer in bytes; buffers grow when a block needs larger windows.</summary>
    public int BufferBytes { get; set; } = 256 * 1024;

    public IReadOnlyList<Edge> Edges => _edges;
    public IReadOnlyList<MessageEdge> MessageEdges => _messageEdges;

    public bool IsRunning => _scheduler is { IsRunning: true };

    /// <summary>Raised (on a scheduler thread) when a block throws; the flowgraph then stops.</summary>
    public event Action<Block, Exception>? Error;

    /// <summary>Exceptions thrown by blocks during the last run.</summary>
    public IReadOnlyList<(Block Block, Exception Exception)> Errors => _scheduler?.Errors ?? [];

    /// <summary>Connects <paramref name="source"/>:<paramref name="sourcePort"/> to <paramref name="destination"/>:<paramref name="destinationPort"/>.</summary>
    public Flowgraph Connect(Block source, int sourcePort, Block destination, int destinationPort)
    {
        EnsureStopped();
        if ((uint)sourcePort >= (uint)source.OutputSignature.Count)
            throw new ArgumentOutOfRangeException(nameof(sourcePort), $"{source.Alias} has {source.OutputSignature.Count} output(s).");
        if ((uint)destinationPort >= (uint)destination.InputSignature.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationPort), $"{destination.Alias} has {destination.InputSignature.Count} input(s).");
        CheckCompatible(source.OutputSignature[sourcePort], destination.InputSignature[destinationPort], source, destination);
        _edges.Add(new Edge(new Endpoint(source, sourcePort), new Endpoint(destination, destinationPort)));
        return this;
    }

    /// <summary>Connects port 0 of each block to port 0 of the next.</summary>
    public Flowgraph Connect(params Block[] chain)
    {
        if (chain.Length == 1)
        {
            Add(chain[0]);
            return this;
        }
        for (var i = 0; i + 1 < chain.Length; i++)
            Connect(chain[i], 0, chain[i + 1], 0);
        return this;
    }

    /// <summary>Connects a message output to a message input.</summary>
    public Flowgraph ConnectMessage(Block source, string sourcePort, Block destination, string destinationPort)
    {
        EnsureStopped();
        if (!source.MessageOutputs.Contains(sourcePort))
            throw new ArgumentException($"{source.Alias} has no message output '{sourcePort}'.", nameof(sourcePort));
        if (!destination.MessageInputs.Contains(destinationPort))
            throw new ArgumentException($"{destination.Alias} has no message input '{destinationPort}'.", nameof(destinationPort));
        _messageEdges.Add(new MessageEdge(source, sourcePort, destination, destinationPort));
        return this;
    }

    /// <summary>Adds a block that has no stream connections (for example a message-only block).</summary>
    public Flowgraph Add(Block block)
    {
        EnsureStopped();
        _looseBlocks.Add(block);
        return this;
    }

    public void Disconnect(Block source, int sourcePort, Block destination, int destinationPort)
    {
        EnsureStopped();
        _edges.Remove(new Edge(new Endpoint(source, sourcePort), new Endpoint(destination, destinationPort)));
    }

    public void DisconnectAll()
    {
        EnsureStopped();
        _edges.Clear();
        _messageEdges.Clear();
        _looseBlocks.Clear();
    }

    /// <summary>
    /// Stops the flowgraph, applies <paramref name="change"/> (connect/disconnect), and starts it again.
    /// Equivalent to GNU Radio's <c>lock()</c>/<c>unlock()</c>.
    /// </summary>
    public void Reconfigure(Action<Flowgraph> change)
    {
        var wasRunning = IsRunning;
        if (wasRunning) Stop();
        change(this);
        if (wasRunning) Start();
    }

    /// <summary>Flattens hierarchical blocks and returns the blocks and edges that actually run.</summary>
    public (IReadOnlyList<Block> Blocks, IReadOnlyList<Edge> Edges, IReadOnlyList<MessageEdge> MessageEdges) Flatten()
    {
        var blocks = new List<Block>();
        var seen = new HashSet<Block>();
        var edges = new List<Edge>();
        var messageEdges = new List<MessageEdge>();

        void AddBlock(Block b)
        {
            if (b is HierBlock) return;
            if (seen.Add(b)) blocks.Add(b);
        }

        var pendingEdges = new Queue<Edge>(_edges);
        var pendingMessages = new Queue<MessageEdge>(_messageEdges);
        var expanded = new HashSet<HierBlock>();

        void Expand(Block b)
        {
            if (b is not HierBlock h || !expanded.Add(h)) return;
            foreach (var e in h.InnerEdges) pendingEdges.Enqueue(e);
            foreach (var m in h.InnerMessageEdges) pendingMessages.Enqueue(m);
            foreach (var pad in h.InputPads)
                foreach (var ep in pad) Expand(ep.Block);
            foreach (var ep in h.OutputPads)
                if (ep is { } e) Expand(e.Block);
        }

        foreach (var b in _looseBlocks) { Expand(b); AddBlock(b); }

        while (pendingEdges.Count > 0)
        {
            var e = pendingEdges.Dequeue();
            Expand(e.Source.Block);
            Expand(e.Destination.Block);
            var src = ResolveSource(e.Source);
            foreach (var dst in ResolveDestinations(e.Destination))
            {
                AddBlock(src.Block);
                AddBlock(dst.Block);
                edges.Add(new Edge(src, dst));
            }
        }

        while (pendingMessages.Count > 0)
        {
            var m = pendingMessages.Dequeue();
            Expand(m.Source);
            Expand(m.Destination);
            AddBlock(m.Source);
            AddBlock(m.Destination);
            messageEdges.Add(m);
        }

        // Inner blocks that only have message connections inside a hier block.
        foreach (var h in expanded)
            foreach (var m in h.InnerMessageEdges)
            {
                AddBlock(m.Source);
                AddBlock(m.Destination);
            }

        return (blocks, edges, messageEdges);
    }

    private static Endpoint ResolveSource(Endpoint ep)
    {
        while (ep.Block is HierBlock h)
            ep = h.OutputPads[ep.Port] ?? throw new InvalidOperationException($"Output {ep.Port} of hier block {h.Alias} is not connected inside.");
        return ep;
    }

    private static IEnumerable<Endpoint> ResolveDestinations(Endpoint ep)
    {
        if (ep.Block is not HierBlock h)
        {
            yield return ep;
            yield break;
        }
        if (h.InputPads[ep.Port].Count == 0)
            throw new InvalidOperationException($"Input {ep.Port} of hier block {h.Alias} is not connected inside.");
        foreach (var inner in h.InputPads[ep.Port])
            foreach (var r in ResolveDestinations(inner))
                yield return r;
    }

    /// <summary>
    /// Checks the flattened graph: every input connected exactly once, every required output used,
    /// item types and vector lengths matching. Returns human-readable problems; empty when valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        (IReadOnlyList<Block> blocks, IReadOnlyList<Edge> edges, _) = Flatten();

        foreach (var e in edges)
        {
            var o = e.Source.Block.OutputSignature[e.Source.Port];
            var i = e.Destination.Block.InputSignature[e.Destination.Port];
            if (o.ItemType != i.ItemType || o.VectorLength != i.VectorLength)
                problems.Add($"{e}: {o} does not match {i}.");
        }

        foreach (var b in blocks)
        {
            for (var p = 0; p < b.InputSignature.Count; p++)
            {
                var count = edges.Count(e => e.Destination.Block == b && e.Destination.Port == p);
                if (count == 0) problems.Add($"{b.Alias}: input {p} is not connected.");
                else if (count > 1) problems.Add($"{b.Alias}: input {p} has {count} connections; an input takes exactly one.");
            }
            for (var p = 0; p < b.OutputSignature.Count; p++)
            {
                if (b.OutputSignature[p].Optional) continue;
                if (!edges.Any(e => e.Source.Block == b && e.Source.Port == p))
                    problems.Add($"{b.Alias}: output {p} is not connected.");
            }
        }
        return problems;
    }

    /// <summary>Starts the flowgraph in the background.</summary>
    public void Start(SchedulerKind kind = SchedulerKind.ThreadPerBlock)
    {
        lock (_lock)
        {
            if (IsRunning) throw new InvalidOperationException("The flowgraph is already running.");
            var problems = Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException("The flowgraph is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            var (blocks, edges, messageEdges) = Flatten();
            _scheduler = new Scheduler(this, blocks, edges, messageEdges, BufferBytes);
            _scheduler.ErrorRaised += (b, ex) => Error?.Invoke(b, ex);
            if (kind == SchedulerKind.ThreadPerBlock) _scheduler.StartThreads();
        }
        if (kind == SchedulerKind.SingleThreaded) _scheduler!.RunSingleThreaded(CancellationToken.None);
    }

    /// <summary>Asks every block to stop and waits for the threads to exit.</summary>
    public void Stop()
    {
        _scheduler?.Stop();
    }

    /// <summary>Blocks until every block has finished (sources exhausted, sinks done, or <see cref="Stop"/>).</summary>
    public void Wait() => _scheduler?.Wait();

    /// <summary>Waits up to <paramref name="timeout"/>; returns false if blocks are still running.</summary>
    public bool Wait(TimeSpan timeout) => _scheduler?.Wait(timeout) ?? true;

    /// <summary>Runs to completion on dedicated threads.</summary>
    public void Run()
    {
        Start();
        Wait();
        ThrowIfFailed();
    }

    /// <summary>Runs to completion on the calling thread, deterministically.</summary>
    public void RunSynchronously(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (IsRunning) throw new InvalidOperationException("The flowgraph is already running.");
            var problems = Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException("The flowgraph is not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
            var (blocks, edges, messageEdges) = Flatten();
            _scheduler = new Scheduler(this, blocks, edges, messageEdges, BufferBytes);
            _scheduler.ErrorRaised += (b, ex) => Error?.Invoke(b, ex);
        }
        _scheduler.RunSingleThreaded(cancellationToken);
        ThrowIfFailed();
    }

    /// <summary>Starts, runs for <paramref name="duration"/>, then stops.</summary>
    public async Task RunForAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Start();
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Stop();
        }
    }

    private void ThrowIfFailed()
    {
        var errors = Errors;
        if (errors.Count == 1) throw new FlowgraphException(errors[0].Block, errors[0].Exception);
        if (errors.Count > 1) throw new AggregateException(errors.Select(e => new FlowgraphException(e.Block, e.Exception)));
    }

    /// <summary>Graphviz DOT description of the flattened graph, for documentation and debugging.</summary>
    public string ToDot()
    {
        var (blocks, edges, messageEdges) = Flatten();
        var sb = new StringBuilder();
        sb.AppendLine($"digraph \"{Name}\" {{");
        sb.AppendLine("  rankdir=LR; node [shape=box, style=rounded, fontname=\"Helvetica\"];");
        foreach (var b in blocks) sb.AppendLine($"  \"{b.Alias}\" [label=\"{b.Name}\"];");
        foreach (var e in edges) sb.AppendLine($"  \"{e.Source.Block.Alias}\" -> \"{e.Destination.Block.Alias}\" [label=\"{e.Source.Port}→{e.Destination.Port}\"];");
        foreach (var m in messageEdges) sb.AppendLine($"  \"{m.Source.Alias}\" -> \"{m.Destination.Alias}\" [style=dashed, label=\"{m.SourcePort}\"];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EnsureStopped()
    {
        if (IsRunning) throw new InvalidOperationException("Stop the flowgraph (or use Reconfigure) before changing connections.");
    }

    private static void CheckCompatible(PortSignature output, PortSignature input, Block source, Block destination)
    {
        if (output.ItemType != input.ItemType || output.VectorLength != input.VectorLength)
            throw new ArgumentException($"Cannot connect {source.Alias} ({output}) to {destination.Alias} ({input}): item types differ.");
    }

    public void Dispose() => Stop();
}

/// <summary>Wraps an exception thrown by a block while the flowgraph ran.</summary>
public sealed class FlowgraphException(Block block, Exception inner)
    : Exception($"Block {block.Alias} ({block.Name}) failed: {inner.Message}", inner)
{
    public Block Block { get; } = block;
}
