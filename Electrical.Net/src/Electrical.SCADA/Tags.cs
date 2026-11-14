using Electrical.Core.Hosting;
using Electrical.SCADA.Modbus;

namespace Electrical.SCADA;

/// <summary>Data quality of a tag value (simplified OPC / IEC 61850 quality).</summary>
public enum TagQuality
{
    /// <summary>Value is valid and current.</summary>
    Good,
    /// <summary>Value may be inaccurate (e.g. outside the scaling range, substituted).</summary>
    Uncertain,
    /// <summary>Value is invalid.</summary>
    Bad,
    /// <summary>Last communication with the device failed; value is stale.</summary>
    CommFailure,
    /// <summary>Tag has never been read.</summary>
    NotConnected,
}

/// <summary>Raw data type of a tag in the Modbus map. 32-bit types occupy two registers (high word first).</summary>
public enum TagDataType { Bool, Int16, UInt16, Int32, UInt32, Float32 }

/// <summary>Modbus location of a tag.</summary>
/// <param name="UnitId">Modbus unit (slave) identifier.</param>
/// <param name="Table">Data table.</param>
/// <param name="Address">Zero-based protocol address.</param>
/// <param name="DataType">Raw representation.</param>
public sealed record TagAddress(byte UnitId, ModbusTable Table, ushort Address, TagDataType DataType = TagDataType.UInt16)
{
    /// <summary>Number of registers (or bits) the tag occupies.</summary>
    public int Width => DataType is TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 ? 2 : 1;

    /// <summary>Decodes a raw numeric value from registers starting at index 0 of <paramref name="regs"/>.</summary>
    public double Decode(ReadOnlySpan<ushort> regs) => DataType switch
    {
        TagDataType.Int16 => (short)regs[0],
        TagDataType.UInt16 or TagDataType.Bool => regs[0],
        TagDataType.Int32 => ModbusData.ToInt32(regs[0], regs[1]),
        TagDataType.UInt32 => ModbusData.ToUInt32(regs[0], regs[1]),
        _ => ModbusData.ToFloat(regs[0], regs[1]),
    };
}

/// <summary>
/// Linear raw → engineering-unit scaling: EU = EuMin + (raw − RawMin)·(EuMax − EuMin)/(RawMax − RawMin).
/// Example: a 4–20 mA transducer digitized to 0–32767 representing 0–150 kV.
/// </summary>
public sealed record TagScaling(double RawMin, double RawMax, double EuMin, double EuMax)
{
    public static TagScaling Identity { get; } = new(0, 1, 0, 1);

    public double Gain => (EuMax - EuMin) / (RawMax - RawMin);
    public double Scale(double raw) => EuMin + (raw - RawMin) * Gain;
    public double Unscale(double eu) => RawMin + (eu - EuMin) / Gain;
    public bool InRawRange(double raw) => raw >= Math.Min(RawMin, RawMax) && raw <= Math.Max(RawMin, RawMax);
}

/// <summary>A process point: engineering value with unit, quality and timestamp, optionally bound to a Modbus address.</summary>
public sealed class Tag
{
    public Tag(string name, string? unit = null, TagAddress? address = null, TagScaling? scaling = null, string? description = null)
    {
        Name = name; Unit = unit; Address = address; Scaling = scaling; Description = description;
    }

    public string Name { get; }
    public string? Description { get; init; }
    /// <summary>Engineering unit (e.g. "kV", "MW", "pu").</summary>
    public string? Unit { get; init; }
    public TagAddress? Address { get; init; }
    /// <summary>Optional raw→EU scaling (null = raw value is already in engineering units).</summary>
    public TagScaling? Scaling { get; init; }
    /// <summary>Last raw value read from the device.</summary>
    public double RawValue { get; internal set; } = double.NaN;
    /// <summary>Current value in engineering units.</summary>
    public double Value { get; internal set; } = double.NaN;
    public TagQuality Quality { get; internal set; } = TagQuality.NotConnected;
    /// <summary>Source timestamp of the current value.</summary>
    public DateTimeOffset Timestamp { get; internal set; }

    public TagValue Snapshot => new(Value, Quality, Timestamp);

    public override string ToString() => $"{Name} = {Value:G6} {Unit} [{Quality}] @ {Timestamp:O}";
}

/// <summary>Value/quality/time triple.</summary>
public readonly record struct TagValue(double Value, TagQuality Quality, DateTimeOffset Timestamp);

/// <summary>Event published on the grid event bus when a tag value or quality changes.</summary>
public sealed record TagValueChanged(string Tag, double Value, double PreviousValue, TagQuality Quality, string? Unit, DateTimeOffset Timestamp) : IGridEvent;

/// <summary>
/// Real-time tag database. Values enter through <see cref="UpdateRaw"/>, <see cref="UpdateValue"/>, <see cref="ScanAsync"/> (Modbus TCP
/// polling) or <see cref="Scan(ModbusRegisterMap, byte, DateTimeOffset?)"/> (in-process map). Changes raise <see cref="Changed"/> and are published
/// as <see cref="TagValueChanged"/> on the optional <see cref="IGridEventBus"/>.
/// </summary>
public sealed class TagDatabase(IGridEventBus? bus = null, TimeProvider? time = null)
{
    private readonly Dictionary<string, Tag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Tag> _order = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Raised synchronously after each value/quality change.</summary>
    public event Action<TagValueChanged>? Changed;

