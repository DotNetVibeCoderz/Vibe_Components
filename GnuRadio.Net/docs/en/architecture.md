# Architecture

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/architecture.md)

## Blocks and ports

A block (`GnuRadio.Block`, GNU Radio's `gr::block`) declares typed stream ports and named message ports and implements
`GeneralWork(WorkContext)`. Items are `unmanaged` value types: `float`, `Complex32` (binary-identical to GNU Radio's
`gr_complex`), `byte`, `short`, `int`… A port may carry vectors (`AddInput<Complex32>(vectorLength: 1024)`).

Most blocks derive from a simpler base:

| Base | Rate | You implement |
|---|---|---|
| `SyncBlock<TIn,TOut>` | 1 : 1 | `Work(ReadOnlySpan<TIn>, Span<TOut>)` |
| `SyncMultiInputBlock<TIn,TOut>` | N inputs, 1 : 1 | `Work(WorkContext, int items, Span<TOut>)` |
| `DecimatorBlock<TIn,TOut>` | D : 1 | `Work(input of n·D items, output of n items)` |
| `InterpolatorBlock<TIn,TOut>` | 1 : I | `Work(input of n, output of n·I)` |
| `SourceBlock<T>` | — | `int Generate(Span<T>)`, return −1 when exhausted |
| `SinkBlock<T>` | — | `bool Consume(ReadOnlySpan<T>)`, return false to finish |
| `MessageBlock` | messages only | message handlers |
| `HierBlock` | composite | wiring of inner blocks |

A custom block:

```csharp
public sealed class Clipper(float limit) : SyncBlock<float, float>("clipper")
{
    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = Math.Clamp(input[i], -limit, limit);
    }
}
```

General blocks read `context.Input<T>(port)`, write `context.Output<T>(port)`, and report with
`context.Consume(port, n)` and `context.Produce(port, n)`; this is how variable-rate blocks (resamplers, framers,
OFDM receivers) are written. `MinimumInputItems` and `OutputMultiple` tell the scheduler the smallest useful window.

## Buffers

Each connected output owns one `StreamBuffer<T>`: a single-writer, multi-reader ring with absolute item counters.
GNU Radio maps its buffers twice in virtual memory so blocks always see a contiguous window; managed code cannot, so
the buffer keeps a **mirror region** after the primary area and copies writes that touch its head. Readers and writers
therefore always get one contiguous span, even across the wrap point. Publication uses volatile reads/writes, so there
are no locks on the sample path. Buffer size defaults to 256 KB per output (`Flowgraph.BufferBytes`) and grows when a
block needs larger windows; smaller buffers mean lower latency.

## Scheduler

`Flowgraph.Start()` uses the **thread-per-block** scheduler, like GNU Radio's TPB: every block runs on its own thread,
sleeping on a signal that its neighbours set when they produce or consume. A block finishes when its inputs are done
and drained, when all its downstream readers detached (for example after a `Head`), or when `GeneralWork` returns
`WorkResult.Done`. `Flowgraph.RunSynchronously()` runs the same per-block step round-robin on one thread.

Exceptions thrown by a block stop the flowgraph; `Run()` rethrows them as `FlowgraphException`, and `Flowgraph.Error`
reports them as they happen. `Block.Statistics` exposes work calls, items and time in work for profiling.

## Tags

Stream tags (`Tag(Offset, Key, Value)`) mark absolute item offsets — frame starts, detections, timestamps. Blocks add
them with `context.AddTag` and read them with `context.GetTags`. The default policy `AllToAll` copies tags to every
output, scaling the offset by `RelativeRate` (so a decimate-by-10 block divides offsets by 10).

## Messages and PMT

Asynchronous messages carry `Pmt` values — the counterpart of GNU Radio's polymorphic types: booleans, integers,
doubles, complex, symbols, pairs, dictionaries, tuples, and byte/float/complex vectors. A **PDU** is the pair
(metadata dictionary . vector). Blocks declare `RegisterMessageInput("in", handler)` and `RegisterMessageOutput("out")`;
the flowgraph connects them with `ConnectMessage`. Handlers run on the receiving block's thread. `Block.PostMessage`
is thread-safe, so UIs use it for runtime control (for example `SignalSource`'s `cmd` port).
`PmtSerializer` produces GNU Radio's `pmt::serialize` wire format.

## Hierarchical blocks

```csharp
public sealed class Doubler : HierBlock
{
    public Doubler() : base("doubler")
    {
        DeclareInput<float>();
        DeclareOutput<float>();
        var mul = new MultiplyConst<float>(2f);
        ConnectInput(0, mul);
        ConnectOutput(0, mul);
    }
}
```

Hierarchical blocks are flattened before the flowgraph starts, so they add no runtime cost. `WbfmReceive`,
`ConstellationModulator`, `ConstellationReceiver`, `FskModulator` and `ChannelModel` are examples.

## Validation and reconfiguration

`Flowgraph.Validate()` lists unconnected inputs, unused required outputs and type mismatches; `Start()` refuses an
invalid graph. Change connections only while stopped, or use `Reconfigure(fg => …)` (GNU Radio's `lock`/`unlock`).
`Flowgraph.ToDot()` exports Graphviz for documentation.

## Out-of-tree modules

Tag custom blocks with `[GrBlock("my_block", Category = "…")]` and load them with `BlockRegistry.LoadModule(path)`;
`BlockRegistry.Create(id, args…)` instantiates them by name.
