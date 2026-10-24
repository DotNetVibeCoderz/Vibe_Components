// GnuRadio.Net quick start: a QPSK link over a noisy channel, decoded back to bytes.
// Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
using System.Text;
using GnuRadio;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Fec;

var message = Encoding.UTF8.GetBytes("Hello from GnuRadio.Net! ");
var payload = Enumerable.Range(0, 200).SelectMany(_ => message).ToArray();

var qpsk = Constellation.Qpsk();
var noise = BerMath.NoiseVoltage(ebN0Db: 12, qpsk.BitsPerSymbol, samplesPerSymbol: 4) / 2f;
var received = new VectorSink<Complex32>();

var fg = new Flowgraph("quick_start");
fg.Connect(
    new VectorSource<byte>(payload),
    new ConstellationModulator(qpsk, samplesPerSymbol: 4),
    new ChannelModel(noise, frequencyOffset: 0.001),
    new ConstellationReceiver(qpsk, samplesPerSymbol: 4),
    received);
fg.Run();

var symbols = received.Data;
Console.WriteLine($"Received {symbols.Length} symbols, EVM {qpsk.Evm(symbols.AsSpan(1000)) * 100:0.0} %");
Console.WriteLine(fg.ToDot());
