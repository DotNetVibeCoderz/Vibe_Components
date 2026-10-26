using GDAL.Net.Native;

namespace GDAL.Net.Vector;

/// <summary>Definition of an attribute field (name, type, width, precision).</summary>
public sealed unsafe class FieldDefinition : GdalObject
{
    internal FieldDefinition(GdalSafeHandle handle) : base(handle) { }

    private static void Release(nint h) => GdalNative.OGR_Fld_Destroy(h);

    /// <summary>Creates a new field definition (to be added with <see cref="Layer.CreateField(FieldDefinition, bool)"/>).</summary>
    public FieldDefinition(string name, FieldType type, int width = 0, int precision = 0, FieldSubType subType = FieldSubType.None)
        : base(new GdalSafeHandle(GdalNative.OGR_Fld_Create(name, (int)type), &Release))
    {
        if (width > 0) GdalNative.OGR_Fld_SetWidth(Handle, width);
        if (precision > 0) GdalNative.OGR_Fld_SetPrecision(Handle, precision);
        if (subType != FieldSubType.None) GdalNative.OGR_Fld_SetSubType(Handle, (int)subType);
    }

    public string Name => Interop.PtrToString(GdalNative.OGR_Fld_GetNameRef(Handle)) ?? string.Empty;
    public FieldType Type => (FieldType)GdalNative.OGR_Fld_GetType(Handle);
    public FieldSubType SubType => (FieldSubType)GdalNative.OGR_Fld_GetSubType(Handle);
    public int Width => GdalNative.OGR_Fld_GetWidth(Handle);
    public int Precision => GdalNative.OGR_Fld_GetPrecision(Handle);

    public bool IsNullable
    {
        get => GdalNative.OGR_Fld_IsNullable(Handle) != 0;
        set => GdalNative.OGR_Fld_SetNullable(Handle, value ? 1 : 0);
    }

    public override string ToString() => $"{Name} ({Type}{(Width > 0 ? $"({Width}{(Precision > 0 ? "," + Precision : "")})" : "")})";
}

/// <summary>Lightweight snapshot of a field schema.</summary>
public readonly record struct FieldInfo(int Index, string Name, FieldType Type, FieldSubType SubType, int Width, int Precision);

/// <summary>Schema of the features of a layer.</summary>
public sealed class FeatureDefinition : GdalObject
{
    internal FeatureDefinition(GdalSafeHandle handle) : base(handle) { }

    public string Name => Interop.PtrToString(GdalNative.OGR_FD_GetName(Handle)) ?? string.Empty;
    public int FieldCount => GdalNative.OGR_FD_GetFieldCount(Handle);
    public GeometryType GeometryType => (GeometryType)GdalNative.OGR_FD_GetGeomType(Handle);

    public FieldDefinition GetField(int index)
    {
        var h = GdalNative.OGR_FD_GetFieldDefn(Handle, index);
        return h == 0 ? throw new ArgumentOutOfRangeException(nameof(index)) : new FieldDefinition(GdalSafeHandle.Borrowed(h, Handle));
    }

    public int GetFieldIndex(string name) => GdalNative.OGR_FD_GetFieldIndex(Handle, name);

    /// <summary>Snapshot of all fields.</summary>
    public IReadOnlyList<FieldInfo> Fields
    {
        get
        {
            var n = FieldCount;
            var list = new FieldInfo[n];
            for (var i = 0; i < n; i++)
            {
                using var f = GetField(i);
                list[i] = new FieldInfo(i, f.Name, f.Type, f.SubType, f.Width, f.Precision);
            }
            return list;
        }
    }
}

/// <summary>
/// A vector feature: an id, attribute values and a geometry. Features returned by a layer are owned by you —
/// dispose them when done (or use <see cref="Layer.ForEach(Action{Feature})"/>, which disposes each feature for you).
/// </summary>
public sealed unsafe class Feature : GdalObject
{
    internal Feature(nint handle) : base(new GdalSafeHandle(handle, &Release)) { }

