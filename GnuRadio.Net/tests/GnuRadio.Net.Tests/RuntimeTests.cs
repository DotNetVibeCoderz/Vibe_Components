using GnuRadio.Blocks;
using GnuRadio.Filter;

namespace GnuRadio.Tests;

public class RuntimeTests
{
    private static float[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (float)i).ToArray();

    [Theory]
    [InlineData(SchedulerKind.ThreadPerBlock)]
    [InlineData(SchedulerKind.SingleThreaded)]
    public void VectorSourceToSinkPreservesEveryItem(SchedulerKind kind)
    {
        var data = Ramp(200_000);
        var fg = new Flowgraph();
        var src = new VectorSource<float>(data);
        var sink = new VectorSink<float>();
        fg.Connect(src, new Copy<float>(), sink);
        Run(fg, kind);
        Assert.Equal(data, sink.Data);
    }

    [Fact]
    public void FanOutDeliversToEveryReader()
    {
        var data = Ramp(50_000);
        var fg = new Flowgraph();
        var src = new VectorSource<float>(data);
        var a = new VectorSink<float>();
        var b = new VectorSink<float>();
        fg.Connect(src, 0, a, 0);
        fg.Connect(src, 0, b, 0);
        fg.Run();
        Assert.Equal(data, a.Data);
        Assert.Equal(data, b.Data);
    }

    [Fact]
    public void HeadStopsAnInfiniteSource()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new NullSource<float>(), new Head<float>(12_345), sink);
        fg.Run();
        Assert.Equal(12_345, sink.Count);
    }

    [Fact]
    public void AddCombinesTwoStreams()
    {
        var fg = new Flowgraph();
        var add = new Add<float>();
        var sink = new VectorSink<float>();
        fg.Connect(new VectorSource<float>(Ramp(1000)), 0, add, 0);
        fg.Connect(new VectorSource<float>(Ramp(1000)), 0, add, 1);
        fg.Connect(add, sink);
        fg.RunSynchronously();
        Assert.Equal(Ramp(1000).Select(x => 2 * x), sink.Data);
    }

    [Fact]
    public void DecimatorAcrossBufferWrapStaysContiguous()
    {
        // Odd decimation and a small buffer force reads across the ring's wrap point.
        var data = Ramp(100_003);
        var fg = new Flowgraph { BufferBytes = 4096 };
        var keep = new KeepOneInN<float>(7);
        var sink = new VectorSink<float>();
        fg.Connect(new VectorSource<float>(data), keep, sink);
        fg.Run();
        var expected = Enumerable.Range(0, data.Length / 7).Select(i => (float)(i * 7 + 6)).ToArray();
        Assert.Equal(expected, sink.Data);
    }

    [Fact]
    public void StreamToVectorRoundTrips()
    {
        var data = Ramp(64 * 100);
        var fg = new Flowgraph { BufferBytes = 4096 };
        var sink = new VectorSink<float>();
        fg.Connect(new VectorSource<float>(data), new StreamToVector<float>(64), new VectorToStream<float>(64), sink);
        fg.Run();
        Assert.Equal(data, sink.Data);
    }

    [Fact]
    public void TagsPropagateThroughSyncBlocksAndScaleThroughDecimators()
    {
        var tags = new[] { new Tag(10, "a", 1), new Tag(700, "b", 2) };
        var fg = new Flowgraph();
        var src = new VectorSource<float>(Ramp(1000), tags: tags);
        var sink = new VectorSink<float>();
        fg.Connect(src, new MultiplyConst<float>(2f), new KeepOneInN<float>(10), sink);
        fg.Run();
        var got = sink.Tags.OrderBy(t => t.Offset).ToArray();
        Assert.Equal(2, got.Length);
        Assert.Equal(1, got[0].Offset);
        Assert.Equal("a", got[0].Key);
        Assert.Equal(70, got[1].Offset);
    }

    [Fact]
    public void ValidationReportsUnconnectedInputs()
    {
        var fg = new Flowgraph();
        var add = new Add<float>();
        fg.Connect(new NullSource<float>(), 0, add, 0);
        fg.Connect(add, new NullSink<float>());
        Assert.Contains(fg.Validate(), p => p.Contains("input 1 is not connected"));
        Assert.Throws<InvalidOperationException>(() => fg.Start());
    }

    [Fact]
    public void ConnectRejectsTypeMismatch()
    {
        var fg = new Flowgraph();
        Assert.Throws<ArgumentException>(() => fg.Connect(new NullSource<float>(), new NullSink<Complex32>()));
    }

    [Fact]
    public void MessagesFlowBetweenBlocks()
    {
        var fg = new Flowgraph();
        var pdu = new StreamToPdu<byte>(4);
        var debug = new MessageDebug();
        fg.Connect(new VectorSource<byte>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), pdu);
        fg.ConnectMessage(pdu, "pdus", debug, "store");
        fg.Run();
        Assert.Equal(2, debug.Count);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, debug.Messages[1].Cdr.AsBytes().ToArray());
    }

    [Fact]
    public void ExceptionsInBlocksSurfaceAsFlowgraphException()
    {
        var fg = new Flowgraph();
        fg.Connect(new NullSource<float>(), new Thrower(), new NullSink<float>());
        var ex = Assert.Throws<FlowgraphException>(() => fg.Run());
        Assert.IsType<Thrower>(ex.Block);
    }

    [Fact]
    public void HierBlocksAreFlattened()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new VectorSource<float>(Ramp(100)), new DoubleThenAddOne(), sink);
        fg.Run();
        Assert.Equal(Ramp(100).Select(x => 2 * x + 1), sink.Data);
        Assert.DoesNotContain(fg.Flatten().Blocks, b => b is HierBlock);
    }

    [Fact]
    public void StopEndsAnUnboundedFlowgraph()
    {
        var fg = new Flowgraph();
        fg.Connect(new NullSource<Complex32>(), new Throttle<Complex32>(1e6), new NullSink<Complex32>());
        fg.Start();
        Thread.Sleep(100);
        Assert.True(fg.IsRunning);
        fg.Stop();
        Assert.False(fg.IsRunning);
    }

    [Fact]
    public void ThrottleLimitsRate()
    {
        var fg = new Flowgraph();
        var sink = new VectorSink<float>();
        fg.Connect(new NullSource<float>(), new Throttle<float>(10_000), new Head<float>(3_000), sink);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        fg.Run();
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.2, 1.5);
    }

    private static void Run(Flowgraph fg, SchedulerKind kind)
    {
        if (kind == SchedulerKind.SingleThreaded) fg.RunSynchronously();
        else fg.Run();
    }

    private sealed class Thrower() : SyncBlock<float, float>("thrower")
    {
        protected override void Work(ReadOnlySpan<float> input, Span<float> output) => throw new InvalidOperationException("boom");
    }

    private sealed class DoubleThenAddOne : HierBlock
    {
        public DoubleThenAddOne() : base("double_then_add_one")
        {
            DeclareInput<float>();
            DeclareOutput<float>();
            var mul = new MultiplyConst<float>(2f);
            var add = new AddConst<float>(1f);
            Connect(mul, add);
            ConnectInput(0, mul);
            ConnectOutput(0, add);
        }
    }
}
