using System.Text;

namespace GnuRadio.Analog;

/// <summary>International Morse code table and timing helpers.</summary>
public static class Morse
{
    public static readonly IReadOnlyDictionary<char, string> Codes = new Dictionary<char, string>
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".", ['F'] = "..-.",
        ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---", ['K'] = "-.-", ['L'] = ".-..",
        ['M'] = "--", ['N'] = "-.", ['O'] = "---", ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.",
        ['S'] = "...", ['T'] = "-", ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-",
        ['Y'] = "-.--", ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['.'] = ".-.-.-", [','] = "--..--", ['?'] = "..--..", ['/'] = "-..-.", ['='] = "-...-",
        ['+'] = ".-.-.", ['-'] = "-....-", ['@'] = ".--.-.",
    };

    private static readonly Dictionary<string, char> Reverse = Codes.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Duration of one dot in seconds for the PARIS standard (1.2 / WPM).</summary>
    public static double DotSeconds(double wordsPerMinute) => 1.2 / wordsPerMinute;

    /// <summary>
    /// Key-down pattern of <paramref name="text"/> in dot units: true = carrier on. Dot = 1, dash = 3,
    /// gaps 1 (element), 3 (letter), 7 (word).
    /// </summary>
    public static bool[] KeyPattern(string text)
    {
        var units = new List<bool>();
        var words = text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var letters = words[w].Where(Codes.ContainsKey).ToArray();
            for (var l = 0; l < letters.Length; l++)
            {
                var code = Codes[letters[l]];
                for (var e = 0; e < code.Length; e++)
                {
                    units.AddRange(Enumerable.Repeat(true, code[e] == '.' ? 1 : 3));
                    if (e < code.Length - 1) units.Add(false);
                }
                if (l < letters.Length - 1) units.AddRange(Enumerable.Repeat(false, 3));
            }
            units.AddRange(Enumerable.Repeat(false, 7));
        }
        return [.. units];
    }

    public static char? Decode(string symbol) => Reverse.TryGetValue(symbol, out var c) ? c : null;
}

/// <summary>
/// Produces the keyed CW envelope (0…1) of a text at a given speed, with raised-cosine edges to avoid key
/// clicks. Multiply with a tone (or use as AM envelope) to get an on-air CW signal.
/// </summary>
public sealed class MorseKeyer : SourceBlock<float>
{
    private readonly float[] _envelope;
    private int _position;

    public MorseKeyer(string text, double sampleRate, double wordsPerMinute = 20, bool repeat = true, double riseTime = 0.005)
        : base("morse_keyer")
    {
        Text = text;
        Repeat = repeat;
        var unit = (int)Math.Round(Morse.DotSeconds(wordsPerMinute) * sampleRate);
        var pattern = Morse.KeyPattern(text);
        var raw = new float[pattern.Length * unit];
        for (var u = 0; u < pattern.Length; u++)
            if (pattern[u]) raw.AsSpan(u * unit, unit).Fill(1f);
        // Shape the edges by convolving with a raised-cosine pulse of the rise time.
        var ramp = Math.Max(1, (int)(riseTime * sampleRate));
        var kernel = new float[ramp];
        var sum = 0f;
        for (var i = 0; i < ramp; i++) sum += kernel[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * (i + 0.5f) / ramp);
        _envelope = new float[raw.Length];
        for (var n = 0; n < raw.Length; n++)
        {
            var acc = 0f;
            for (var k = 0; k < ramp; k++)
            {
                var idx = n - k + ramp / 2;
                if ((uint)idx < (uint)raw.Length) acc += raw[idx] * kernel[k];
            }
            _envelope[n] = acc / sum;
        }
    }

    public string Text { get; }
    public bool Repeat { get; }

    protected override int Generate(Span<float> output)
    {
        var written = 0;
        while (written < output.Length)
        {
            if (_position == _envelope.Length)
            {
                if (!Repeat) return written == 0 ? -1 : written;
                _position = 0;
            }
            var n = Math.Min(output.Length - written, _envelope.Length - _position);
            _envelope.AsSpan(_position, n).CopyTo(output[written..]);
            _position += n;
            written += n;
        }
        return written;
    }
}

/// <summary>
/// Decodes CW from an envelope (for example |x| after a narrow filter). Adapts the on/off threshold to the
/// signal level and classifies durations against the dot length for <see cref="WordsPerMinute"/>.
/// Decoded characters are published on message port <c>text</c> and appended to <see cref="Text"/>.
/// </summary>
public sealed class MorseDecoder : SinkBlock<float>
{
    private readonly double _sampleRate;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _symbol = new();
    private readonly Lock _lock = new();
    private float _peak = 1e-6f;
    private float _floor;
    private bool _on;
    private long _run;
    private bool _wordSpacePending;

    public MorseDecoder(double sampleRate, double wordsPerMinute = 20) : base("morse_decoder")
    {
        _sampleRate = sampleRate;
        WordsPerMinute = wordsPerMinute;
        RegisterMessageOutput("text");
    }

    public double WordsPerMinute { get; set; }

    public string Text
    {
        get
        {
            lock (_lock) return _text.ToString();
        }
    }

    public event Action<string>? TextDecoded;

    protected override bool Consume(ReadOnlySpan<float> input)
    {
        var unit = Morse.DotSeconds(WordsPerMinute) * _sampleRate;
        foreach (var x in input)
        {
            var v = MathF.Abs(x);
            _peak = Math.Max(v, _peak * 0.99995f);
            _floor = v < _floor ? v : _floor + 0.00001f * (v - _floor);
            var threshold = _floor + 0.5f * (_peak - _floor);
            var on = _on ? v > threshold * 0.8f : v > threshold;
            if (on == _on)
            {
                _run++;
                // A long silence ends the current letter and the word.
                if (!_on && _symbol.Length > 0 && _run > 5 * unit) FlushLetter(wordGap: true);
                continue;
            }
            if (_on) _symbol.Append(_run < 2 * unit ? '.' : '-');
            else if (_run > 2 * unit && _symbol.Length > 0) FlushLetter(wordGap: _run > 5 * unit);
            else if (_wordSpacePending && _run > 5 * unit) EmitWordSpace();
            _on = on;
            _run = 1;
        }
        return true;
    }

    private void FlushLetter(bool wordGap)
    {
        var c = Morse.Decode(_symbol.ToString());
        _symbol.Clear();
        if (c is null) return;
        Emit(c.Value.ToString());
        if (wordGap) _wordSpacePending = true;
    }

    private void EmitWordSpace()
    {
        _wordSpacePending = false;
        Emit(" ");
    }

    private void Emit(string s)
    {
        if (_wordSpacePending && s != " ")
        {
            _wordSpacePending = false;
            Emit(" ");
        }
        lock (_lock) _text.Append(s);
        PublishMessage("text", Pmt.Symbol(s));
        TextDecoded?.Invoke(s);
    }
}
