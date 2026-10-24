namespace GnuRadio.Blocks;

/// <summary>Discards everything (GNU Radio's <c>blocks.null_sink</c>).</summary>
public sealed class NullSink<T>(int vectorLength = 1) : SinkBlock<T>("null_sink", vectorLength) where T : unmanaged
{
    protected override bool Consume(ReadOnlySpan<T> input) => true;
}

/// <summary>
/// Collects every item and tag in memory (GNU Radio's <c>blocks.vector_sink</c>). Reading
/// <see cref="Data"/> while the flowgraph runs returns a snapshot.
/// </summary>
public sealed class VectorSink<T> : Block where T : unmanaged
{
    private readonly List<T> _data = [];
    private readonly List<Tag> _tags = [];
    private readonly List<Tag> _scratch = [];
    private readonly Lock _lock = new();

    public VectorSink(int vectorLength = 1, int reserve = 0) : base("vector_sink")
    {
        AddInput<T>(vectorLength);
        VectorLength = vectorLength;
        _data.Capacity = reserve;
        TagPropagation = TagPropagationPolicy.None;
    }

    public int VectorLength { get; }

    public T[] Data
    {
        get
        {
            lock (_lock) return [.. _data];
        }
    }

    public Tag[] Tags
    {
        get
        {
            lock (_lock) return [.. _tags];
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _data.Count / VectorLength;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _data.Clear();
            _tags.Clear();
        }
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        if (n == 0) return WorkResult.Ok;
        _scratch.Clear();
        context.GetTags(0, _scratch);
        lock (_lock)
        {
            _data.AddRange(context.Input<T>(0));
            _tags.AddRange(_scratch);
        }
        context.Consume(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>Remembers the most recent item (GNU Radio's <c>blocks.probe_signal</c>).</summary>
public sealed class ProbeSignal<T>() : SinkBlock<T>("probe_signal") where T : unmanaged
{
    private T _level;
    private readonly Lock _lock = new();

    public T Level
    {
        get
        {
            lock (_lock) return _level;
        }
    }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        lock (_lock) _level = input[^1];
        return true;
    }
}

/// <summary>Prints or collects stream tags (GNU Radio's <c>blocks.tag_debug</c>).</summary>
public sealed class TagDebug<T> : Block where T : unmanaged
{
    private readonly List<Tag> _scratch = [];
    private readonly List<Tag> _tags = [];
    private readonly Lock _lock = new();

    public TagDebug(string label = "", bool display = false, string? keyFilter = null) : base("tag_debug")
    {
        AddInput<T>();
        Label = label;
        Display = display;
        KeyFilter = keyFilter;
    }

    public string Label { get; }
    public bool Display { get; set; }
    public string? KeyFilter { get; }

    public Tag[] Tags
    {
        get
        {
            lock (_lock) return [.. _tags];
        }
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.InputItems(0);
        _scratch.Clear();
        context.GetTags(0, _scratch);
        foreach (var t in _scratch)
        {
            if (KeyFilter is not null && t.Key != KeyFilter) continue;
            lock (_lock) _tags.Add(t);
            if (Display) Console.WriteLine($"[{Label}] offset {t.Offset}: {t.Key} = {t.Value} (from {t.Source})");
        }
        context.Consume(0, n);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Collects or prints messages (GNU Radio's <c>blocks.message_debug</c>). Ports: <c>store</c> (keep),
/// <c>print</c> (write to the console and keep). <see cref="MessageReceived"/> fires for each message.
/// </summary>
public sealed class MessageDebug : MessageBlock
{
    private readonly List<Pmt> _messages = [];
    private readonly Lock _lock = new();

    public MessageDebug() : base("message_debug")
    {
        RegisterMessageInput("store", Store);
        RegisterMessageInput("print", m =>
        {
            Console.WriteLine($"[{Alias}] {m}");
            Store(m);
        });
    }

    public event Action<Pmt>? MessageReceived;

    public IReadOnlyList<Pmt> Messages
    {
        get
        {
            lock (_lock) return [.. _messages];
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _messages.Count;
        }
    }

    private void Store(Pmt message)
    {
        lock (_lock) _messages.Add(message);
        MessageReceived?.Invoke(message);
    }

    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }
}

/// <summary>Publishes a fixed message on <c>strobe</c> at a fixed period (GNU Radio's <c>blocks.message_strobe</c>).</summary>
public sealed class MessageStrobe : MessageBlock
{
    private Timer? _timer;

    public MessageStrobe(Pmt message, TimeSpan period) : base("message_strobe")
    {
        Message = message;
        Period = period;
        RegisterMessageOutput("strobe");
        RegisterMessageInput("set_msg", m => Message = m);
    }

    public Pmt Message { get; set; }
    public TimeSpan Period { get; }

    public override void Start() => _timer = new Timer(_ => PublishMessage("strobe", Message), null, Period, Period);

    public override void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
