using System.Buffers.Binary;
using System.Text;

namespace GnuRadio;

/// <summary>
/// Binary PMT encoding compatible with GNU Radio's <c>pmt::serialize</c>/<c>pmt::deserialize</c> for the common
/// types (booleans, symbols, integers, doubles, complex, pairs, dictionaries, tuples, and u8/f32/c32 vectors).
/// This is the wire format of GNU Radio's ZeroMQ message blocks and of PDUs exchanged with GNU Radio.
/// </summary>
public static class PmtSerializer
{
    private const byte True = 0x00;
    private const byte False = 0x01;
    private const byte SymbolTag = 0x02;
    private const byte Int32Tag = 0x03;
    private const byte DoubleTag = 0x04;
    private const byte ComplexTag = 0x05;
    private const byte NullTag = 0x06;
    private const byte PairTag = 0x07;
    private const byte VectorTag = 0x08;
    private const byte DictTag = 0x09;
    private const byte UniformVectorTag = 0x0A;
    private const byte UInt64Tag = 0x0B;
    private const byte TupleTag = 0x0C;
    private const byte Int64Tag = 0x0D;

    private const byte UvU8 = 0x00;
    private const byte UvF32 = 0x08;
    private const byte UvF64 = 0x09;
    private const byte UvC32 = 0x0A;

    public static byte[] Serialize(Pmt value)
    {
        using var ms = new MemoryStream();
        Write(ms, value);
        return ms.ToArray();
    }

    public static Pmt Deserialize(ReadOnlySpan<byte> data)
    {
        var position = 0;
        return Read(data, ref position);
    }

