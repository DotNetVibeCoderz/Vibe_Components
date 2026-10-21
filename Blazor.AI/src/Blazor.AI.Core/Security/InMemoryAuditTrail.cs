using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace Blazor.AI.Core.Security;

/// <summary>Bounded ring buffer of audit entries. Replace with a database-backed <see cref="IAuditTrail"/> for compliance needs.</summary>
public sealed class InMemoryAuditTrail : IAuditTrail
{
    private readonly AuditEntry[] _buffer;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public InMemoryAuditTrail(IOptions<BlazorAIOptions> options)
        : this(options.Value.Guardrails.AuditCapacity)
    {
    }

    public InMemoryAuditTrail(int capacity = 2_000)
    {
        _buffer = new AuditEntry[Math.Max(16, capacity)];
    }

    public event Action<AuditEntry>? Recorded;

    public void Record(AuditEntry entry)
    {
        lock (_gate)
        {
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }

        Recorded?.Invoke(entry);
    }

    /// <summary>Most recent entries first.</summary>
    public IReadOnlyList<AuditEntry> Snapshot(int max = int.MaxValue)
    {
        lock (_gate)
        {
            var take = Math.Min(max, _count);
            var result = new AuditEntry[take];
            for (var i = 0; i < take; i++)
            {
                result[i] = _buffer[(_next - 1 - i + _buffer.Length) % _buffer.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
    }
}
