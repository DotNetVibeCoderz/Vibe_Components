using System.Buffers.Binary;
using System.Text;

namespace GeoPandas.Net.Data.FlatGeobufInternal;

/// <summary>
/// Minimal FlatBuffers builder (back-to-front, same algorithm and alignment rules as the official builders)
/// — just enough for the FlatGeobuf header and feature tables, without a code generator or package dependency.
/// </summary>
internal sealed class FbBuilder
{
    private byte[] _buf;
    private int _space;
    private int _minAlign = 1;
    private int[] _vtable = new int[16];
    private int _vtableInUse;
    private int _objectStart;

    public FbBuilder(int initialSize = 1024)
    {
        _buf = new byte[Math.Max(64, initialSize)];
        _space = _buf.Length;
    }

    public int Offset => _buf.Length - _space;

    public void Clear()
    {
        _space = _buf.Length;
        _minAlign = 1;
        _vtableInUse = 0;
    }

    private void Grow()
    {
        int old = _buf.Length;
        var nb = new byte[old * 2];
        Buffer.BlockCopy(_buf, 0, nb, old, old);
        _buf = nb;
        _space += old;
    }

    private void Pad(int n) { for (int i = 0; i < n; i++) _buf[--_space] = 0; }

    /// <summary>Aligns so that after writing <paramref name="additional"/> bytes, a <paramref name="size"/>-byte value is aligned.</summary>
    public void Prep(int size, int additional)
    {
        if (size > _minAlign) _minAlign = size;
        int alignSize = (~(_buf.Length - _space + additional) + 1) & (size - 1);
        while (_space < alignSize + size + additional) Grow();
        Pad(alignSize);
    }

    public void PutByte(byte v) { _buf[--_space] = v; }
    public void PutUShort(ushort v) { _space -= 2; BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_space), v); }
    public void PutShort(short v) { _space -= 2; BinaryPrimitives.WriteInt16LittleEndian(_buf.AsSpan(_space), v); }
    public void PutInt(int v) { _space -= 4; BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_space), v); }
    public void PutUInt(uint v) { _space -= 4; BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_space), v); }
    public void PutULong(ulong v) { _space -= 8; BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(_space), v); }
    public void PutDouble(double v) { _space -= 8; BinaryPrimitives.WriteDoubleLittleEndian(_buf.AsSpan(_space), v); }

    public void AddByte(byte v) { Prep(1, 0); PutByte(v); }
    public void AddUShort(ushort v) { Prep(2, 0); PutUShort(v); }
    public void AddInt(int v) { Prep(4, 0); PutInt(v); }
    public void AddULong(ulong v) { Prep(8, 0); PutULong(v); }

    public void AddOffset(int off)
    {
        Prep(4, 0);
        if (off > Offset) throw new InvalidOperationException("FlatBuffers offset points forward.");
        PutInt(Offset - off + 4);
    }

    // ------------------------------------------------------------------ vectors & strings

    public void StartVector(int elemSize, int count, int alignment)
    {
        Prep(4, elemSize * count);
        Prep(alignment, elemSize * count);
    }

    public int EndVector(int count) { PutInt(count); return Offset; }

    public int CreateString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Prep(4, bytes.Length + 1);
        PutByte(0);
        _space -= bytes.Length;
        bytes.CopyTo(_buf.AsSpan(_space));
        return EndVector(bytes.Length);
    }

    public int CreateByteVector(ReadOnlySpan<byte> bytes)
    {
        StartVector(1, bytes.Length, 1);
        _space -= bytes.Length;
        bytes.CopyTo(_buf.AsSpan(_space));
        return EndVector(bytes.Length);
    }

    public int CreateDoubleVector(ReadOnlySpan<double> values)
    {
        StartVector(8, values.Length, 8);
        for (int i = values.Length - 1; i >= 0; i--) PutDouble(values[i]);
        return EndVector(values.Length);
    }

    public int CreateUIntVector(ReadOnlySpan<uint> values)
    {
        StartVector(4, values.Length, 4);
        for (int i = values.Length - 1; i >= 0; i--) PutUInt(values[i]);
        return EndVector(values.Length);
    }

    public int CreateOffsetVector(ReadOnlySpan<int> offsets)
    {
        StartVector(4, offsets.Length, 4);
        for (int i = offsets.Length - 1; i >= 0; i--) AddOffset(offsets[i]);
        return EndVector(offsets.Length);
    }

    // ------------------------------------------------------------------ tables

    public void StartTable(int numFields)
    {
        if (_vtable.Length < numFields) _vtable = new int[numFields];
        Array.Clear(_vtable, 0, numFields);
        _vtableInUse = numFields;
        _objectStart = Offset;
    }

    private void Slot(int field) => _vtable[field] = Offset;

    public void AddFieldByte(int field, byte v, byte def) { if (v == def) return; AddByte(v); Slot(field); }
    public void AddFieldBool(int field, bool v, bool def) { if (v == def) return; AddByte(v ? (byte)1 : (byte)0); Slot(field); }
    public void AddFieldUShort(int field, ushort v, ushort def) { if (v == def) return; AddUShort(v); Slot(field); }
    public void AddFieldInt(int field, int v, int def) { if (v == def) return; AddInt(v); Slot(field); }
    public void AddFieldULong(int field, ulong v, ulong def) { if (v == def) return; AddULong(v); Slot(field); }
    public void AddFieldOffset(int field, int off) { if (off == 0) return; AddOffset(off); Slot(field); }

    public int EndTable()
    {
        AddInt(0);
        int vtableLoc = Offset;
        int i = _vtableInUse - 1;
        while (i >= 0 && _vtable[i] == 0) i--;
        int trimmed = i + 1;
        for (; i >= 0; i--) AddShortRaw((short)(_vtable[i] != 0 ? vtableLoc - _vtable[i] : 0));
        AddShortRaw((short)(vtableLoc - _objectStart));
        AddShortRaw((short)((trimmed + 2) * 2));
        int vt = Offset;
        BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_buf.Length - vtableLoc), vt - vtableLoc);
        _vtableInUse = 0;
        return vtableLoc;
    }

    private void AddShortRaw(short v) { Prep(2, 0); PutShort(v); }

    /// <summary>Finishes with a root offset; returns the buffer bytes (no size prefix).</summary>
    public ReadOnlySpan<byte> Finish(int root)
    {
        // FlatGeobuf stores buffers size-prefixed: align as the official FinishSizePrefixed does
        Prep(_minAlign, 8);
        AddOffset(root);
        return _buf.AsSpan(_space);
    }
}