    private static void Write(Stream s, Pmt v)
    {
        Span<byte> b = stackalloc byte[16];
        switch (v.Kind)
        {
            case PmtKind.Bool:
                s.WriteByte(v.ToBool() ? True : False);
                break;
            case PmtKind.Nil:
                s.WriteByte(NullTag);
                break;
            case PmtKind.Symbol:
            {
                var bytes = Encoding.UTF8.GetBytes(v.ToSymbol());
                s.WriteByte(SymbolTag);
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)bytes.Length);
                s.Write(b[..2]);
                s.Write(bytes);
                break;
            }
            case PmtKind.Integer:
                s.WriteByte(Int64Tag);
                BinaryPrimitives.WriteInt64BigEndian(b, v.ToInt64());
                s.Write(b[..8]);
                break;
            case PmtKind.Real:
                s.WriteByte(DoubleTag);
                BinaryPrimitives.WriteDoubleBigEndian(b, v.ToDouble());
                s.Write(b[..8]);
                break;
            case PmtKind.Complex:
            {
                var c = v.ToComplex();
                s.WriteByte(ComplexTag);
                BinaryPrimitives.WriteDoubleBigEndian(b, c.Real);
                BinaryPrimitives.WriteDoubleBigEndian(b[8..], c.Imaginary);
                s.Write(b[..16]);
                break;
            }
            case PmtKind.Pair:
                s.WriteByte(PairTag);
                Write(s, v.Car);
                Write(s, v.Cdr);
                break;
            case PmtKind.Dict:
            {
                // GNU Radio dicts are association lists: (k . v) consed onto the rest, nil-terminated.
                var entries = v.AsDict().ToArray();
                for (var i = 0; i < entries.Length; i++)
                {
                    s.WriteByte(DictTag);
                    Write(s, Pmt.Cons(Pmt.Symbol(entries[i].Key), entries[i].Value));
                }
                s.WriteByte(NullTag);
                break;
            }
            case PmtKind.Tuple:
            {
                var items = v.AsTuple();
                s.WriteByte(TupleTag);
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)items.Length);
                s.Write(b[..4]);
                foreach (var item in items) Write(s, item);
                break;
            }
            case PmtKind.U8Vector:
            case PmtKind.Blob:
                WriteUniformHeader(s, UvU8, v.AsBytes().Length);
                s.Write(v.AsBytes());
                break;
            case PmtKind.F32Vector:
                WriteUniformHeader(s, UvF32, v.AsFloats().Length);
                foreach (var f in v.AsFloats())
                {
                    BinaryPrimitives.WriteSingleBigEndian(b, f);
                    s.Write(b[..4]);
                }
                break;
            case PmtKind.C32Vector:
                WriteUniformHeader(s, UvC32, v.AsComplex().Length);
                foreach (var c in v.AsComplex())
                {
                    BinaryPrimitives.WriteSingleBigEndian(b, c.Real);
                    BinaryPrimitives.WriteSingleBigEndian(b[4..], c.Imaginary);
                    s.Write(b[..8]);
                }
                break;
            default:
                throw new NotSupportedException($"Cannot serialise PMT of kind {v.Kind}.");
        }
    }

    private static void WriteUniformHeader(Stream s, byte type, int length)
    {
        Span<byte> b = stackalloc byte[4];
        s.WriteByte(UniformVectorTag);
        s.WriteByte(type);
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)length);
        s.Write(b);
        s.WriteByte(1);
        s.WriteByte(0);
    }

    private static Pmt Read(ReadOnlySpan<byte> d, ref int p)
    {
        var tag = d[p++];
        switch (tag)
        {
            case True: return Pmt.True;
            case False: return Pmt.False;
            case NullTag: return Pmt.Nil;
            case SymbolTag:
            {
                var len = BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
                p += 2;
                var sym = Encoding.UTF8.GetString(d.Slice(p, len));
                p += len;
                return Pmt.Symbol(sym);
            }
            case Int32Tag:
            {
                var v = BinaryPrimitives.ReadInt32BigEndian(d[p..]);
                p += 4;
                return Pmt.From(v);
            }
            case Int64Tag:
            case UInt64Tag:
            {
                var v = BinaryPrimitives.ReadInt64BigEndian(d[p..]);
                p += 8;
                return Pmt.From(v);
            }
            case DoubleTag:
            {
                var v = BinaryPrimitives.ReadDoubleBigEndian(d[p..]);
                p += 8;
                return Pmt.From(v);
            }
            case ComplexTag:
            {
                var re = BinaryPrimitives.ReadDoubleBigEndian(d[p..]);
                var im = BinaryPrimitives.ReadDoubleBigEndian(d[(p + 8)..]);
                p += 16;
                return Pmt.From(new Complex32((float)re, (float)im));
            }
            case PairTag:
            {
                var car = Read(d, ref p);
                var cdr = Read(d, ref p);
                return Pmt.Cons(car, cdr);
            }
            case DictTag:
            {
                var entries = new List<KeyValuePair<string, Pmt>>();
                p--;
                while (d[p] == DictTag)
                {
                    p++;
                    var pair = Read(d, ref p);
                    entries.Add(new(pair.Car.Kind == PmtKind.Symbol ? pair.Car.ToSymbol() : pair.Car.ToString(), pair.Cdr));
                }
                Read(d, ref p); // terminating nil
                return Pmt.Dict(entries);
            }
            case TupleTag:
            case VectorTag:
            {
                var len = (int)BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
                p += 4;
                var items = new Pmt[len];
                for (var i = 0; i < len; i++) items[i] = Read(d, ref p);
                return Pmt.Tuple(items);
            }
            case UniformVectorTag:
            {
                var type = d[p++];
                var len = (int)BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
                p += 4;
                var pad = d[p++];
                p += pad;
                switch (type)
                {
                    case UvU8:
                    {
                        var bytes = d.Slice(p, len);
                        p += len;
                        return Pmt.Vector(bytes);
                    }
                    case UvF32:
                    {
                        var f = new float[len];
                        for (var i = 0; i < len; i++, p += 4) f[i] = BinaryPrimitives.ReadSingleBigEndian(d[p..]);
                        return Pmt.Vector(f);
                    }
                    case UvF64:
                    {
                        var f = new float[len];
                        for (var i = 0; i < len; i++, p += 8) f[i] = (float)BinaryPrimitives.ReadDoubleBigEndian(d[p..]);
                        return Pmt.Vector(f);
                    }
                    case UvC32:
                    {
                        var c = new Complex32[len];
                        for (var i = 0; i < len; i++, p += 8)
                            c[i] = new Complex32(BinaryPrimitives.ReadSingleBigEndian(d[p..]), BinaryPrimitives.ReadSingleBigEndian(d[(p + 4)..]));
                        return Pmt.Vector(c);
                    }
                    default:
                        throw new NotSupportedException($"Uniform vector type 0x{type:X2} is not supported.");
                }
            }
            default:
                throw new InvalidDataException($"Unknown PMT tag 0x{tag:X2} at offset {p - 1}.");
        }
    }
}
