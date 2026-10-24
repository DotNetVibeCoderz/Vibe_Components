using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GnuRadio;

/// <summary>Kind of value held by a <see cref="Pmt"/>.</summary>
public enum PmtKind
{
    Nil,
    Bool,
    Integer,
    Real,
    Complex,
    Symbol,
    Pair,
    Dict,
    Tuple,
    U8Vector,
    F32Vector,
    C32Vector,
    Blob,
}

/// <summary>
/// Polymorphic message value, the .NET counterpart of GNU Radio's PMT ("polymorphic type").
/// Used for asynchronous messages between blocks and for stream tags. Instances are immutable;
/// vectors are exposed as read-only memory so a message can be fanned out to many blocks safely.
/// </summary>
public sealed class Pmt : IEquatable<Pmt>
{
    private readonly object? _value;
    private readonly Pmt? _car;
    private readonly Pmt? _cdr;

    private Pmt(PmtKind kind, object? value, Pmt? car = null, Pmt? cdr = null)
    {
        Kind = kind;
        _value = value;
        _car = car;
        _cdr = cdr;
    }

    public PmtKind Kind { get; }

    public static Pmt Nil { get; } = new(PmtKind.Nil, null);
    public static Pmt True { get; } = new(PmtKind.Bool, true);
    public static Pmt False { get; } = new(PmtKind.Bool, false);

    public static Pmt From(bool value) => value ? True : False;
    public static Pmt From(long value) => new(PmtKind.Integer, value);
    public static Pmt From(int value) => new(PmtKind.Integer, (long)value);
    public static Pmt From(double value) => new(PmtKind.Real, value);
    public static Pmt From(Complex32 value) => new(PmtKind.Complex, value);

    /// <summary>A symbol (interned string), GNU Radio's <c>pmt::intern</c>.</summary>
    public static Pmt Symbol(string value) => new(PmtKind.Symbol, value);

    public static Pmt From(string value) => Symbol(value);
    public static Pmt Cons(Pmt car, Pmt cdr) => new(PmtKind.Pair, null, car, cdr);
    public static Pmt Tuple(params Pmt[] items) => new(PmtKind.Tuple, ImmutableArray.Create(items));
    public static Pmt Vector(ReadOnlySpan<byte> data) => new(PmtKind.U8Vector, data.ToArray());
    public static Pmt Vector(ReadOnlySpan<float> data) => new(PmtKind.F32Vector, data.ToArray());
    public static Pmt Vector(ReadOnlySpan<Complex32> data) => new(PmtKind.C32Vector, data.ToArray());
    public static Pmt Blob(ReadOnlySpan<byte> data) => new(PmtKind.Blob, data.ToArray());

    public static Pmt Dict(IEnumerable<KeyValuePair<string, Pmt>> entries) =>
        new(PmtKind.Dict, entries.ToImmutableDictionary());

    public static Pmt Dict(params (string Key, Pmt Value)[] entries) =>
        new(PmtKind.Dict, entries.ToImmutableDictionary(e => e.Key, e => e.Value));

    public static Pmt EmptyDict { get; } = new(PmtKind.Dict, ImmutableDictionary<string, Pmt>.Empty);

    /// <summary>
    /// A PDU, GNU Radio's (metadata . vector) pair: the standard shape of packet messages
    /// passed between framing, CRC and FEC blocks.
    /// </summary>
    public static Pmt Pdu(Pmt metadata, Pmt vector)
    {
        if (metadata.Kind != PmtKind.Dict && metadata.Kind != PmtKind.Nil)
            throw new ArgumentException("PDU metadata must be a dictionary or nil.", nameof(metadata));
        return Cons(metadata, vector);
    }

    public static Pmt Pdu(ReadOnlySpan<byte> data) => Pdu(EmptyDict, Vector(data));

    public bool IsNil => Kind == PmtKind.Nil;
    public bool IsPdu => Kind == PmtKind.Pair && _car!.Kind is PmtKind.Dict or PmtKind.Nil && _cdr!.IsVector;
    public bool IsVector => Kind is PmtKind.U8Vector or PmtKind.F32Vector or PmtKind.C32Vector;

    public Pmt Car => Kind == PmtKind.Pair ? _car! : throw WrongKind(PmtKind.Pair);
    public Pmt Cdr => Kind == PmtKind.Pair ? _cdr! : throw WrongKind(PmtKind.Pair);

    public bool ToBool() => Kind == PmtKind.Bool ? (bool)_value! : throw WrongKind(PmtKind.Bool);

    public long ToInt64() => Kind switch
    {
        PmtKind.Integer => (long)_value!,
        PmtKind.Real => (long)(double)_value!,
        _ => throw WrongKind(PmtKind.Integer),
    };

    public double ToDouble() => Kind switch
    {
        PmtKind.Real => (double)_value!,
        PmtKind.Integer => (long)_value!,
        _ => throw WrongKind(PmtKind.Real),
    };

    public Complex32 ToComplex() => Kind switch
    {
        PmtKind.Complex => (Complex32)_value!,
        PmtKind.Real => new Complex32((float)(double)_value!, 0),
        PmtKind.Integer => new Complex32((long)_value!, 0),
        _ => throw WrongKind(PmtKind.Complex),
    };

