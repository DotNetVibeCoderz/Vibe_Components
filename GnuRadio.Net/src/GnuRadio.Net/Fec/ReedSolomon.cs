namespace GnuRadio.Fec;

/// <summary>Arithmetic in GF(2^8) with a chosen primitive polynomial.</summary>
public sealed class GaloisField256
{
    private readonly byte[] _exp = new byte[512];
    private readonly int[] _log = new int[256];

    public GaloisField256(int primitivePolynomial = 0x11D)
    {
        Polynomial = primitivePolynomial;
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            _exp[i] = (byte)x;
            _log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= primitivePolynomial;
        }
        if (x != 1) throw new ArgumentException("Polynomial is not primitive.", nameof(primitivePolynomial));
        for (var i = 255; i < 512; i++) _exp[i] = _exp[i - 255];
        _log[0] = -1;
    }

    public int Polynomial { get; }

    public byte Exp(int power) => _exp[((power % 255) + 255) % 255];
    public int Log(byte value) => value == 0 ? throw new ArgumentException("log(0) is undefined.") : _log[value];

    public byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : _exp[_log[a] + _log[b]];

    public byte Divide(byte a, byte b)
    {
        if (b == 0) throw new DivideByZeroException();
        return a == 0 ? (byte)0 : _exp[_log[a] - _log[b] + 255];
    }

    public byte Inverse(byte a) => Divide(1, a);
}

/// <summary>
/// Reed–Solomon codec over GF(2^8) (GNU Radio's <c>fec.rs</c>, Phil Karn's <c>encode_rs_8</c>/<c>decode_rs_8</c>).
/// Systematic: the codeword is the message followed by <see cref="ParityBytes"/> parity bytes. Shortened codes
/// (n &lt; 255) are supported, and up to ParityBytes/2 byte errors per codeword are corrected.
/// </summary>
public sealed class ReedSolomon
{
    private readonly GaloisField256 _gf;
    private readonly byte[] _generator;

    /// <param name="n">Codeword length in bytes (≤ 255).</param>
    /// <param name="k">Message length in bytes.</param>
    /// <param name="firstRoot">First consecutive root (fcr); generator roots are α^(fcr+i).</param>
    /// <param name="primitivePolynomial">Field polynomial, 0x11D by default.</param>
    public ReedSolomon(int n = 255, int k = 223, int firstRoot = 0, int primitivePolynomial = 0x11D)
    {
        if (n is < 2 or > 255 || k < 1 || k >= n) throw new ArgumentOutOfRangeException(nameof(k), "Need 1 ≤ k < n ≤ 255.");
        N = n;
        K = k;
        FirstRoot = firstRoot;
        _gf = new GaloisField256(primitivePolynomial);
        var nroots = n - k;
        // g(x) = Π (x − α^(fcr+i)), coefficients highest degree first.
        var g = new byte[] { 1 };
        for (var i = 0; i < nroots; i++)
        {
            var root = _gf.Exp(firstRoot + i);
            var next = new byte[g.Length + 1];
            for (var j = 0; j < g.Length; j++)
            {
                next[j] ^= g[j];
                next[j + 1] ^= _gf.Multiply(g[j], root);
            }
            g = next;
        }
        _generator = g;
    }

    public int N { get; }
    public int K { get; }
    public int FirstRoot { get; }
    public int ParityBytes => N - K;

    /// <summary>The CCSDS-sized (255, 223) code: corrects 16 byte errors.</summary>
    public static ReedSolomon Rs255_223() => new();

    public byte[] Encode(ReadOnlySpan<byte> message)
    {
        var codeword = new byte[N];
        Encode(message, codeword);
        return codeword;
    }

    public void Encode(ReadOnlySpan<byte> message, Span<byte> codeword)
    {
        if (message.Length != K) throw new ArgumentException($"Message must be {K} bytes.", nameof(message));
        message.CopyTo(codeword);
        var nroots = ParityBytes;
        Span<byte> parity = stackalloc byte[nroots];
        parity.Clear();
        for (var i = 0; i < K; i++)
        {
            var feedback = (byte)(message[i] ^ parity[0]);
            for (var j = 0; j < nroots - 1; j++) parity[j] = (byte)(parity[j + 1] ^ _gf.Multiply(feedback, _generator[j + 1]));
            parity[nroots - 1] = _gf.Multiply(feedback, _generator[nroots]);
        }
        parity.CopyTo(codeword[K..]);
    }

