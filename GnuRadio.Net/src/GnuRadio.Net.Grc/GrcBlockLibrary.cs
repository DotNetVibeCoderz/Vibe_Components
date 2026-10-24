using System.Globalization;
using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Sinks;
using GnuRadio.Synchronization;

namespace GnuRadio.Grc;

/// <summary>Parameter access for one .grc block during import or code generation.</summary>
public sealed class GrcBlockContext(GrcBlock block, IReadOnlyDictionary<string, object?> scope)
{
    public GrcBlock Block { get; } = block;
    public IReadOnlyDictionary<string, object?> Scope { get; } = scope;

    public string Raw(string key, string fallback = "") => Block.Parameter(key, fallback);

    public object? Value(string key, string fallback = "0")
    {
        var raw = Raw(key, fallback);
        return GrcExpression.Evaluate(string.IsNullOrWhiteSpace(raw) ? fallback : raw, Scope);
    }

    public double Double(string key, string fallback = "0") => GrcExpression.ToDouble(Value(key, fallback));
    public float Float(string key, string fallback = "0") => (float)Double(key, fallback);
    public int Int(string key, string fallback = "0") => (int)Math.Round(Double(key, fallback));

    public bool Bool(string key, string fallback = "False") => Value(key, fallback) switch
    {
        bool b => b,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        var v => GrcExpression.ToDouble(v) != 0,
    };

    /// <summary>GRC item type: complex, float, int, short, byte.</summary>
    public string Type => Raw("type", "complex");

    public string TypeName => TypeNameOf(Type);

    public static string TypeNameOf(string type) => type switch
    {
        "complex" or "fc32" or "c" => "Complex32",
        "float" or "f32" or "f" => "float",
        "int" or "s32" or "i" => "int",
        "short" or "s16" or "s" => "short",
        "byte" or "u8" or "b" => "byte",
        _ => "Complex32",
    };

    /// <summary>C# expression for a parameter, optionally cast.</summary>
    public string Cs(string key, string fallback = "0", string? cast = null)
    {
        var raw = Raw(key, fallback);
        if (string.IsNullOrWhiteSpace(raw)) raw = fallback;
        string expr;
        try
        {
            expr = GrcExpression.Parse(raw).ToCSharp();
        }
        catch (FormatException)
        {
            expr = "\"" + raw.Replace("\"", "\\\"") + "\"";
        }
        return cast is null ? expr : $"({cast})({expr})";
    }

    /// <summary>Creates a generic block for the block's item type.</summary>
    public Block ByType(Func<Block> complex, Func<Block> real, Func<Block>? integer = null, Func<Block>? shortType = null, Func<Block>? byteType = null) =>
        TypeName switch
        {
            "Complex32" => complex(),
            "float" => real(),
            "int" when integer is not null => integer(),
            "short" when shortType is not null => shortType(),
            "byte" when byteType is not null => byteType(),
            _ => throw new NotSupportedException($"{Block.Id} with type '{Type}' is not supported."),
        };
}

/// <summary>How one GRC block id maps to GnuRadio.Net: a factory and a C# code template.</summary>
public sealed record GrcMapping(Func<GrcBlockContext, Block> Create, Func<GrcBlockContext, string> Code);

/// <summary>
/// The table of GRC block ids GnuRadio.Net understands. Add your own with <see cref="Register"/>
/// (for example ZeroMQ or hardware blocks from the other packages).
/// </summary>
public static class GrcBlockLibrary
{
    private static readonly Dictionary<string, GrcMapping> s_mappings = new(StringComparer.Ordinal);

    /// <summary>Variable-like block ids and the parameter that holds their value.</summary>
    public static readonly IReadOnlyDictionary<string, string> VariableIds = new Dictionary<string, string>
    {
        ["variable"] = "value",
        ["parameter"] = "value",
        ["variable_qtgui_range"] = "value",
        ["variable_qtgui_slider"] = "value",
        ["variable_qtgui_chooser"] = "value",
        ["variable_qtgui_check_box"] = "value",
        ["variable_qtgui_entry"] = "value",
        ["variable_qtgui_label"] = "value",
        ["variable_qtgui_push_button"] = "value",
        ["variable_constellation"] = "const_points",
    };

    /// <summary>Block ids that carry no signal processing.</summary>
    public static readonly IReadOnlySet<string> IgnoredIds = new HashSet<string> { "options", "import", "note", "virtual_sink", "virtual_source", "snippet" };

    static GrcBlockLibrary()
    {
        RegisterDefaults();
    }

    public static IReadOnlyCollection<string> SupportedIds => s_mappings.Keys;

    public static void Register(string id, GrcMapping mapping) => s_mappings[id] = mapping;

    public static bool TryGet(string id, out GrcMapping mapping) => s_mappings.TryGetValue(id, out mapping!);

    private static string Cplx(string t) => GrcBlockContext.TypeNameOf(t);

