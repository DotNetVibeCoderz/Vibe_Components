namespace GnuRadio;

/// <summary>Type-erased view of a <see cref="StreamBuffer{T}"/> used by the scheduler.</summary>
internal interface IStreamBuffer
{
    Type ItemType { get; }
    int VectorLength { get; }
    int Capacity { get; }
    long ItemsWritten { get; }
    bool WriterDone { get; }
    int ReaderCount { get; }
    bool AllReadersDetached { get; }
    bool HasTags { get; }

    void SetWriter(Block writer);
    int AddReader(Block reader);
    int ReadableContiguous(int reader);
    long ReadableTotal(int reader);
    long ItemsRead(int reader);
    void Consume(int reader, int items);
    void DetachReader(int reader);
    int WritableContiguous();
    void Produce(int items);
    void MarkWriterDone();
    void AddTag(in Tag tag);
    void GetTags(long start, long end, List<Tag> into);
}

/// <summary>
/// Single-writer, multi-reader ring buffer connecting one output port to any number of inputs.
/// </summary>
/// <remarks>
/// GNU Radio maps its buffers twice into virtual memory so a block always sees a contiguous
/// window. Managed code cannot do that, so this buffer emulates it: the array carries a mirror
/// region of <c>mirror</c> items after the primary <c>capacity</c> items, and every write that
/// touches the first <c>mirror</c> slots (or spills past the end) is copied to its twin. Readers and
/// writers therefore always get one contiguous span of up to <c>mirror + 1</c> items, which is what
/// decimators and vector blocks need to make progress across the wrap point.
/// Indices are absolute item counts (never wrap); publication uses volatile writes so the
/// writer and readers synchronise without locks.
/// </remarks>
public sealed class StreamBuffer<T> : IStreamBuffer where T : unmanaged
{
    private readonly T[] _data;
    private readonly int _capacity;
    private readonly int _mirror;
    private readonly int _vlen;
    private long _written;
    private volatile bool _writerDone;
    private Block? _writer;
    private ReaderState[] _readers = [];

    private readonly List<Tag> _tags = [];
    private readonly Lock _tagLock = new();
    private volatile int _tagCount;

    private sealed class ReaderState
    {
        public long Read;
        public volatile bool Detached;
        public required Block Block;
    }

    public StreamBuffer(int capacity, int vectorLength = 1, int mirror = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorLength, 1);
        _capacity = capacity;
        _mirror = mirror < 0 ? capacity / 2 : Math.Min(mirror, capacity);
        _vlen = vectorLength;
        _data = GC.AllocateUninitializedArray<T>((capacity + _mirror) * vectorLength);
    }

    public Type ItemType => typeof(T);
    public int VectorLength => _vlen;
    public int Capacity => _capacity;
    public long ItemsWritten => Volatile.Read(ref _written);
    public bool WriterDone => _writerDone;
    public int ReaderCount => _readers.Length;
    public bool HasTags => _tagCount > 0;

    public bool AllReadersDetached
    {
        get
        {
            var readers = _readers;
            if (readers.Length == 0) return false;
            foreach (var r in readers)
                if (!r.Detached) return false;
            return true;
        }
    }

    void IStreamBuffer.SetWriter(Block writer) => _writer = writer;

    int IStreamBuffer.AddReader(Block reader)
    {
        _readers = [.. _readers, new ReaderState { Block = reader, Read = Volatile.Read(ref _written) }];
        return _readers.Length - 1;
    }

    private long MinRead()
    {
        var written = Volatile.Read(ref _written);
        var min = written;
        foreach (var r in _readers)
        {
            if (r.Detached) continue;
            var read = Volatile.Read(ref r.Read);
            if (read < min) min = read;
        }
        return min;
    }

    public int WritableContiguous()
    {
        var written = _written;
        var free = _capacity - (int)(written - MinRead());
        var contiguous = _capacity + _mirror - (int)(written % _capacity);
        return Math.Min(free, contiguous);
    }

    /// <summary>Writable window of <paramref name="items"/> items (elements = items × vector length).</summary>
    public Span<T> WriteSpan(int items) => _data.AsSpan((int)(_written % _capacity) * _vlen, items * _vlen);

    public void Produce(int items)
    {
        if (items <= 0) return;
        var start = (int)(_written % _capacity);
        var end = start + items;

        // Keep the mirror region and the head of the primary region identical.
        if (start < _mirror)
        {
            var len = Math.Min(end, _mirror) - start;
            _data.AsSpan(start * _vlen, len * _vlen).CopyTo(_data.AsSpan((start + _capacity) * _vlen));
        }
        if (end > _capacity)
        {
            var len = end - _capacity;
            _data.AsSpan(_capacity * _vlen, len * _vlen).CopyTo(_data.AsSpan(0, len * _vlen));
        }

        Volatile.Write(ref _written, _written + items);
        foreach (var r in _readers)
            r.Block.Wake();
    }

    public int ReadableContiguous(int reader)
    {
        var r = _readers[reader];
        var read = r.Read;
        var available = (int)(Volatile.Read(ref _written) - read);
        var contiguous = _capacity + _mirror - (int)(read % _capacity);
        return Math.Min(available, contiguous);
    }

    public long ReadableTotal(int reader) => Volatile.Read(ref _written) - _readers[reader].Read;

    public long ItemsRead(int reader) => _readers[reader].Read;

    public ReadOnlySpan<T> ReadSpan(int reader, int items) =>
        _data.AsSpan((int)(_readers[reader].Read % _capacity) * _vlen, items * _vlen);

    public void Consume(int reader, int items)
    {
        if (items <= 0) return;
        var r = _readers[reader];
        Volatile.Write(ref r.Read, r.Read + items);
        if (_tagCount > 0) PruneTags();
        _writer?.Wake();
    }

    public void DetachReader(int reader)
    {
        _readers[reader].Detached = true;
        _writer?.Wake();
    }

    public void MarkWriterDone()
    {
        _writerDone = true;
        foreach (var r in _readers)
            r.Block.Wake();
    }

    public void AddTag(in Tag tag)
    {
        lock (_tagLock)
        {
            _tags.Add(tag);
            _tagCount = _tags.Count;
        }
    }

    public void GetTags(long start, long end, List<Tag> into)
    {
        if (_tagCount == 0) return;
        lock (_tagLock)
        {
            foreach (var t in _tags)
                if (t.Offset >= start && t.Offset < end)
                    into.Add(t);
        }
    }

    private void PruneTags()
    {
        var min = MinRead();
        lock (_tagLock)
        {
            _tags.RemoveAll(t => t.Offset < min);
            _tagCount = _tags.Count;
        }
    }
}