    /// <summary>
    /// Corrects <paramref name="codeword"/> in place. Returns the number of corrected bytes, or −1 when the
    /// errors exceed the code's capability (the codeword is then left unchanged).
    /// </summary>
    public int Decode(Span<byte> codeword)
    {
        if (codeword.Length != N) throw new ArgumentException($"Codeword must be {N} bytes.", nameof(codeword));
        var nroots = ParityBytes;
        Span<byte> syndromes = stackalloc byte[nroots];
        var allZero = true;
        for (var i = 0; i < nroots; i++)
        {
            var root = _gf.Exp(FirstRoot + i);
            byte s = 0;
            foreach (var c in codeword) s = (byte)(_gf.Multiply(s, root) ^ c);
            syndromes[i] = s;
            if (s != 0) allZero = false;
        }
        if (allZero) return 0;

        // Berlekamp–Massey: error locator Λ(x), lowest degree first.
        var lambda = new byte[nroots + 1];
        var b = new byte[nroots + 1];
        lambda[0] = b[0] = 1;
        var l = 0;
        var m = 1;
        byte bScale = 1;
        for (var r = 0; r < nroots; r++)
        {
            var d = syndromes[r];
            for (var i = 1; i <= l; i++) d ^= _gf.Multiply(lambda[i], syndromes[r - i]);
            if (d == 0)
            {
                m++;
                continue;
            }
            var coef = _gf.Divide(d, bScale);
            if (2 * l <= r)
            {
                var t = (byte[])lambda.Clone();
                for (var i = 0; i + m <= nroots; i++) lambda[i + m] ^= _gf.Multiply(coef, b[i]);
                l = r + 1 - l;
                b = t;
                bScale = d;
                m = 1;
            }
            else
            {
                for (var i = 0; i + m <= nroots; i++) lambda[i + m] ^= _gf.Multiply(coef, b[i]);
                m++;
            }
        }
        if (l > nroots / 2) return -1;

        // Chien search over the n positions; position j has degree e = n−1−j and locator X = α^e.
        var positions = new List<int>();
        for (var j = 0; j < N; j++)
        {
            var e = N - 1 - j;
            var xInv = _gf.Exp(-e);
            byte v = 0;
            for (var i = l; i >= 0; i--) v = (byte)(_gf.Multiply(v, xInv) ^ lambda[i]);
            if (v == 0) positions.Add(j);
        }
        if (positions.Count != l) return -1;

        // Ω(x) = S(x)·Λ(x) mod x^nroots.
        var omega = new byte[nroots];
        for (var i = 0; i < nroots; i++)
        {
            byte acc = 0;
            for (var j = 0; j <= Math.Min(i, l); j++) acc ^= _gf.Multiply(lambda[j], syndromes[i - j]);
            omega[i] = acc;
        }

        // Forney: magnitude = X^(1−fcr) · Ω(X⁻¹) / Λ'(X⁻¹).
        var corrections = new byte[positions.Count];
        for (var p = 0; p < positions.Count; p++)
        {
            var e = N - 1 - positions[p];
            var xInv = _gf.Exp(-e);
            byte num = 0;
            for (var i = nroots - 1; i >= 0; i--) num = (byte)(_gf.Multiply(num, xInv) ^ omega[i]);
            byte den = 0;
            for (var i = 1; i <= l; i += 2)
                den ^= _gf.Multiply(lambda[i], _gf.Exp(-e * (i - 1)));
            if (den == 0) return -1;
            corrections[p] = _gf.Multiply(_gf.Divide(num, den), _gf.Exp(e * (1 - FirstRoot)));
        }
        for (var p = 0; p < positions.Count; p++) codeword[positions[p]] ^= corrections[p];
        return positions.Count;
    }
}