    public int Count { get { lock (_gate) return _order.Count; } }

    /// <summary>Tags in insertion order.</summary>
    public IReadOnlyList<Tag> All { get { lock (_gate) return _order.ToArray(); } }

    public Tag Add(Tag tag)
    {
        lock (_gate)
        {
            if (!_tags.TryAdd(tag.Name, tag)) throw new ArgumentException($"Tag '{tag.Name}' already exists.");
            _order.Add(tag);
        }
        return tag;
    }

    public Tag Add(string name, string? unit = null, TagAddress? address = null, TagScaling? scaling = null, string? description = null) =>
        Add(new Tag(name, unit, address, scaling, description));

    public Tag this[string name] => Get(name);

    public Tag Get(string name) { lock (_gate) return _tags.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"Tag '{name}' not found."); }

    public bool TryGet(string name, out Tag? tag) { lock (_gate) return _tags.TryGetValue(name, out tag); }

    /// <summary>Stores a raw device value; applies scaling and flags out-of-range raw values as <see cref="TagQuality.Uncertain"/>.</summary>
    public void UpdateRaw(string name, double raw, DateTimeOffset? timestamp = null, TagQuality quality = TagQuality.Good)
    {
        var t = Get(name);
        double eu = t.Scaling?.Scale(raw) ?? raw;
        if (quality == TagQuality.Good && t.Scaling is { } s && !s.InRawRange(raw)) quality = TagQuality.Uncertain;
        Set(t, raw, eu, quality, timestamp ?? _time.GetUtcNow());
    }

    /// <summary>Stores a value already in engineering units.</summary>
    public void UpdateValue(string name, double value, DateTimeOffset? timestamp = null, TagQuality quality = TagQuality.Good)
    {
        var t = Get(name);
        Set(t, t.Scaling?.Unscale(value) ?? value, value, quality, timestamp ?? _time.GetUtcNow());
    }

    /// <summary>Changes only the quality (e.g. communication failure), keeping the last value.</summary>
    public void SetQuality(string name, TagQuality quality, DateTimeOffset? timestamp = null)
    {
        var t = Get(name);
        Set(t, t.RawValue, t.Value, quality, timestamp ?? _time.GetUtcNow());
    }

    private void Set(Tag t, double raw, double eu, TagQuality q, DateTimeOffset ts)
    {
        double prev;
        bool changed;
        lock (t)
        {
            prev = t.Value;
            changed = !prev.Equals(eu) || t.Quality != q;
            t.RawValue = raw; t.Value = eu; t.Quality = q; t.Timestamp = ts;
        }
        if (!changed) return;
        var e = new TagValueChanged(t.Name, eu, prev, q, t.Unit, ts);
        Changed?.Invoke(e);
        bus?.Publish(e);
    }

    /// <summary>Reads all addressed tags of <paramref name="unitId"/> directly from an in-process register map.</summary>
    public void Scan(ModbusRegisterMap map, byte unitId, DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? _time.GetUtcNow();
        foreach (var t in All)
        {
            if (t.Address is not { } a || a.UnitId != unitId) continue;
            double raw = a.Table is ModbusTable.Coil or ModbusTable.DiscreteInput
                ? (map.GetBit(a.Table, a.Address) ? 1 : 0)
                : a.Decode(map.ReadRegisters(a.Table, a.Address, a.Width));
            UpdateRaw(t.Name, raw, ts);
        }
    }

    /// <summary>
    /// Polls all addressed tags over Modbus TCP (one request per tag). Failed reads mark the tag
    /// <see cref="TagQuality.CommFailure"/> (timeouts/socket errors) or <see cref="TagQuality.Bad"/> (exception responses).
    /// </summary>
    /// <returns>Number of tags read successfully.</returns>
    public async Task<int> ScanAsync(ModbusTcpClient client, CancellationToken ct = default)
    {
        int ok = 0;
        foreach (var t in All)
        {
            if (t.Address is not { } a) continue;
            try
            {
                double raw = a.Table switch
                {
                    ModbusTable.Coil => (await client.ReadCoilsAsync(a.Address, 1, a.UnitId, ct).ConfigureAwait(false))[0] ? 1 : 0,
                    ModbusTable.DiscreteInput => (await client.ReadDiscreteInputsAsync(a.Address, 1, a.UnitId, ct).ConfigureAwait(false))[0] ? 1 : 0,
                    ModbusTable.InputRegister => a.Decode(await client.ReadInputRegistersAsync(a.Address, (ushort)a.Width, a.UnitId, ct).ConfigureAwait(false)),
                    _ => a.Decode(await client.ReadHoldingRegistersAsync(a.Address, (ushort)a.Width, a.UnitId, ct).ConfigureAwait(false)),
                };
                UpdateRaw(t.Name, raw);
                ok++;
            }
            catch (ModbusException) { SetQuality(t.Name, TagQuality.Bad); }
            catch (Exception e) when (e is TimeoutException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                SetQuality(t.Name, TagQuality.CommFailure);
            }
        }
        return ok;
    }
}
