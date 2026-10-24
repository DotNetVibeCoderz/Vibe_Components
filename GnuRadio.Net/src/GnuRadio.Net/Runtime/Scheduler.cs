using System.Diagnostics;

namespace GnuRadio;

/// <summary>
/// Executes a flattened graph. The thread-per-block mode gives every block its own thread that sleeps
/// on the block's signal until a neighbour produces or consumes; the single-threaded mode runs the
/// same per-block step round-robin on the caller's thread.
/// </summary>
internal sealed class Scheduler
{
    private enum Step
    {
        Progress,
        Idle,
        Done,
    }

    private sealed class Executor
    {
        public required Block Block;
        public required WorkContext Context;
        public required int[] OutputsWithReaders;
        public bool Done;
        public bool Started;
        public readonly List<Tag> TagScratch = [];
    }

    private readonly Flowgraph _owner;
    private readonly Executor[] _executors;
    private readonly List<Thread> _threads = [];
    private readonly List<(Block, Exception)> _errors = [];
    private volatile bool _stopRequested;
    private volatile bool _streamingDone;
    private int _activeStreamBlocks;
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(20);

    public Scheduler(Flowgraph owner, IReadOnlyList<Block> blocks, IReadOnlyList<Edge> edges, IReadOnlyList<MessageEdge> messageEdges, int bufferBytes)
    {
        _owner = owner;

        foreach (var b in blocks) b.MessageSubscribers.Clear();
        foreach (var m in messageEdges)
        {
            if (!m.Source.MessageSubscribers.TryGetValue(m.SourcePort, out var list))
                m.Source.MessageSubscribers[m.SourcePort] = list = [];
            list.Add((m.Destination, m.DestinationPort));
        }

        // One buffer per connected output port, shared by all readers of that port.
        var buffers = new Dictionary<Endpoint, IStreamBuffer>();
        foreach (var group in edges.GroupBy(e => e.Source))
        {
            var src = group.Key;
            var sig = src.Block.OutputSignature[src.Port];
            var unit = Math.Max(src.Block.OutputMultiple, group.Max(e => e.Destination.Block.MinimumInputItems));
            if (src.Block.MaxOutputItems > 0) unit = Math.Max(unit, src.Block.MaxOutputItems);
            var bytesPerItem = sig.ItemSize * sig.VectorLength;
            var capacity = Math.Max(Math.Max(64, bufferBytes / bytesPerItem), 4 * unit);
            var buffer = sig.CreateBuffer(capacity);
            buffer.SetWriter(src.Block);
            buffers[src] = buffer;
        }

        var executors = new List<Executor>();
        foreach (var b in blocks)
        {
            var ins = new IStreamBuffer[b.InputSignature.Count];
            var readers = new int[ins.Length];
            for (var p = 0; p < ins.Length; p++)
            {
                var edge = edges.First(e => e.Destination.Block == b && e.Destination.Port == p);
                ins[p] = buffers[edge.Source];
                readers[p] = ins[p].AddReader(b);
            }
            var outs = new IStreamBuffer?[b.OutputSignature.Count];
            var withReaders = new List<int>();
            for (var p = 0; p < outs.Length; p++)
            {
                outs[p] = buffers.GetValueOrDefault(new Endpoint(b, p));
                if (outs[p] is not null) withReaders.Add(p);
            }
            executors.Add(new Executor
            {
                Block = b,
                Context = new WorkContext(b, ins, readers, outs),
                OutputsWithReaders = [.. withReaders],
            });
        }
        _executors = [.. executors];
        _activeStreamBlocks = _executors.Count(e => e.Block.HasStreamPorts);
    }

    public bool IsRunning { get; private set; }

    public IReadOnlyList<(Block, Exception)> Errors
    {
        get
        {
            lock (_errors) return [.. _errors];
        }
    }

    public event Action<Block, Exception>? ErrorRaised;

