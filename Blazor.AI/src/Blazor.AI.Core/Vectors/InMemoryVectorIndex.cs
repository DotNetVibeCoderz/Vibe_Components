using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using Blazor.AI.Abstractions.Vectors;

namespace Blazor.AI.Core.Vectors;

/// <summary>
/// Thread-safe in-memory vector index. Vectors are normalised on insert and packed into one contiguous
/// <c>float[]</c> slab, so search is a single SIMD dot-product sweep plus a bounded min-heap for top-k.
/// </summary>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private readonly List<VectorRecord?> _records = [];
    private readonly Stack<int> _free = new();
    private float[] _slab = [];
    private int _dimensions;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _slots.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public int Dimensions => _dimensions;

    public ValueTask UpsertAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Upsert(record);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> query, VectorSearchRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new VectorSearchRequest();
        if (request.Top <= 0 || query.IsEmpty)
        {
            return ValueTask.FromResult<IReadOnlyList<VectorSearchHit>>([]);
        }

        var normalized = VectorMath.Normalize(query.Span);
        var heap = new PriorityQueue<int, float>(request.Top + 1);

        _lock.EnterReadLock();
        try
        {
            if (_dimensions != 0 && normalized.Length != _dimensions)
            {
                throw new ArgumentException($"Query has {normalized.Length} dimensions but the index stores {_dimensions}.", nameof(query));
            }

            var slab = _slab.AsSpan();
            for (var slot = 0; slot < _records.Count; slot++)
            {
                var record = _records[slot];
                if (record is null || !Matches(record, request.Filter))
                {
                    continue;
                }

                var score = TensorPrimitives.Dot(slab.Slice(slot * _dimensions, _dimensions), normalized);
                if (score < request.MinScore)
                {
                    continue;
                }

                if (heap.Count < request.Top)
                {
                    heap.Enqueue(slot, score);
                }
                else if (heap.TryPeek(out _, out var lowest) && score > lowest)
                {
                    heap.DequeueEnqueue(slot, score);
                }
            }

            var hits = new VectorSearchHit[heap.Count];
            for (var i = hits.Length - 1; i >= 0; i--)
            {
                heap.TryDequeue(out var slot, out var score);
                hits[i] = new VectorSearchHit(_records[slot]!, score);
            }

            return ValueTask.FromResult<IReadOnlyList<VectorSearchHit>>(hits);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_slots.Remove(id, out var slot))
            {
                return ValueTask.FromResult(false);
            }

            _records[slot] = null;
            _slab.AsSpan(slot * _dimensions, _dimensions).Clear();
            _free.Push(slot);
            return ValueTask.FromResult(true);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try
        {
            _slots.Clear();
            _records.Clear();
            _free.Clear();
            _slab = [];
            _dimensions = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<VectorRecord> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        VectorRecord[] snapshot;
        _lock.EnterReadLock();
        try
        {
            snapshot = _records.Where(r => r is not null).ToArray()!;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        foreach (var record in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }

        await Task.CompletedTask;
    }

    private void Upsert(VectorRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(record.Id);
        var vector = record.Vector.Span;
        if (_dimensions == 0)
        {
            _dimensions = vector.Length;
        }
        else if (vector.Length != _dimensions)
        {
            throw new ArgumentException($"Record '{record.Id}' has {vector.Length} dimensions but the index stores {_dimensions}.");
        }

        if (!_slots.TryGetValue(record.Id, out var slot))
        {
            slot = _free.Count > 0 ? _free.Pop() : _records.Count;
            if (slot == _records.Count)
            {
                _records.Add(null);
                EnsureCapacity(_records.Count);
            }

            _slots[record.Id] = slot;
        }

        var target = _slab.AsSpan(slot * _dimensions, _dimensions);
        vector.CopyTo(target);
        var norm = TensorPrimitives.Norm(target);
        if (norm > 0)
        {
            TensorPrimitives.Divide(target, norm, target);
        }

        _records[slot] = record;
    }

    private void EnsureCapacity(int slots)
    {
        var needed = slots * _dimensions;
        if (_slab.Length >= needed)
        {
            return;
        }

        var grown = new float[Math.Max(needed, Math.Max(_dimensions * 64, _slab.Length * 2))];
        _slab.CopyTo(grown, 0);
        _slab = grown;
    }

    private static bool Matches(VectorRecord record, IReadOnlyDictionary<string, string>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            return true;
        }

        if (record.Metadata is null)
        {
            return false;
        }

        foreach (var (key, value) in filter)
        {
            if (!record.Metadata.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