/// <summary>Zero-copy FlatBuffers table accessor over a span.</summary>
internal readonly ref struct FbTable
{
    private readonly ReadOnlySpan<byte> _buf;
    private readonly int _pos;
    private readonly int _vtable;
    private readonly int _vtableSize;

    public FbTable(ReadOnlySpan<byte> buf, int pos)
    {
        _buf = buf; _pos = pos;
        _vtable = pos - BinaryPrimitives.ReadInt32LittleEndian(buf[pos..]);
        _vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[_vtable..]);
    }

    public static FbTable Root(ReadOnlySpan<byte> buf) => new(buf, (int)BinaryPrimitives.ReadUInt32LittleEndian(buf));

    private int FieldPos(int field)
    {
        int vo = 4 + 2 * field;
        if (vo >= _vtableSize) return 0;
        int off = BinaryPrimitives.ReadUInt16LittleEndian(_buf[(_vtable + vo)..]);
        return off == 0 ? 0 : _pos + off;
    }

    public bool Has(int field) => FieldPos(field) != 0;
    public byte GetByte(int field, byte def = 0) { int p = FieldPos(field); return p == 0 ? def : _buf[p]; }
    public bool GetBool(int field, bool def = false) { int p = FieldPos(field); return p == 0 ? def : _buf[p] != 0; }
    public ushort GetUShort(int field, ushort def = 0) { int p = FieldPos(field); return p == 0 ? def : BinaryPrimitives.ReadUInt16LittleEndian(_buf[p..]); }
    public int GetInt(int field, int def = 0) { int p = FieldPos(field); return p == 0 ? def : BinaryPrimitives.ReadInt32LittleEndian(_buf[p..]); }
    public ulong GetULong(int field, ulong def = 0) { int p = FieldPos(field); return p == 0 ? def : BinaryPrimitives.ReadUInt64LittleEndian(_buf[p..]); }

    private int Indirect(int p) => p + (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf[p..]);

    public string? GetString(int field)
    {
        int p = FieldPos(field);
        if (p == 0) return null;
        int s = Indirect(p);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf[s..]);
        return Encoding.UTF8.GetString(_buf.Slice(s + 4, len));
    }

    /// <summary>Raw bytes of a vector of scalars and its element count.</summary>
    public ReadOnlySpan<byte> GetVectorBytes(int field, int elemSize, out int count)
    {
        int p = FieldPos(field);
        if (p == 0) { count = 0; return default; }
        int s = Indirect(p);
        count = (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf[s..]);
        return _buf.Slice(s + 4, count * elemSize);
    }

    public int GetVectorLength(int field)
    {
        int p = FieldPos(field);
        return p == 0 ? 0 : (int)BinaryPrimitives.ReadUInt32LittleEndian(_buf[Indirect(p)..]);
    }

    /// <summary>Table element <paramref name="index"/> of a vector of tables.</summary>
    public FbTable GetTableElement(int field, int index)
    {
        int s = Indirect(FieldPos(field));
        int e = s + 4 + 4 * index;
        return new FbTable(_buf, Indirect(e));
    }

    public bool TryGetTable(int field, out FbTable table)
    {
        int p = FieldPos(field);
        table = p == 0 ? default : new FbTable(_buf, Indirect(p));
        return p != 0;
    }
}
