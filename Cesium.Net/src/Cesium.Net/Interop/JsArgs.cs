using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CesiumNet.Interop;

/// <summary>
/// Builds the JSON argument array for a bridge call without intermediate object graphs.
/// Instances are pooled per thread; always finish with <see cref="ToJson"/>.
/// </summary>
internal sealed class JsArgs
{
    [ThreadStatic] private static JsArgs? t_cached;

    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private readonly Utf8JsonWriter _writer;

    private JsArgs() => _writer = new Utf8JsonWriter(_buffer, new JsonWriterOptions { SkipValidation = true });

    public static JsArgs Rent()
    {
        var args = t_cached ?? new JsArgs();
        t_cached = null;
        args._buffer.ResetWrittenCount();
        args._writer.Reset(args._buffer);
        args._writer.WriteStartArray();
        return args;
    }

    public JsArgs Str(string? value)
    {
        if (value is null) _writer.WriteNullValue(); else _writer.WriteStringValue(value);
        return this;
    }

    public JsArgs Num(double value) { _writer.WriteNumberValue(value); return this; }

    public JsArgs Num(double? value)
    {
        if (value is { } v) _writer.WriteNumberValue(v); else _writer.WriteNullValue();
        return this;
    }

    public JsArgs Bool(bool value) { _writer.WriteBooleanValue(value); return this; }

    public JsArgs Obj<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        if (value is null) _writer.WriteNullValue();
        else JsonSerializer.Serialize(_writer, value, typeInfo);
        return this;
    }

    /// <summary>Completes the array, returns it as a string and returns this instance to the pool.</summary>
    public string ToJson()
    {
        _writer.WriteEndArray();
        _writer.Flush();
        var json = Encoding.UTF8.GetString(_buffer.WrittenSpan);
        if (_buffer.Capacity <= 64 * 1024) t_cached = this; // don't retain huge buffers
        return json;
    }

    /// <summary>Empty argument list.</summary>
    public const string Empty = "[]";
}