    private static void Release(nint h) => GdalNative.OGR_F_Destroy(h);

    /// <summary>Creates an empty feature following a layer schema.</summary>
    public Feature(FeatureDefinition definition) : this(GdalNative.OGR_F_Create(definition.Handle)) { }

    public long Id
    {
        get => GdalNative.OGR_F_GetFID(Handle);
        set => GdalNative.CheckOgr(GdalNative.OGR_F_SetFID(Handle, value), "SetFID");
    }

    public FeatureDefinition Definition => new(GdalSafeHandle.Borrowed(GdalNative.OGR_F_GetDefnRef(Handle), Handle));

    public int FieldCount => GdalNative.OGR_F_GetFieldCount(Handle);

    public int GetFieldIndex(string name)
    {
        var i = GdalNative.OGR_F_GetFieldIndex(Handle, name);
        return i < 0 ? throw new KeyNotFoundException($"Field '{name}' does not exist.") : i;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The feature geometry (borrowed: valid while the feature lives; clone to keep it). Setting copies the geometry.</summary>
    public Geometry? Geometry
    {
        get
        {
            var h = GdalNative.OGR_F_GetGeometryRef(Handle);
            return h == 0 ? null : new Geometry(GdalSafeHandle.Borrowed(h, Handle));
        }
        set
        {
            if (value is null)
            {
                GdalNative.CheckOgr(GdalNative.OGR_F_SetGeometryDirectly(Handle, 0), "SetGeometry");
            }
            else
            {
                GdalNative.CheckOgr(GdalNative.OGR_F_SetGeometry(Handle, value.Handle), "SetGeometry");
            }
        }
    }

    /// <summary>Transfers ownership of <paramref name="geometry"/> to the feature (no copy; the geometry object becomes unusable).</summary>
    public void SetGeometryDirectly(Geometry geometry)
    {
        if (geometry.IsBorrowed) throw new InvalidOperationException("Only owned geometries can be transferred.");
        var h = geometry.RawHandle.Detach();
        GdalNative.CheckOgr(GdalNative.OGR_F_SetGeometryDirectly(Handle, h), "SetGeometryDirectly");
    }

    // ------------------------------------------------------------------ attributes

    public bool IsNull(int index) => GdalNative.OGR_F_IsFieldSetAndNotNull(Handle, index) == 0;
    public bool IsNull(string name) => IsNull(GetFieldIndex(name));

    public string GetString(int index) => Interop.PtrToString(GdalNative.OGR_F_GetFieldAsString(Handle, index)) ?? string.Empty;
    public string GetString(string name) => GetString(GetFieldIndex(name));

    public int GetInt(int index) => GdalNative.OGR_F_GetFieldAsInteger(Handle, index);
    public int GetInt(string name) => GetInt(GetFieldIndex(name));

    public long GetInt64(int index) => GdalNative.OGR_F_GetFieldAsInteger64(Handle, index);
    public long GetInt64(string name) => GetInt64(GetFieldIndex(name));

    public double GetDouble(int index) => GdalNative.OGR_F_GetFieldAsDouble(Handle, index);
    public double GetDouble(string name) => GetDouble(GetFieldIndex(name));

    public DateTimeOffset? GetDateTime(int index)
    {
        int y, m, d, h, mi, tz;
        float s;
        if (GdalNative.OGR_F_GetFieldAsDateTimeEx(Handle, index, &y, &m, &d, &h, &mi, &s, &tz) == 0 || y == 0) return null;
        var offset = tz > 1 ? TimeSpan.FromMinutes((tz - 100) * 15) : TimeSpan.Zero;
        var whole = (int)s;
        return new DateTimeOffset(y, Math.Max(1, m), Math.Max(1, d), h, mi, Math.Clamp(whole, 0, 59), offset).AddMilliseconds((s - whole) * 1000);
    }

    public DateTimeOffset? GetDateTime(string name) => GetDateTime(GetFieldIndex(name));

    public byte[] GetBinary(int index)
    {
        int count;
        var p = GdalNative.OGR_F_GetFieldAsBinary(Handle, index, &count);
        return p == 0 || count <= 0 ? [] : new ReadOnlySpan<byte>((void*)p, count).ToArray();
    }

    /// <summary>Gets the value as the natural CLR type of the field (null for null/unset fields).</summary>
    public object? GetValue(int index)
    {
        if (IsNull(index)) return null;
        using var defn = Definition;
        using var field = defn.GetField(index);
        return field.Type switch
        {
            FieldType.Integer => field.SubType == FieldSubType.Boolean ? GetInt(index) != 0 : GetInt(index),
            FieldType.Integer64 => GetInt64(index),
            FieldType.Real => GetDouble(index),
            FieldType.Date or FieldType.DateTime => GetDateTime(index),
            FieldType.Binary => GetBinary(index),
            _ => GetString(index),
        };
    }

    public object? GetValue(string name) => GetValue(GetFieldIndex(name));

    /// <summary>Indexer by field name (natural CLR type). Setting accepts string, numbers, bool, DateTime(Offset), byte[] or null.</summary>
    public object? this[string name]
    {
        get => GetValue(name);
        set => SetValue(GetFieldIndex(name), value);
    }

    public object? this[int index]
    {
        get => GetValue(index);
        set => SetValue(index, value);
    }

    public void SetValue(int index, object? value)
    {
        switch (value)
        {
            case null: GdalNative.OGR_F_SetFieldNull(Handle, index); break;
            case string s: GdalNative.OGR_F_SetFieldString(Handle, index, s); break;
            case int i: GdalNative.OGR_F_SetFieldInteger(Handle, index, i); break;
            case short i: GdalNative.OGR_F_SetFieldInteger(Handle, index, i); break;
            case byte i: GdalNative.OGR_F_SetFieldInteger(Handle, index, i); break;
            case bool b: GdalNative.OGR_F_SetFieldInteger(Handle, index, b ? 1 : 0); break;
            case long l: GdalNative.OGR_F_SetFieldInteger64(Handle, index, l); break;
            case double d: GdalNative.OGR_F_SetFieldDouble(Handle, index, d); break;
            case float f: GdalNative.OGR_F_SetFieldDouble(Handle, index, f); break;
            case decimal m: GdalNative.OGR_F_SetFieldDouble(Handle, index, (double)m); break;
            case DateTime dt: SetDateTime(index, new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)); break;
            case DateTimeOffset dto: SetDateTime(index, dto); break;
            case DateOnly date: GdalNative.OGR_F_SetFieldDateTimeEx(Handle, index, date.Year, date.Month, date.Day, 0, 0, 0, 0); break;
            case byte[] bytes:
                fixed (byte* p = bytes) GdalNative.OGR_F_SetFieldBinary(Handle, index, bytes.Length, p);
                break;
            default: GdalNative.OGR_F_SetFieldString(Handle, index, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty); break;
        }
    }

    private void SetDateTime(int index, DateTimeOffset v)
    {
        var tz = 100 + (int)(v.Offset.TotalMinutes / 15);
        GdalNative.OGR_F_SetFieldDateTimeEx(Handle, index, v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second + v.Millisecond / 1000f, tz);
    }

    public void Unset(int index) => GdalNative.OGR_F_UnsetField(Handle, index);

    /// <summary>Snapshot of all attributes as a dictionary (field name → natural CLR value).</summary>
    public Dictionary<string, object?> GetAttributes()
    {
        using var defn = Definition;
        var n = defn.FieldCount;
        var dict = new Dictionary<string, object?>(n, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            using var f = defn.GetField(i);
            dict[f.Name] = GetValue(i);
        }
        return dict;
    }

    public Feature Clone() => new(GdalNative.OGR_F_Clone(Handle));

    public override string ToString() => $"Feature {Id}";
}