    internal static Waveform ParseWaveform(string raw) => raw switch
    {
        _ when raw.Contains("COS") => Waveform.Cosine,
        _ when raw.Contains("SIN") => Waveform.Sine,
        _ when raw.Contains("SQR") => Waveform.Square,
        _ when raw.Contains("TRI") => Waveform.Triangle,
        _ when raw.Contains("SAW") => Waveform.Sawtooth,
        _ when raw.Contains("CONST") => Waveform.Constant,
        _ => Waveform.Cosine,
    };

    internal static NoiseType ParseNoise(string raw) => raw switch
    {
        _ when raw.Contains("UNIFORM") => NoiseType.Uniform,
        _ when raw.Contains("LAPLACIAN") => NoiseType.Laplacian,
        _ when raw.Contains("IMPULSE") => NoiseType.Impulse,
        _ => NoiseType.Gaussian,
    };

    internal static WindowType ParseWindow(string raw)
    {
        var u = raw.ToUpperInvariant();
        return u switch
        {
            _ when u.Contains("BLACKMAN_H") || u.Contains("BLACKMANHARRIS") => WindowType.BlackmanHarris,
            _ when u.Contains("BLACKMAN") => WindowType.Blackman,
            _ when u.Contains("HANN") => WindowType.Hann,
            _ when u.Contains("KAISER") => WindowType.Kaiser,
            _ when u.Contains("RECT") => WindowType.Rectangular,
            _ when u.Contains("FLATTOP") => WindowType.FlatTop,
            _ when u.Contains("BARTLETT") => WindowType.Bartlett,
            _ => WindowType.Hamming,
        };
    }

    private static string Enum<T>(T value) where T : struct, System.Enum => $"{typeof(T).Name}.{value}";