    public void StartThreads()
    {
        IsRunning = true;
        foreach (var ex in _executors)
        {
            ex.Block.IsRunning = true;
            var thread = new Thread(() => ThreadLoop(ex))
            {
                IsBackground = true,
                Name = $"gr:{ex.Block.Alias}",
            };
            _threads.Add(thread);
        }
        foreach (var t in _threads) t.Start();
    }

    public void RunSingleThreaded(CancellationToken cancellationToken)
    {
        IsRunning = true;
        foreach (var ex in _executors) ex.Block.IsRunning = true;
        try
        {
            foreach (var ex in _executors) BeginBlock(ex);
            var spin = new SpinWait();
            while (!_stopRequested && !cancellationToken.IsCancellationRequested)
            {
                var progress = false;
                var allDone = true;
                foreach (var ex in _executors)
                {
                    if (ex.Done) continue;
                    var step = SafeStep(ex);
                    if (step == Step.Progress) progress = true;
                    if (step == Step.Done) { FinishBlock(ex); progress = true; }
                    else allDone = false;
                }
                if (allDone) break;
                if (progress) spin.Reset();
                else spin.SpinOnce(sleep1Threshold: 30);
            }
        }
        finally
        {
            foreach (var ex in _executors)
                if (!ex.Done) FinishBlock(ex);
            Finished();
        }
    }

    private void ThreadLoop(Executor ex)
    {
        var block = ex.Block;
        try
        {
            BeginBlock(ex);
            while (!_stopRequested)
            {
                block.Signal.Reset();
                var step = SafeStep(ex);
                if (step == Step.Done) break;
                if (step == Step.Idle) block.Signal.Wait(IdleWait);
            }
        }
        finally
        {
            FinishBlock(ex);
        }
    }

    private void BeginBlock(Executor ex)
    {
        try
        {
            ex.Block.Start();
            ex.Started = true;
        }
        catch (Exception e)
        {
            Fail(ex.Block, e);
        }
    }

    private Step SafeStep(Executor ex)
    {
        if (!ex.Started) return Step.Done;
        try
        {
            return RunOnce(ex);
        }
        catch (Exception e)
        {
            Fail(ex.Block, e);
            return Step.Done;
        }
    }

    private Step RunOnce(Executor ex)
    {
        var block = ex.Block;
        if (block.HasPendingMessages) block.DeliverMessages();

        if (!block.HasStreamPorts)
            return _streamingDone && !block.HasPendingMessages ? Step.Done : Step.Idle;

        var ctx = ex.Context;
        var minIn = block.MinimumInputItems;

        for (var i = 0; i < ctx.InBuffers.Length; i++)
        {
            var buffer = ctx.InBuffers[i];
            // Read the done flag before the count: once it is set every item is already published.
            var writerDone = buffer.WriterDone;
            var available = buffer.ReadableContiguous(ctx.InReaders[i]);
            var total = buffer.ReadableTotal(ctx.InReaders[i]);
            ctx.InAvailable[i] = available;
            ctx.InDone[i] = writerDone && available == total;
            if (writerDone && total < minIn) return Step.Done;
        }

        if (ex.OutputsWithReaders.Length > 0)
        {
            var allDetached = true;
            foreach (var o in ex.OutputsWithReaders)
                if (!ctx.OutBuffers[o]!.AllReadersDetached) { allDetached = false; break; }
            if (allDetached) return Step.Done;
        }

        for (var i = 0; i < ctx.InAvailable.Length; i++)
            if (ctx.InAvailable[i] < minIn) return Step.Idle;

        ctx.Prepare();
        var multiple = block.OutputMultiple;
        for (var o = 0; o < ctx.OutSpace.Length; o++)
        {
            var space = ctx.OutSpace[o];
            if (block.MaxOutputItems > 0 && space > block.MaxOutputItems) space = block.MaxOutputItems;
            if (multiple > 1) space -= space % multiple;
            if (space < multiple) return Step.Idle;
            ctx.OutSpace[o] = space;
        }

        var started = Stopwatch.GetTimestamp();
        var result = block.GeneralWork(ctx);
        var elapsed = Stopwatch.GetTimestamp() - started;

        var produced = 0L;
        var consumed = 0L;
        foreach (var p in ctx.Produced) produced += p;
        foreach (var c in ctx.Consumed) consumed += c;

        if (block.TagPropagation != TagPropagationPolicy.None && consumed > 0)
            PropagateTags(ex);

        ctx.Commit();

        var stats = block.Statistics;
        Interlocked.Increment(ref stats.WorkCalls);
        Interlocked.Add(ref stats.ItemsProduced, produced);
        Interlocked.Add(ref stats.ItemsConsumed, consumed);
        Interlocked.Add(ref stats.WorkTicks, elapsed);

        if (result == WorkResult.Done) return Step.Done;
        if (produced == 0 && consumed == 0)
        {
            // Nothing moved and an input will never grow: the block cannot finish its work.
            for (var i = 0; i < ctx.InDone.Length; i++)
                if (ctx.InDone[i]) return Step.Done;
            return Step.Idle;
        }
        return Step.Progress;
    }

