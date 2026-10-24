using System.Text;
using GnuRadio.Blocks;
using GnuRadio.Fec;

namespace GnuRadio.Tests;

public class FecTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void CrcPresetsMatchStandardCheckValues()
    {
        Assert.Equal(0xF4u, Crc.Crc8.Compute(Check));
        Assert.Equal(0x29B1u, Crc.Crc16Ccitt.Compute(Check));
        Assert.Equal(0x906Eu, Crc.Crc16X25.Compute(Check));
        Assert.Equal(0xCBF43926u, Crc.Crc32.Compute(Check));
    }

    [Fact]
    public void CrcAppendAndCheckRoundTrip()
    {
        var framed = Crc.Crc32.Append(Check);
        Assert.True(Crc.Crc32.Check(framed));
        framed[2] ^= 0x10;
        Assert.False(Crc.Crc32.Check(framed));
    }

    [Theory]
    [InlineData(255, 223, 16)]
    [InlineData(255, 239, 8)]
    [InlineData(64, 48, 8)]
    public void ReedSolomonCorrectsUpToHalfTheParity(int n, int k, int errors)
    {
        var rs = new ReedSolomon(n, k);
        var rng = new FastRandom(42);
        var message = new byte[k];
        rng.NextBytes(message);
        var codeword = rs.Encode(message);
        var corrupted = (byte[])codeword.Clone();
        var positions = Enumerable.Range(0, n).OrderBy(_ => rng.Next(1000)).Take(errors).ToArray();
        foreach (var p in positions) corrupted[p] ^= (byte)(1 + rng.Next(255));
        Assert.Equal(errors, rs.Decode(corrupted));
        Assert.Equal(codeword, corrupted);
    }

    [Fact]
    public void ReedSolomonReportsUncorrectable()
    {
        var rs = new ReedSolomon(255, 239);
        var codeword = rs.Encode(new byte[239]);
        for (var i = 0; i < 20; i++) codeword[i * 7] ^= 0x55;
        Assert.Equal(-1, rs.Decode(codeword));
    }

    [Fact]
    public void ViterbiCorrectsScatteredHardErrors()
    {
        var code = new ConvolutionalCode(256);
        var rng = new FastRandom(5);
        var bits = new byte[256];
        rng.NextBits(bits);
        var coded = new byte[code.OutputSize];
        code.Encode(bits, coded);
        for (var i = 10; i < coded.Length; i += 37) coded[i] ^= 1;
        var llr = coded.Select(b => b == 0 ? 1f : -1f).ToArray();
        var decoded = new byte[256];
        code.Decode(llr, decoded);
        Assert.Equal(bits, decoded);
    }

    [Fact]
    public void ConvolutionalCodeBeatsUncodedAt4Db()
    {
        var conv = new ConvolutionalCode(512);
        var coded = BerMath.Simulate(conv, conv, 4, maxBits: 100_000);
        Assert.True(coded.Ber < BerMath.BpskBer(4) / 10, $"coded BER {coded.Ber}");
    }

    [Fact]
    public void LdpcEncodesValidCodewordsAndDecodesNoise()
    {
        var ldpc = LdpcCode.Gallager(504, 3, 6, seed: 2);
        Assert.InRange(ldpc.K, 250, 260);
        var rng = new FastRandom(9);
        var bits = new byte[ldpc.K];
        rng.NextBits(bits);
        var cw = new byte[ldpc.N];
        ldpc.Encode(bits, cw);
        Assert.True(ldpc.IsCodeword(cw));
        var result = BerMath.Simulate(ldpc, ldpc, 3.0, maxBits: 50_000);
        Assert.True(result.Ber < 1e-3, $"LDPC BER {result.Ber}");
    }

    [Fact]
    public void AlistRoundTrips()
    {
        var a = LdpcCode.Gallager(96, 3, 6, seed: 4);
        var b = LdpcCode.FromAlist(a.ToAlist());
        Assert.Equal(a.N, b.N);
        Assert.Equal(a.K, b.K);
    }

    [Fact]
    public void PolarCodeDecodesCleanAndNoisyFrames()
    {
        var polar = new PolarCode(256, 128, designSnrDb: 1);
        var bits = new byte[128];
        new FastRandom(3).NextBits(bits);
        var coded = new byte[256];
        polar.Encode(bits, coded);
        var decoded = new byte[128];
        polar.Decode(coded.Select(b => b == 0 ? 4f : -4f).ToArray(), decoded);
        Assert.Equal(bits, decoded);
        var result = BerMath.Simulate(polar, polar, 4.0, maxBits: 50_000);
        Assert.True(result.Ber < 2e-3, $"Polar BER {result.Ber}");
    }

    [Fact]
    public void ReedSolomonBitAdapterRoundTrips()
    {
        var fec = new ReedSolomonFec(new ReedSolomon(32, 24));
        IFecEncoder e = fec;
        var bits = new byte[e.InputSize];
        new FastRandom(1).NextBits(bits);
        var coded = new byte[e.OutputSize];
        fec.Encode(bits, coded);
        coded[3] ^= 1;
        coded[100] ^= 1;
        var decoded = new byte[e.InputSize];
        fec.Decode(coded.Select(b => b == 0 ? 1f : -1f).ToArray(), decoded);
        Assert.Equal(bits, decoded);
    }

    [Fact]
    public void FecBlocksRunInAFlowgraph()
    {
        var code = new ConvolutionalCode(100);
        var bits = new byte[1000];
        new FastRandom(8).NextBits(bits);
        var fg = new Flowgraph();
        var sink = new VectorSink<byte>();
        fg.Connect(new VectorSource<byte>(bits), new FecEncoderBlock(code), new Digital.BitsToNrz(), new MultiplyConst<float>(-1f),
            new FecDecoderBlock(code), sink);
        fg.Run();
        Assert.Equal(bits, sink.Data);
    }

    [Fact]
    public void TheoreticalBpskBerAt10Db()
    {
        Assert.InRange(BerMath.BpskBer(10), 3.8e-6, 4.0e-6);
    }
}