    private static void RegisterDefaults()
    {
        // ---- sources ----
        Register("analog_sig_source_x", new(
            c => c.ByType(
                () => new SignalSource<Complex32>(c.Double("samp_rate", "32000"), ParseWaveform(c.Raw("waveform")), c.Double("freq", "1000"), c.Float("amp", "1"), c.Float("offset", "0"), c.Double("phase", "0")),
                () => new SignalSource<float>(c.Double("samp_rate", "32000"), ParseWaveform(c.Raw("waveform")), c.Double("freq", "1000"), c.Float("amp", "1"), c.Float("offset", "0"), c.Double("phase", "0"))),
            c => $"new SignalSource<{c.TypeName}>({c.Cs("samp_rate", "32000", "double")}, {Enum(ParseWaveform(c.Raw("waveform")))}, {c.Cs("freq", "1000", "double")}, {c.Cs("amp", "1", "float")}, {c.Cs("offset", "0", "float")})"));

        Register("analog_noise_source_x", new(
            c => c.ByType(
                () => new NoiseSource<Complex32>(ParseNoise(c.Raw("noise_type")), c.Float("amp", "1"), c.Int("seed", "0")),
                () => new NoiseSource<float>(ParseNoise(c.Raw("noise_type")), c.Float("amp", "1"), c.Int("seed", "0"))),
            c => $"new NoiseSource<{c.TypeName}>({Enum(ParseNoise(c.Raw("noise_type")))}, {c.Cs("amp", "1", "float")}, {c.Cs("seed", "0", "long")})"));

        Register("analog_const_source_x", new(
            c => c.ByType(() => new ConstantSource<Complex32>(new Complex32(c.Float("const"), 0)), () => new ConstantSource<float>(c.Float("const"))),
            c => c.TypeName == "Complex32" ? $"new ConstantSource<Complex32>(new Complex32({c.Cs("const", "0", "float")}, 0))" : $"new ConstantSource<float>({c.Cs("const", "0", "float")})"));

        Register("blocks_null_source", new(
            c => c.ByType(() => new NullSource<Complex32>(c.Int("vlen", "1")), () => new NullSource<float>(c.Int("vlen", "1")), () => new NullSource<int>(), () => new NullSource<short>(), () => new NullSource<byte>()),
            c => $"new NullSource<{c.TypeName}>({c.Cs("vlen", "1", "int")})"));

        Register("blocks_vector_source_x", new(
            c => c.ByType(
                () => new VectorSource<Complex32>(ToArray<Complex32>(c.Value("vector", "[0]"), v => v is Complex32 z ? z : new Complex32((float)GrcExpression.ToDouble(v), 0)), c.Bool("repeat", "True"), c.Int("vlen", "1")),
                () => new VectorSource<float>(ToArray(c.Value("vector", "[0]"), v => (float)GrcExpression.ToDouble(v)), c.Bool("repeat", "True"), c.Int("vlen", "1")),
                () => new VectorSource<int>(ToArray(c.Value("vector", "[0]"), v => (int)GrcExpression.ToDouble(v)), c.Bool("repeat", "True"), c.Int("vlen", "1")),
                () => new VectorSource<short>(ToArray(c.Value("vector", "[0]"), v => (short)GrcExpression.ToDouble(v)), c.Bool("repeat", "True"), c.Int("vlen", "1")),
                () => new VectorSource<byte>(ToArray(c.Value("vector", "[0]"), v => (byte)GrcExpression.ToDouble(v)), c.Bool("repeat", "True"), c.Int("vlen", "1"))),
            c => $"new VectorSource<{c.TypeName}>({c.Cs("vector", "[0]")}.Select(v => ({c.TypeName})v).ToArray(), {c.Cs("repeat", "True")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_file_source", new(
            c => c.ByType(() => new FileSource<Complex32>(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True"), c.Int("vlen", "1")),
                          () => new FileSource<float>(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True"), c.Int("vlen", "1")),
                          () => new FileSource<int>(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True")),
                          () => new FileSource<short>(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True")),
                          () => new FileSource<byte>(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True"))),
            c => $"new FileSource<{c.TypeName}>({c.Cs("file", "''")}, {c.Cs("repeat", "True")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_wavfile_source", new(
            c => new WavFileSource(c.Value("file", "''")?.ToString() ?? "", c.Bool("repeat", "True")),
            c => $"new WavFileSource({c.Cs("file", "''")}, {c.Cs("repeat", "True")})"));

        // ---- stream plumbing ----
        foreach (var id in new[] { "blocks_throttle", "blocks_throttle2" })
            Register(id, new(
                c => c.ByType(() => new Throttle<Complex32>(c.Double("samples_per_second", "32000"), c.Int("vlen", "1")),
                              () => new Throttle<float>(c.Double("samples_per_second", "32000"), c.Int("vlen", "1")),
                              () => new Throttle<int>(c.Double("samples_per_second", "32000"), c.Int("vlen", "1")),
                              () => new Throttle<short>(c.Double("samples_per_second", "32000"), c.Int("vlen", "1")),
                              () => new Throttle<byte>(c.Double("samples_per_second", "32000"), c.Int("vlen", "1"))),
                c => $"new Throttle<{c.TypeName}>({c.Cs("samples_per_second", "32000", "double")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_head", new(
            c => c.ByType(() => new Head<Complex32>(c.Int("num_items", "1024"), c.Int("vlen", "1")), () => new Head<float>(c.Int("num_items", "1024"), c.Int("vlen", "1")),
                          () => new Head<int>(c.Int("num_items", "1024")), () => new Head<short>(c.Int("num_items", "1024")), () => new Head<byte>(c.Int("num_items", "1024"))),
            c => $"new Head<{c.TypeName}>({c.Cs("num_items", "1024", "long")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_skiphead", new(
            c => c.ByType(() => new SkipHead<Complex32>(c.Int("num_items")), () => new SkipHead<float>(c.Int("num_items")), byteType: () => new SkipHead<byte>(c.Int("num_items"))),
            c => $"new SkipHead<{c.TypeName}>({c.Cs("num_items", "0", "long")})"));

        Register("blocks_delay", new(
            c => c.ByType(() => new Delay<Complex32>(c.Int("delay")), () => new Delay<float>(c.Int("delay")), byteType: () => new Delay<byte>(c.Int("delay"))),
            c => $"new Delay<{c.TypeName}>({c.Cs("delay", "0", "int")})"));

        Register("blocks_keep_one_in_n", new(
            c => c.ByType(() => new KeepOneInN<Complex32>(c.Int("n", "1")), () => new KeepOneInN<float>(c.Int("n", "1")), byteType: () => new KeepOneInN<byte>(c.Int("n", "1"))),
            c => $"new KeepOneInN<{c.TypeName}>({c.Cs("n", "1", "int")})"));

        Register("blocks_stream_to_vector", new(
            c => c.ByType(() => new StreamToVector<Complex32>(c.Int("num_items", "1")), () => new StreamToVector<float>(c.Int("num_items", "1")), byteType: () => new StreamToVector<byte>(c.Int("num_items", "1"))),
            c => $"new StreamToVector<{c.TypeName}>({c.Cs("num_items", "1", "int")})"));

        Register("blocks_vector_to_stream", new(
            c => c.ByType(() => new VectorToStream<Complex32>(c.Int("num_items", "1")), () => new VectorToStream<float>(c.Int("num_items", "1")), byteType: () => new VectorToStream<byte>(c.Int("num_items", "1"))),
            c => $"new VectorToStream<{c.TypeName}>({c.Cs("num_items", "1", "int")})"));

        Register("blocks_copy", new(
            c => c.ByType(() => new Copy<Complex32>(), () => new Copy<float>(), byteType: () => new Copy<byte>()),
            c => $"new Copy<{c.TypeName}>()"));

        // ---- math ----
        Register("blocks_add_xx", new(
            c => c.ByType(() => new Add<Complex32>(c.Int("num_inputs", "2"), c.Int("vlen", "1")), () => new Add<float>(c.Int("num_inputs", "2"), c.Int("vlen", "1")),
                          () => new Add<int>(c.Int("num_inputs", "2")), () => new Add<short>(c.Int("num_inputs", "2"))),
            c => $"new Add<{c.TypeName}>({c.Cs("num_inputs", "2", "int")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_sub_xx", new(
            c => c.ByType(() => new Subtract<Complex32>(c.Int("num_inputs", "2")), () => new Subtract<float>(c.Int("num_inputs", "2"))),
            c => $"new Subtract<{c.TypeName}>({c.Cs("num_inputs", "2", "int")})"));

        Register("blocks_multiply_xx", new(
            c => c.ByType(() => new Multiply<Complex32>(c.Int("num_inputs", "2"), c.Int("vlen", "1")), () => new Multiply<float>(c.Int("num_inputs", "2"), c.Int("vlen", "1"))),
            c => $"new Multiply<{c.TypeName}>({c.Cs("num_inputs", "2", "int")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_multiply_const_vxx", new(
            c => c.ByType(() => new MultiplyConst<Complex32>(ToComplex(c.Value("const", "1")), c.Int("vlen", "1")), () => new MultiplyConst<float>(c.Float("const", "1"), c.Int("vlen", "1"))),
            c => c.TypeName == "Complex32"
                ? $"new MultiplyConst<Complex32>(new Complex32({c.Cs("const", "1", "float")}, 0), {c.Cs("vlen", "1", "int")})"
                : $"new MultiplyConst<float>({c.Cs("const", "1", "float")}, {c.Cs("vlen", "1", "int")})"));
        s_mappings["blocks_multiply_const_xx"] = s_mappings["blocks_multiply_const_vxx"];

        Register("blocks_add_const_vxx", new(
            c => c.ByType(() => new AddConst<Complex32>(ToComplex(c.Value("const", "0"))), () => new AddConst<float>(c.Float("const", "0"))),
            c => c.TypeName == "Complex32" ? $"new AddConst<Complex32>(new Complex32({c.Cs("const", "0", "float")}, 0))" : $"new AddConst<float>({c.Cs("const", "0", "float")})"));

        Register("blocks_multiply_conjugate_cc", new(_ => new MultiplyConjugate(), _ => "new MultiplyConjugate()"));
        Register("blocks_conjugate_cc", new(_ => new Conjugate(), _ => "new Conjugate()"));
        Register("blocks_complex_to_mag", new(c => new ComplexToMag(c.Int("vlen", "1")), c => $"new ComplexToMag({c.Cs("vlen", "1", "int")})"));
        Register("blocks_complex_to_mag_squared", new(c => new ComplexToMagSquared(c.Int("vlen", "1")), c => $"new ComplexToMagSquared({c.Cs("vlen", "1", "int")})"));
        Register("blocks_complex_to_arg", new(c => new ComplexToArg(c.Int("vlen", "1")), c => $"new ComplexToArg({c.Cs("vlen", "1", "int")})"));
        Register("blocks_complex_to_real", new(c => new ComplexToReal(c.Int("vlen", "1")), c => $"new ComplexToReal({c.Cs("vlen", "1", "int")})"));
        Register("blocks_complex_to_imag", new(c => new ComplexToImag(c.Int("vlen", "1")), c => $"new ComplexToImag({c.Cs("vlen", "1", "int")})"));
        Register("blocks_complex_to_float", new(_ => new ComplexToFloat(), _ => "new ComplexToFloat()"));
        Register("blocks_float_to_complex", new(_ => new FloatToComplex(), _ => "new FloatToComplex()"));
        Register("blocks_nlog10_ff", new(c => new NLog10(c.Float("n", "1"), c.Float("k", "0"), c.Int("vlen", "1")),
            c => $"new NLog10({c.Cs("n", "1", "float")}, {c.Cs("k", "0", "float")}, {c.Cs("vlen", "1", "int")})"));
        Register("blocks_rotator_cc", new(c => new Rotator(c.Double("phase_inc", "0")), c => $"new Rotator({c.Cs("phase_inc", "0", "double")})"));
        Register("blocks_float_to_short", new(c => new FloatToShort(c.Float("scale", "1")), c => $"new FloatToShort({c.Cs("scale", "1", "float")})"));
        Register("blocks_short_to_float", new(c => new ShortToFloat(c.Float("scale", "1")), c => $"new ShortToFloat({c.Cs("scale", "1", "float")})"));
        Register("blocks_char_to_float", new(c => new CharToFloat(c.Float("scale", "1")), c => $"new CharToFloat({c.Cs("scale", "1", "float")})"));
        Register("blocks_uchar_to_float", new(_ => new UCharToFloat(), _ => "new UCharToFloat()"));
        Register("blocks_float_to_uchar", new(_ => new FloatToUChar(), _ => "new FloatToUChar()"));
        Register("blocks_unpack_k_bits_bb", new(c => new UnpackKBits(c.Int("k", "8")), c => $"new UnpackKBits({c.Cs("k", "8", "int")})"));
        Register("blocks_pack_k_bits_bb", new(c => new PackKBits(c.Int("k", "8")), c => $"new PackKBits({c.Cs("k", "8", "int")})"));
        Register("blocks_repack_bits_bb", new(c => new RepackBits(c.Int("k", "8"), c.Int("l", "1")), c => $"new RepackBits({c.Cs("k", "8", "int")}, {c.Cs("l", "1", "int")})"));
        Register("blocks_moving_average_xx", new(
            c => c.ByType(() => new MovingAverage<Complex32>(c.Int("length", "1000"), c.Float("scale", "1")), () => new MovingAverage<float>(c.Int("length", "1000"), c.Float("scale", "1"))),
            c => $"new MovingAverage<{c.TypeName}>({c.Cs("length", "1000", "int")}, {c.Cs("scale", "1", "float")})"));

        // ---- sinks ----
        Register("blocks_null_sink", new(
            c => c.ByType(() => new NullSink<Complex32>(c.Int("vlen", "1")), () => new NullSink<float>(c.Int("vlen", "1")), () => new NullSink<int>(), () => new NullSink<short>(), () => new NullSink<byte>()),
            c => $"new NullSink<{c.TypeName}>({c.Cs("vlen", "1", "int")})"));

        Register("blocks_vector_sink_x", new(
            c => c.ByType(() => new VectorSink<Complex32>(c.Int("vlen", "1")), () => new VectorSink<float>(c.Int("vlen", "1")), () => new VectorSink<int>(), () => new VectorSink<short>(), () => new VectorSink<byte>()),
            c => $"new VectorSink<{c.TypeName}>({c.Cs("vlen", "1", "int")})"));

        Register("blocks_file_sink", new(
            c => c.ByType(() => new FileSink<Complex32>(c.Value("file", "''")?.ToString() ?? "", c.Bool("append", "False"), c.Int("vlen", "1")),
                          () => new FileSink<float>(c.Value("file", "''")?.ToString() ?? "", c.Bool("append", "False"), c.Int("vlen", "1")),
                          () => new FileSink<int>(c.Value("file", "''")?.ToString() ?? ""),
                          () => new FileSink<short>(c.Value("file", "''")?.ToString() ?? ""),
                          () => new FileSink<byte>(c.Value("file", "''")?.ToString() ?? "")),
            c => $"new FileSink<{c.TypeName}>({c.Cs("file", "''")}, {c.Cs("append", "False")}, {c.Cs("vlen", "1", "int")})"));

        Register("blocks_wavfile_sink", new(
            c => new WavFileSink(c.Value("file", "''")?.ToString() ?? "", c.Int("nchan", "1"), c.Int("samp_rate", "48000")),
            c => $"new WavFileSink({c.Cs("file", "''")}, {c.Cs("nchan", "1", "int")}, {c.Cs("samp_rate", "48000", "int")})"));

        Register("audio_sink", new(
            c => new AudioSink(c.Int("samp_rate", "48000"), c.Int("num_inputs", "1")),
            c => $"new AudioSink({c.Cs("samp_rate", "48000", "int")}, {c.Cs("num_inputs", "1", "int")})"));

        Register("blocks_probe_signal_x", new(
            c => c.ByType(() => new ProbeSignal<Complex32>(), () => new ProbeSignal<float>()),
            c => $"new ProbeSignal<{c.TypeName}>()"));

        Register("blocks_tag_debug", new(
            c => c.ByType(() => new TagDebug<Complex32>(c.Value("name", "''")?.ToString() ?? "", c.Bool("display", "True")), () => new TagDebug<float>(c.Value("name", "''")?.ToString() ?? "", c.Bool("display", "True")),
                          byteType: () => new TagDebug<byte>(c.Value("name", "''")?.ToString() ?? "", c.Bool("display", "True"))),
            c => $"new TagDebug<{c.TypeName}>({c.Cs("name", "''")}, {c.Cs("display", "True")})"));

        // ---- messages ----
        Register("blocks_message_debug", new(_ => new MessageDebug(), _ => "new MessageDebug()"));
        Register("blocks_message_strobe", new(
            c => new MessageStrobe(c.Value("msg", "pmt.intern('TEST')") as Pmt ?? Pmt.Symbol("TEST"), TimeSpan.FromMilliseconds(c.Double("period", "1000"))),
            c => $"new MessageStrobe({c.Cs("msg", "pmt.intern('TEST')")}, TimeSpan.FromMilliseconds({c.Cs("period", "1000", "double")}))"));

        // ---- qtgui → GnuRadio.Net visual sinks ----
        Register("qtgui_time_sink_x", new(
            c => c.ByType(() => new TimeSink<Complex32>(c.Int("size", "1024"), c.Double("srate", "32000"), StripQuotes(c.Raw("name", "Time")), c.Int("nconnections", "1")),
                          () => new TimeSink<float>(c.Int("size", "1024"), c.Double("srate", "32000"), StripQuotes(c.Raw("name", "Time")), c.Int("nconnections", "1"))),
            c => $"new TimeSink<{c.TypeName}>({c.Cs("size", "1024", "int")}, {c.Cs("srate", "32000", "double")}, {c.Cs("name", "'Time'")}, {c.Cs("nconnections", "1", "int")})"));

        Register("qtgui_freq_sink_x", new(
            c => new FrequencySink(c.Int("fftsize", "1024"), c.Double("bw", "32000"), c.Double("fc", "0"), StripQuotes(c.Raw("name", "Spectrum")), ParseWindow(c.Raw("wintype"))),
            c => $"new FrequencySink({c.Cs("fftsize", "1024", "int")}, {c.Cs("bw", "32000", "double")}, {c.Cs("fc", "0", "double")}, {c.Cs("name", "'Spectrum'")})"));
        s_mappings["qtgui_sink_x"] = s_mappings["qtgui_freq_sink_x"];

        Register("qtgui_waterfall_sink_x", new(
            c => new WaterfallSink(c.Int("fftsize", "1024"), c.Double("bw", "32000"), c.Double("fc", "0"), 256, StripQuotes(c.Raw("name", "Waterfall"))),
            c => $"new WaterfallSink({c.Cs("fftsize", "1024", "int")}, {c.Cs("bw", "32000", "double")}, {c.Cs("fc", "0", "double")}, 256, {c.Cs("name", "'Waterfall'")})"));

        Register("qtgui_const_sink_x", new(
            c => new ConstellationSink(c.Int("size", "1024"), StripQuotes(c.Raw("name", "Constellation"))),
            c => $"new ConstellationSink({c.Cs("size", "1024", "int")}, {c.Cs("name", "'Constellation'")})"));

        Register("qtgui_number_sink", new(
            c => new NumberSink(StripQuotes(c.Raw("name", "Level"))),
            c => $"new NumberSink({c.Cs("name", "'Level'")})"));

        // ---- filters ----
        Register("low_pass_filter", FilterMapping("low_pass"));
        Register("high_pass_filter", FilterMapping("high_pass"));
        Register("band_pass_filter", FilterMapping("band_pass"));
        Register("band_reject_filter", FilterMapping("band_reject"));

        Register("rational_resampler_xxx", new(
            c => c.Raw("type", "ccc").StartsWith('f')
                ? new RationalResampler<float>(c.Int("interp", "1"), c.Int("decim", "1"), null, c.Double("fbw", "0.4") is var f && f > 0 ? f : 0.4)
                : new RationalResampler<Complex32>(c.Int("interp", "1"), c.Int("decim", "1"), null, c.Double("fbw", "0.4") is var g && g > 0 ? g : 0.4),
            c => $"new RationalResampler<{(c.Raw("type", "ccc").StartsWith('f') ? "float" : "Complex32")}>({c.Cs("interp", "1", "int")}, {c.Cs("decim", "1", "int")})"));

        Register("pfb_arb_resampler_xxx", new(
            c => c.Raw("type", "ccf").StartsWith('f') ? new ArbitraryResampler<float>(c.Double("rrate", "1")) : new ArbitraryResampler<Complex32>(c.Double("rrate", "1")),
            c => $"new ArbitraryResampler<{(c.Raw("type", "ccf").StartsWith('f') ? "float" : "Complex32")}>({c.Cs("rrate", "1", "double")})"));

        Register("freq_xlating_fir_filter_xxx", new(
            c => new FreqXlatingFirFilter(c.Int("decim", "1"), ToArray(c.Value("taps", "[1]"), v => (float)GrcExpression.ToDouble(v)), c.Double("center_freq", "0"), c.Double("samp_rate", "32000")),
            c => $"new FreqXlatingFirFilter({c.Cs("decim", "1", "int")}, {c.Cs("taps", "[1]")}.Select(t => (float)t).ToArray(), {c.Cs("center_freq", "0", "double")}, {c.Cs("samp_rate", "32000", "double")})"));

        Register("fft_vxx", new(
            c => new FftBlock(c.Int("fft_size", "1024"), c.Bool("forward", "True"), WindowType.BlackmanHarris, c.Bool("shift", "True")),
            c => $"new FftBlock({c.Cs("fft_size", "1024", "int")}, {c.Cs("forward", "True")}, WindowType.BlackmanHarris, {c.Cs("shift", "True")})"));

        Register("single_pole_iir_filter_xx", new(
            c => c.ByType(() => new SinglePoleIir<Complex32>(c.Float("alpha", "1"), c.Int("vlen", "1")), () => new SinglePoleIir<float>(c.Float("alpha", "1"), c.Int("vlen", "1"))),
            c => $"new SinglePoleIir<{c.TypeName}>({c.Cs("alpha", "1", "float")}, {c.Cs("vlen", "1", "int")})"));

        Register("dc_blocker_xx", new(
            c => c.Raw("type", "cc") == "ff" ? new DcBlocker<float>(c.Int("length", "32")) : new DcBlocker<Complex32>(c.Int("length", "32")),
            c => $"new DcBlocker<{(c.Raw("type", "cc") == "ff" ? "float" : "Complex32")}>({c.Cs("length", "32", "int")})"));

        // ---- analog ----
        Register("analog_quadrature_demod_cf", new(c => new QuadratureDemod(c.Float("gain", "1")), c => $"new QuadratureDemod({c.Cs("gain", "1", "float")})"));
        Register("analog_frequency_modulator_fc", new(c => new FrequencyModulator(c.Float("sensitivity", "1")), c => $"new FrequencyModulator({c.Cs("sensitivity", "1", "float")})"));
        Register("analog_phase_modulator_fc", new(c => new PhaseModulator(c.Float("sensitivity", "1")), c => $"new PhaseModulator({c.Cs("sensitivity", "1", "float")})"));
        Register("analog_wfm_rcv", new(
            c => new WbfmReceive(c.Double("quad_rate", "480000"), c.Int("audio_decimation", "10")),
            c => $"new WbfmReceive({c.Cs("quad_rate", "480000", "double")}, {c.Cs("audio_decimation", "10", "int")})"));
        Register("analog_nbfm_rx", new(
            c => new NbfmReceive(c.Double("quad_rate", "480000"), Math.Max(1, (int)Math.Round(c.Double("quad_rate", "480000") / c.Double("audio_rate", "48000"))), c.Double("max_dev", "5000"), c.Double("tau", "75e-6")),
            c => $"new NbfmReceive({c.Cs("quad_rate", "480000", "double")}, (int)Math.Round({c.Cs("quad_rate", "480000", "double")} / {c.Cs("audio_rate", "48000", "double")}), {c.Cs("max_dev", "5000", "double")})"));
        Register("analog_agc_xx", new(
            c => c.ByType(() => new Agc<Complex32>(c.Float("rate", "1e-4"), c.Float("reference", "1"), c.Float("gain", "1")), () => new Agc<float>(c.Float("rate", "1e-4"), c.Float("reference", "1"), c.Float("gain", "1"))),
            c => $"new Agc<{c.TypeName}>({c.Cs("rate", "1e-4", "float")}, {c.Cs("reference", "1", "float")}, {c.Cs("gain", "1", "float")})"));
        Register("analog_pll_carriertracking_cc", new(
            c => new Pll(c.Double("w", "0.0628"), c.Double("max_freq", "0.5"), c.Double("min_freq", "-0.5")),
            c => $"new Pll({c.Cs("w", "0.0628", "double")}, {c.Cs("max_freq", "0.5", "double")}, {c.Cs("min_freq", "-0.5", "double")})"));
        Register("analog_pll_refout_cc", new(
            c => new Pll(c.Double("w", "0.0628"), c.Double("max_freq", "0.5"), c.Double("min_freq", "-0.5"), PllOutput.Reference),
            c => $"new Pll({c.Cs("w", "0.0628", "double")}, {c.Cs("max_freq", "0.5", "double")}, {c.Cs("min_freq", "-0.5", "double")}, PllOutput.Reference)"));
        Register("analog_pll_freqdet_cf", new(
            c => new PllFreqDet(c.Double("w", "0.0628"), c.Double("max_freq", "0.5"), c.Double("min_freq", "-0.5")),
            c => $"new PllFreqDet({c.Cs("w", "0.0628", "double")}, {c.Cs("max_freq", "0.5", "double")}, {c.Cs("min_freq", "-0.5", "double")})"));

        // ---- digital ----
        Register("digital_costas_loop_cc", new(c => new CostasLoop(c.Double("w", "0.0628"), c.Int("order", "4")), c => $"new CostasLoop({c.Cs("w", "0.0628", "double")}, {c.Cs("order", "4", "int")})"));
        Register("digital_binary_slicer_fb", new(_ => new BinarySlicer(), _ => "new BinarySlicer()"));
        Register("digital_chunks_to_symbols_xx", new(
            c => new ChunksToSymbols(new Constellation("custom", ToArray(c.Value("symbol_table", "[-1, 1]"), ToComplex))),
            c => $"new ChunksToSymbols(new Constellation(\"custom\", {c.Cs("symbol_table", "[-1, 1]")}.Select(v => (Complex32)(float)v).ToArray()))"));
        Register("digital_diff_encoder_bb", new(c => new DiffEncoder(c.Int("modulus", "2")), c => $"new DiffEncoder({c.Cs("modulus", "2", "int")})"));
        Register("digital_diff_decoder_bb", new(c => new DiffDecoder(c.Int("modulus", "2")), c => $"new DiffDecoder({c.Cs("modulus", "2", "int")})"));
        Register("digital_constellation_modulator", new(
            c => new ConstellationModulator(c.Value("constellation", "digital.constellation_qpsk()") as Constellation ?? Constellation.Qpsk(), c.Int("samples_per_symbol", "4"), c.Double("excess_bw", "0.35"), c.Bool("differential", "False")),
            c => $"new ConstellationModulator({c.Cs("constellation", "digital.constellation_qpsk()")}, {c.Cs("samples_per_symbol", "4", "int")}, {c.Cs("excess_bw", "0.35", "double")}, {c.Cs("differential", "False")})"));
        Register("digital_constellation_decoder_cb", new(
            c => new ConstellationDecoder(c.Value("constellation", "digital.constellation_qpsk()") as Constellation ?? Constellation.Qpsk()),
            c => $"new ConstellationDecoder({c.Cs("constellation", "digital.constellation_qpsk()")})"));
        Register("digital_correlate_access_code_tag_xx", new(
            c => new CorrelateAccessCodeTag(c.Value("access_code", "''")?.ToString() ?? "", c.Int("threshold", "0"), c.Value("tagname", "'syncword'")?.ToString() ?? "syncword"),
            c => $"new CorrelateAccessCodeTag({c.Cs("access_code", "''")}, {c.Cs("threshold", "0", "int")}, {c.Cs("tagname", "'syncword'")})"));

        // ---- channels ----
        Register("channels_channel_model", new(
            c => new ChannelModel(c.Float("noise_voltage", "0"), c.Double("freq_offset", "0"), c.Double("epsilon", "1"),
                ToArray(c.Value("taps", "[1]"), ToComplex), c.Int("seed", "0")),
            c => $"new ChannelModel({c.Cs("noise_voltage", "0", "float")}, {c.Cs("freq_offset", "0", "double")}, {c.Cs("epsilon", "1", "double")}, seed: {c.Cs("seed", "0", "long")})"));
        Register("channels_fading_model", new(
            c => new FadingModel(c.Int("N", "8"), c.Double("fDTs", "0.0001"), c.Bool("LOS", "False"), c.Double("K", "4"), c.Int("seed", "0")),
            c => $"new FadingModel({c.Cs("N", "8", "int")}, {c.Cs("fDTs", "0.0001", "double")}, {c.Cs("LOS", "False")}, {c.Cs("K", "4", "double")}, {c.Cs("seed", "0", "long")})"));
    }

    private static GrcMapping FilterMapping(string kind) => new(
        c =>
        {
            var gain = c.Double("gain", "1");
            var rate = c.Double("samp_rate", "32000");
            var width = c.Double("width", "1000");
            var window = ParseWindow(c.Raw("win"));
            var beta = c.Double("beta", "6.76");
            var taps = kind switch
            {
                "low_pass" => Firdes.LowPass(gain, rate, c.Double("cutoff_freq", "1000"), width, window, beta),
                "high_pass" => Firdes.HighPass(gain, rate, c.Double("cutoff_freq", "1000"), width, window, beta),
                "band_pass" => Firdes.BandPass(gain, rate, c.Double("low_cutoff_freq", "1000"), c.Double("high_cutoff_freq", "2000"), width, window, beta),
                _ => Firdes.BandReject(gain, rate, c.Double("low_cutoff_freq", "1000"), c.Double("high_cutoff_freq", "2000"), width, window, beta),
            };
            var type = c.Raw("type", "fir_filter_ccf");
            var decim = c.Int("decim", "1");
            var interp = c.Int("interp", "1");
            if (type.StartsWith("interp", StringComparison.Ordinal))
                return type.EndsWith("fff", StringComparison.Ordinal) ? new InterpFirFilter<float>(interp, taps) : new InterpFirFilter<Complex32>(interp, taps);
            return type.EndsWith("fff", StringComparison.Ordinal) ? new FirFilterFff(decim, taps) : new FirFilterCcf(decim, taps);
        },
        c =>
        {
            var window = Enum(ParseWindow(c.Raw("win")));
            var design = kind switch
            {
                "low_pass" => $"Firdes.LowPass({c.Cs("gain", "1", "double")}, {c.Cs("samp_rate", "32000", "double")}, {c.Cs("cutoff_freq", "1000", "double")}, {c.Cs("width", "1000", "double")}, {window}, {c.Cs("beta", "6.76", "double")})",
                "high_pass" => $"Firdes.HighPass({c.Cs("gain", "1", "double")}, {c.Cs("samp_rate", "32000", "double")}, {c.Cs("cutoff_freq", "1000", "double")}, {c.Cs("width", "1000", "double")}, {window}, {c.Cs("beta", "6.76", "double")})",
                "band_pass" => $"Firdes.BandPass({c.Cs("gain", "1", "double")}, {c.Cs("samp_rate", "32000", "double")}, {c.Cs("low_cutoff_freq", "1000", "double")}, {c.Cs("high_cutoff_freq", "2000", "double")}, {c.Cs("width", "1000", "double")}, {window}, {c.Cs("beta", "6.76", "double")})",
                _ => $"Firdes.BandReject({c.Cs("gain", "1", "double")}, {c.Cs("samp_rate", "32000", "double")}, {c.Cs("low_cutoff_freq", "1000", "double")}, {c.Cs("high_cutoff_freq", "2000", "double")}, {c.Cs("width", "1000", "double")}, {window}, {c.Cs("beta", "6.76", "double")})",
            };
            var type = c.Raw("type", "fir_filter_ccf");
            if (type.StartsWith("interp", StringComparison.Ordinal))
                return $"new InterpFirFilter<{(type.EndsWith("fff", StringComparison.Ordinal) ? "float" : "Complex32")}>({c.Cs("interp", "1", "int")}, {design})";
            return type.EndsWith("fff", StringComparison.Ordinal)
                ? $"new FirFilterFff({c.Cs("decim", "1", "int")}, {design})"
                : $"new FirFilterCcf({c.Cs("decim", "1", "int")}, {design})";
        });

    private static string StripQuotes(string s) => s.Trim().Trim('\'', '"');

    private static Complex32 ToComplex(object? v) => v is Complex32 c ? c : new Complex32((float)GrcExpression.ToDouble(v), 0);

    private static T[] ToArray<T>(object? value, Func<object?, T> convert) => value switch
    {
        object?[] items => items.Select(convert).ToArray(),
        Array a => a.Cast<object?>().Select(convert).ToArray(),
        null => [],
        _ => [convert(value)],
    };

    internal static string FormatInvariant(double d) => d.ToString(CultureInfo.InvariantCulture);
}
