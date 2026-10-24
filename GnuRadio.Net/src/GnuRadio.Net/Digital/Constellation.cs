using System.Numerics;

namespace GnuRadio.Digital;

/// <summary>
/// A digital constellation: the complex point for every symbol value, plus hard and soft decisions
/// (GNU Radio's <c>digital.constellation</c>). Points are indexed by symbol value and normalised to unit
/// average power; the built-in PSK and square-QAM constellations are Gray coded.
/// </summary>
/// <remarks>
/// Soft decisions are max-log LLRs with the convention used throughout GnuRadio.Net's FEC:
/// <c>LLR = log P(bit = 0) / P(bit = 1)</c>, so a positive value means "0".
/// Bits of a symbol are MSB first.
/// </remarks>
public sealed class Constellation
{
    private readonly Kind _kind;
    private readonly int _pamLevels;
    private readonly int[] _pamGrayToLevel = [];
    private readonly int[] _pamLevelToGray = [];
    private readonly float _qamScale;
    private readonly float _pskOffset;

    private enum Kind
    {
        Generic,
        Psk,
        SquareQam,
    }

    public Constellation(string name, Complex32[] points, int rotationalSymmetry = 1)
        : this(name, points, rotationalSymmetry, Kind.Generic, 0, 0, 0)
    {
    }

    private Constellation(string name, Complex32[] points, int rotationalSymmetry, Kind kind, int pamLevels, float qamScale, float pskOffset)
    {
        if (!BitOperations.IsPow2(points.Length)) throw new ArgumentException("The number of points must be a power of two.", nameof(points));
        Name = name;
        Points = points;
        Arity = points.Length;
        BitsPerSymbol = BitOperations.Log2((uint)points.Length);
        RotationalSymmetry = rotationalSymmetry;
        _kind = kind;
        _pamLevels = pamLevels;
        _qamScale = qamScale;
        _pskOffset = pskOffset;
        if (kind == Kind.SquareQam)
        {
            _pamGrayToLevel = new int[pamLevels];
            _pamLevelToGray = new int[pamLevels];
            for (var level = 0; level < pamLevels; level++)
            {
                var g = level ^ (level >> 1);
                _pamLevelToGray[level] = g;
                _pamGrayToLevel[g] = level;
            }
        }
    }

    public string Name { get; }
    public IReadOnlyList<Complex32> Points { get; }
    public int Arity { get; }
    public int BitsPerSymbol { get; }

    /// <summary>Rotations that map the constellation onto itself (2 for BPSK, 4 for QPSK/QAM, 8 for 8PSK).</summary>
    public int RotationalSymmetry { get; }

    public Complex32 Map(int symbol) => Points[symbol];

    /// <summary>Normalises <paramref name="points"/> to unit average power.</summary>
    public static Complex32[] Normalize(Complex32[] points)
    {
        var p = points.Average(c => c.MagnitudeSquared);
        var k = 1f / MathF.Sqrt(p);
        return points.Select(c => c * k).ToArray();
    }

    /// <summary>BPSK: symbol 0 → −1, symbol 1 → +1 (GNU Radio's <c>constellation_bpsk</c>).</summary>
    public static Constellation Bpsk() => new("BPSK", [new Complex32(-1, 0), new Complex32(1, 0)], 2, Kind.Generic, 0, 0, 0);

    /// <summary>
    /// QPSK with Gray coding: MSB selects the sign of Q, LSB the sign of I
    /// (points (±1 ± j)/√2, GNU Radio's <c>constellation_qpsk</c> ordering).
    /// </summary>
    public static Constellation Qpsk()
    {
        var s = 0.70710678f;
        return new Constellation("QPSK", [new(-s, -s), new(s, -s), new(-s, s), new(s, s)], 4, Kind.Generic, 0, 0, 0);
    }

    /// <summary>Gray-coded M-PSK on the unit circle, first point at <paramref name="offset"/> radians.</summary>
    public static Constellation Psk(int order, float offset = 0)
    {
        var points = new Complex32[order];
        for (var k = 0; k < order; k++)
        {
            var gray = k ^ (k >> 1);
            points[gray] = Complex32.FromPolar(1f, offset + 2 * MathF.PI * k / order);
        }
        return new Constellation($"{order}PSK", points, order, Kind.Psk, 0, 0, offset);
    }

    /// <summary>
    /// 8PSK rotated by π/8 so no point sits on an axis, as GNU Radio's <c>constellation_8psk</c>; this is the
    /// orientation an 8th-order Costas loop locks to.
    /// </summary>
    public static Constellation Psk8() => Psk(8, MathF.PI / 8);

