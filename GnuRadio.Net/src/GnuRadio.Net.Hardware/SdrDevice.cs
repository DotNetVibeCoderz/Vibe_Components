namespace GnuRadio.Hardware;

/// <summary>Direction of a stream on an SDR.</summary>
public enum SdrDirection
{
    Receive,
    Transmit,
}

/// <summary>
/// A software-defined radio front end. Implementations: <see cref="SoapySdrDevice"/> (any SoapySDR-supported
/// hardware), <see cref="RtlTcpDevice"/> (rtl_tcp servers) and <see cref="SimulatedSdrDevice"/> (no hardware).
/// Settings may be changed while streaming.
/// </summary>
public interface ISdrDevice : IDisposable
{
    string Driver { get; }
    string Label { get; }
    bool CanTransmit { get; }

    double SampleRate { get; set; }
    double CenterFrequency { get; set; }
    double Bandwidth { get; set; }

    /// <summary>Overall gain in dB.</summary>
    double Gain { get; set; }

    bool AutomaticGain { get; set; }

    void Start(SdrDirection direction = SdrDirection.Receive);
    void Stop();

    /// <summary>Reads up to <c>buffer.Length</c> samples; returns the count, 0 on timeout.</summary>
    int Read(Span<Complex32> buffer, TimeSpan timeout);

    /// <summary>Writes samples for transmission; returns the count accepted.</summary>
    int Write(ReadOnlySpan<Complex32> buffer, TimeSpan timeout);
}

/// <summary>
/// Receives from any <see cref="ISdrDevice"/> (the role of GNU Radio's <c>soapy.source</c>, <c>uhd.usrp_source</c>,
/// <c>osmosdr.source</c>). Message port <c>cmd</c> takes a dict with <c>freq</c>, <c>gain</c>, <c>rate</c>, <c>bw</c>.
/// The block owns the device and disposes it when the flowgraph stops, unless <c>ownsDevice</c> is false.
/// </summary>
public sealed class SdrSource : SourceBlock<Complex32>
{
    private readonly bool _ownsDevice;

    public SdrSource(ISdrDevice device, bool ownsDevice = true) : base("sdr_source")
    {
        Device = device;
        _ownsDevice = ownsDevice;
        Alias = $"{device.Driver}_source{Id}";
        RegisterMessageInput("cmd", Command);
    }

    public ISdrDevice Device { get; }

    /// <summary>Read timeout per work call.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(100);

    private void Command(Pmt msg) => SdrCommands.Apply(Device, msg);

    public override void Start() => Device.Start(SdrDirection.Receive);

    public override void Stop()
    {
        Device.Stop();
        if (_ownsDevice) Device.Dispose();
    }

    protected override int Generate(Span<Complex32> output) => Device.Read(output, Timeout);
}

/// <summary>Transmits through an <see cref="ISdrDevice"/> (GNU Radio's <c>soapy.sink</c>, <c>uhd.usrp_sink</c>).</summary>
public sealed class SdrSink : SinkBlock<Complex32>
{
    private readonly bool _ownsDevice;

    public SdrSink(ISdrDevice device, bool ownsDevice = true) : base("sdr_sink")
    {
        if (!device.CanTransmit) throw new NotSupportedException($"{device.Label} cannot transmit.");
        Device = device;
        _ownsDevice = ownsDevice;
        RegisterMessageInput("cmd", m => SdrCommands.Apply(Device, m));
    }

    public ISdrDevice Device { get; }

    public override void Start() => Device.Start(SdrDirection.Transmit);

    public override void Stop()
    {
        Device.Stop();
        if (_ownsDevice) Device.Dispose();
    }

    protected override bool Consume(ReadOnlySpan<Complex32> input)
    {
        var written = 0;
        while (written < input.Length)
        {
            var n = Device.Write(input[written..], TimeSpan.FromMilliseconds(500));
            if (n <= 0) break;
            written += n;
        }
        return true;
    }
}

internal static class SdrCommands
{
    public static void Apply(ISdrDevice device, Pmt msg)
    {
        var d = msg.AsDict();
        if (d.TryGetValue("freq", out var f)) device.CenterFrequency = f.ToDouble();
        if (d.TryGetValue("gain", out var g)) device.Gain = g.ToDouble();
        if (d.TryGetValue("rate", out var r)) device.SampleRate = r.ToDouble();
        if (d.TryGetValue("bw", out var b)) device.Bandwidth = b.ToDouble();
        if (d.TryGetValue("agc", out var a)) device.AutomaticGain = a.ToBool();
    }
}

/// <summary>Shortcuts for the radios listed in GNU Radio's hardware support, all through SoapySDR modules.</summary>
public static class Sdr
{
    /// <summary>Ettus USRP (B200/B210, N2x0, X3x0…) via SoapyUHD.</summary>
    public static SoapySdrDevice Usrp(string extraArgs = "") => Open("uhd", extraArgs);

    /// <summary>ADALM-PLUTO via SoapyPlutoSDR (USB or <c>hostname=192.168.2.1</c>).</summary>
    public static SoapySdrDevice PlutoSdr(string extraArgs = "") => Open("plutosdr", extraArgs);

    /// <summary>LimeSDR via SoapyLMS7.</summary>
    public static SoapySdrDevice LimeSdr(string extraArgs = "") => Open("lime", extraArgs);

    /// <summary>HackRF One via SoapyHackRF.</summary>
    public static SoapySdrDevice HackRf(string extraArgs = "") => Open("hackrf", extraArgs);

    /// <summary>Nuand bladeRF via SoapyBladeRF.</summary>
    public static SoapySdrDevice BladeRf(string extraArgs = "") => Open("bladerf", extraArgs);

    /// <summary>RTL2832U dongles via SoapyRTLSDR. For a remote dongle use <see cref="RtlTcpDevice"/>.</summary>
    public static SoapySdrDevice RtlSdr(string extraArgs = "") => Open("rtlsdr", extraArgs);

    /// <summary>Airspy R2/Mini via SoapyAirspy.</summary>
    public static SoapySdrDevice Airspy(string extraArgs = "") => Open("airspy", extraArgs);

    private static SoapySdrDevice Open(string driver, string extraArgs) =>
        new(string.IsNullOrWhiteSpace(extraArgs) ? $"driver={driver}" : $"driver={driver},{extraArgs}");
}