    public string ToSymbol() => Kind == PmtKind.Symbol ? (string)_value! : throw WrongKind(PmtKind.Symbol);

    public ReadOnlySpan<byte> AsBytes() => Kind is PmtKind.U8Vector or PmtKind.Blob ? (byte[])_value! : throw WrongKind(PmtKind.U8Vector);
    public ReadOnlySpan<float> AsFloats() => Kind == PmtKind.F32Vector ? (float[])_value! : throw WrongKind(PmtKind.F32Vector);
    public ReadOnlySpan<Complex32> AsComplex() => Kind == PmtKind.C32Vector ? (Complex32[])_value! : throw WrongKind(PmtKind.C32Vector);

    public ImmutableArray<Pmt> AsTuple() => Kind == PmtKind.Tuple ? (ImmutableArray<Pmt>)_value! : throw WrongKind(PmtKind.Tuple);

    public ImmutableDictionary<string, Pmt> AsDict() => Kind switch
    {
        PmtKind.Dict => (ImmutableDictionary<string, Pmt>)_value!,
        PmtKind.Nil => ImmutableDictionary<string, Pmt>.Empty,
        _ => throw WrongKind(PmtKind.Dict),
    };

    /// <summary>Returns a new dictionary with <paramref name="key"/> set; this instance is unchanged.</summary>
    public Pmt DictAdd(string key, Pmt value) => new(PmtKind.Dict, AsDict().SetItem(key, value));

    public Pmt DictRef(string key, Pmt notFound) => AsDict().TryGetValue(key, out var v) ? v : notFound;

    public static implicit operator Pmt(long value) => From(value);
    public static implicit operator Pmt(int value) => From(value);
    public static implicit operator Pmt(double value) => From(value);
    public static implicit operator Pmt(bool value) => From(value);
    public static implicit operator Pmt(string value) => Symbol(value);
    public static implicit operator Pmt(Complex32 value) => From(value);

    private InvalidCastException WrongKind(PmtKind expected) =>
        new($"PMT of kind {Kind} cannot be read as {expected}.");

    public bool Equals(Pmt? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            PmtKind.Nil => true,
            PmtKind.Pair => _car!.Equals(other._car) && _cdr!.Equals(other._cdr),
            PmtKind.U8Vector or PmtKind.Blob => AsBytes().SequenceEqual(other.AsBytes()),
            PmtKind.F32Vector => AsFloats().SequenceEqual(other.AsFloats()),
            PmtKind.C32Vector => AsComplex().SequenceEqual(other.AsComplex()),
            PmtKind.Tuple => AsTuple().SequenceEqual(other.AsTuple()),
            PmtKind.Dict => AsDict().Count == other.AsDict().Count &&
                            AsDict().All(kv => other.AsDict().TryGetValue(kv.Key, out var v) && kv.Value.Equals(v)),
            _ => Equals(_value, other._value),
        };
    }

    public override bool Equals(object? obj) => Equals(obj as Pmt);

    public override int GetHashCode() => Kind switch
    {
        PmtKind.Pair => HashCode.Combine(_car, _cdr),
        PmtKind.Integer or PmtKind.Real or PmtKind.Bool or PmtKind.Symbol or PmtKind.Complex => HashCode.Combine(Kind, _value),
        _ => Kind.GetHashCode(),
    };

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    private void Write(StringBuilder sb)
    {
        switch (Kind)
        {
            case PmtKind.Nil: sb.Append("()"); break;
            case PmtKind.Bool: sb.Append((bool)_value! ? "#t" : "#f"); break;
            case PmtKind.Integer: sb.Append(((long)_value!).ToString(CultureInfo.InvariantCulture)); break;
            case PmtKind.Real: sb.Append(((double)_value!).ToString("G", CultureInfo.InvariantCulture)); break;
            case PmtKind.Complex: sb.Append(((Complex32)_value!).ToString()); break;
            case PmtKind.Symbol: sb.Append((string)_value!); break;
            case PmtKind.Pair:
                sb.Append('(');
                _car!.Write(sb);
                sb.Append(" . ");
                _cdr!.Write(sb);
                sb.Append(')');
                break;
            case PmtKind.Tuple:
                sb.Append("{");
                sb.AppendJoin(' ', AsTuple());
                sb.Append('}');
                break;
            case PmtKind.Dict:
                sb.Append('(');
                sb.AppendJoin(' ', AsDict().Select(kv => $"({kv.Key} . {kv.Value})"));
                sb.Append(')');
                break;
            case PmtKind.U8Vector: sb.Append("#u8[").Append(AsBytes().Length).Append(']'); break;
            case PmtKind.F32Vector: sb.Append("#f32[").Append(AsFloats().Length).Append(']'); break;
            case PmtKind.C32Vector: sb.Append("#c32[").Append(AsComplex().Length).Append(']'); break;
            case PmtKind.Blob: sb.Append("#blob[").Append(AsBytes().Length).Append(']'); break;
        }
    }
}

/// <summary>
/// A stream tag: metadata attached to an absolute item offset of a stream
/// (GNU Radio's <c>tag_t</c>). Tags flow downstream alongside the samples.
/// </summary>
public readonly record struct Tag(long Offset, string Key, Pmt Value, string? Source = null);