    /// <summary>Gray-coded square M-QAM (M = 4, 16, 64, 256…), unit average power.</summary>
    public static Constellation Qam(int order)
    {
        var bits = BitOperations.Log2((uint)order);
        if (!BitOperations.IsPow2(order) || bits % 2 != 0)
            throw new ArgumentException("Square QAM needs M = 4^k.", nameof(order));
        var levels = 1 << (bits / 2);
        var scale = 1f / MathF.Sqrt(2f * (order - 1) / 3f);
        var points = new Complex32[order];
        for (var iLevel = 0; iLevel < levels; iLevel++)
        for (var qLevel = 0; qLevel < levels; qLevel++)
        {
            var symbol = ((iLevel ^ (iLevel >> 1)) << (bits / 2)) | (qLevel ^ (qLevel >> 1));
            points[symbol] = new Complex32((2 * iLevel - levels + 1) * scale, (2 * qLevel - levels + 1) * scale);
        }
        return new Constellation($"{order}QAM", points, 4, Kind.SquareQam, levels, scale, 0);
    }

    /// <summary>Looks up a constellation by name: bpsk, qpsk, 8psk, 16qam, 64qam, 256qam.</summary>
    public static Constellation FromName(string name) => name.ToLowerInvariant() switch
    {
        "bpsk" => Bpsk(),
        "qpsk" => Qpsk(),
        "8psk" => Psk8(),
        "16psk" => Psk(16),
        "16qam" => Qam(16),
        "64qam" => Qam(64),
        "256qam" => Qam(256),
        _ => throw new ArgumentException($"Unknown constellation '{name}'.", nameof(name)),
    };

    /// <summary>Hard decision: the symbol whose point is nearest to <paramref name="sample"/>.</summary>
    public int Decide(Complex32 sample)
    {
        switch (_kind)
        {
            case Kind.Psk:
            {
                var step = 2 * MathF.PI / Arity;
                var k = (int)MathF.Round((sample.Phase - _pskOffset) / step);
                k = ((k % Arity) + Arity) % Arity;
                return k ^ (k >> 1);
            }
            case Kind.SquareQam:
            {
                var half = BitsPerSymbol / 2;
                var i = SliceLevel(sample.Real);
                var q = SliceLevel(sample.Imaginary);
                return (_pamLevelToGray[i] << half) | _pamLevelToGray[q];
            }
            default:
            {
                var best = 0;
                var bestDist = float.MaxValue;
                for (var s = 0; s < Arity; s++)
                {
                    var d = (sample - Points[s]).MagnitudeSquared;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = s;
                    }
                }
                return best;
            }
        }
    }

    private int SliceLevel(float v)
    {
        var level = (int)MathF.Floor((v / _qamScale + _pamLevels - 1) / 2f + 0.5f);
        return Math.Clamp(level, 0, _pamLevels - 1);
    }

    /// <summary>
    /// Max-log LLRs of the symbol's bits, MSB first, into <paramref name="llrs"/> (length <see cref="BitsPerSymbol"/>).
    /// <paramref name="noiseVariance"/> is the complex noise variance per sample.
    /// </summary>
    public void SoftDecision(Complex32 sample, float noiseVariance, Span<float> llrs)
    {
        Span<float> min0 = stackalloc float[BitsPerSymbol];
        Span<float> min1 = stackalloc float[BitsPerSymbol];
        min0.Fill(float.MaxValue);
        min1.Fill(float.MaxValue);
        for (var s = 0; s < Arity; s++)
        {
            var d = (sample - Points[s]).MagnitudeSquared;
            for (var b = 0; b < BitsPerSymbol; b++)
            {
                var bit = (s >> (BitsPerSymbol - 1 - b)) & 1;
                if (bit == 0) { if (d < min0[b]) min0[b] = d; }
                else if (d < min1[b]) min1[b] = d;
            }
        }
        var inv = 1f / Math.Max(noiseVariance, 1e-9f);
        for (var b = 0; b < BitsPerSymbol; b++) llrs[b] = (min1[b] - min0[b]) * inv;
    }

    /// <summary>Error vector magnitude (RMS, relative to unit power) of samples against their decisions.</summary>
    public double Evm(ReadOnlySpan<Complex32> samples)
    {
        if (samples.IsEmpty) return 0;
        var e = 0.0;
        foreach (var s in samples) e += (s - Points[Decide(s)]).MagnitudeSquared;
        return Math.Sqrt(e / samples.Length);
    }

    public override string ToString() => Name;
}