    private static void PropagateTags(Executor ex)
    {
        var ctx = ex.Context;
        var block = ex.Block;
        var tags = ex.TagScratch;
        var rate = block.RelativeRate;
        for (var i = 0; i < ctx.InBuffers.Length; i++)
        {
            var buffer = ctx.InBuffers[i];
            if (!buffer.HasTags || ctx.Consumed[i] == 0) continue;
            tags.Clear();
            var start = buffer.ItemsRead(ctx.InReaders[i]);
            buffer.GetTags(start, start + ctx.Consumed[i], tags);
            foreach (var tag in tags)
            {
                var offset = rate == 1.0 ? tag.Offset : (long)Math.Round(tag.Offset * rate);
                if (block.TagPropagation == TagPropagationPolicy.AllToAll)
                {
                    for (var o = 0; o < ctx.OutBuffers.Length; o++)
                        ctx.OutBuffers[o]?.AddTag(tag with { Offset = offset });
                }
                else if (i < ctx.OutBuffers.Length)
                {
                    ctx.OutBuffers[i]?.AddTag(tag with { Offset = offset });
                }
            }
        }
    }

    private void FinishBlock(Executor ex)
    {
        if (ex.Done) return;
        ex.Done = true;
        var ctx = ex.Context;
        foreach (var o in ctx.OutBuffers) o?.MarkWriterDone();
        for (var i = 0; i < ctx.InBuffers.Length; i++) ctx.InBuffers[i].DetachReader(ctx.InReaders[i]);
        try
        {
            if (ex.Started) ex.Block.Stop();
        }
        catch (Exception e)
        {
            Fail(ex.Block, e);
        }
        ex.Block.IsRunning = false;

        if (ex.Block.HasStreamPorts && Interlocked.Decrement(ref _activeStreamBlocks) == 0)
        {
            _streamingDone = true;
            foreach (var other in _executors) other.Block.Wake();
        }
    }

    private void Fail(Block block, Exception e)
    {
        lock (_errors) _errors.Add((block, e));
        ErrorRaised?.Invoke(block, e);
        RequestStop();
    }

    private void RequestStop()
    {
        _stopRequested = true;
        foreach (var ex in _executors) ex.Block.Wake();
    }

    public void Stop()
    {
        RequestStop();
        Wait();
    }

    public void Wait()
    {
        foreach (var t in _threads)
            if (t != Thread.CurrentThread) t.Join();
        Finished();
    }

    public bool Wait(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        foreach (var t in _threads)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
            if (!t.Join(remaining)) return false;
        }
        Finished();
        return true;
    }

    private void Finished()
    {
        IsRunning = false;
        foreach (var ex in _executors) ex.Block.IsRunning = false;
    }
}
