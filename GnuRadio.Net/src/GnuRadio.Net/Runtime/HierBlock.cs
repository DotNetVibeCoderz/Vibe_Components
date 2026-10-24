namespace GnuRadio;

/// <summary>
/// A block built from other blocks (GNU Radio's <c>hier_block2</c>). Declare the outer ports with
/// <see cref="Block.AddInput{T}"/>/<see cref="Block.AddOutput{T}"/> through <see cref="DeclareInput{T}"/>
/// and <see cref="DeclareOutput{T}"/>, wire the inside with <see cref="Connect(Block, int, Block, int)"/>,
/// and bind ports with <see cref="ConnectInput"/> and <see cref="ConnectOutput"/>.
/// The flowgraph flattens hierarchical blocks before running, so they cost nothing at runtime.
/// </summary>
public abstract class HierBlock : Block
{
    internal readonly List<Edge> InnerEdges = [];
    internal readonly List<MessageEdge> InnerMessageEdges = [];
    internal readonly List<List<Endpoint>> InputPads = [];
    internal readonly List<Endpoint?> OutputPads = [];

    protected HierBlock(string name) : base(name)
    {
    }

    protected void DeclareInput<T>(int vectorLength = 1, string? name = null) where T : unmanaged
    {
        AddInput<T>(vectorLength, name);
        InputPads.Add([]);
    }

    protected void DeclareOutput<T>(int vectorLength = 1, string? name = null) where T : unmanaged
    {
        AddOutput<T>(vectorLength, name);
        OutputPads.Add(null);
    }

    protected void Connect(Block source, int sourcePort, Block destination, int destinationPort) =>
        InnerEdges.Add(new Edge(new Endpoint(source, sourcePort), new Endpoint(destination, destinationPort)));

    /// <summary>Chains blocks port 0 to port 0.</summary>
    protected void Connect(params Block[] chain)
    {
        for (var i = 0; i + 1 < chain.Length; i++)
            Connect(chain[i], 0, chain[i + 1], 0);
    }

    protected void ConnectMessage(Block source, string sourcePort, Block destination, string destinationPort) =>
        InnerMessageEdges.Add(new MessageEdge(source, sourcePort, destination, destinationPort));

    /// <summary>Routes outer input <paramref name="pad"/> into an inner block's input.</summary>
    protected void ConnectInput(int pad, Block destination, int destinationPort = 0) =>
        InputPads[pad].Add(new Endpoint(destination, destinationPort));

    /// <summary>Routes an inner block's output to outer output <paramref name="pad"/>.</summary>
    protected void ConnectOutput(int pad, Block source, int sourcePort = 0) =>
        OutputPads[pad] = new Endpoint(source, sourcePort);

    protected internal sealed override WorkResult GeneralWork(WorkContext context) =>
        throw new InvalidOperationException("Hierarchical blocks are flattened and never scheduled.");
}

/// <summary>A (block, port) pair.</summary>
public readonly record struct Endpoint(Block Block, int Port)
{
    public override string ToString() => $"{Block.Alias}:{Port}";
}

/// <summary>A stream connection.</summary>
public readonly record struct Edge(Endpoint Source, Endpoint Destination)
{
    public override string ToString() => $"{Source} -> {Destination}";
}

/// <summary>A message connection.</summary>
public readonly record struct MessageEdge(Block Source, string SourcePort, Block Destination, string DestinationPort)
{
    public override string ToString() => $"{Source.Alias}[{SourcePort}] -> {Destination.Alias}[{DestinationPort}]";
}
