# GnuRadio.Net Gallery

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/gallery.md)

Two apps render the same 17 demos from `samples/GnuRadio.Net.Gallery.Shared`:

```bash
dotnet run --project samples/GnuRadio.Net.Gallery.Web        # Blazor, http://localhost:5180
dotnet run --project samples/GnuRadio.Net.Gallery.Desktop    # Avalonia
```

Both switch between English and Bahasa Indonesia. Each demo has a front panel (frequency readouts, knobs, choices,
buttons), instrument screens, a live readout strip, an explanation, and its C# sample code.

| Category | Demos |
|---|---|
| Spectrum & waterfall | FM band scanner (simulated SDR, signal detection, click to tune) |
| Analog | Broadcast FM receiver (with Windows audio output) · AM & SSB · CW/Morse with decoding |
| Digital | PSK & QAM modem · FSK/GFSK/GMSK · OFDM link |
| Synchronisation | Carrier & timing recovery (Costas lock-in trace) |
| Channels | Rayleigh/Rician fading with Doppler |
| Error correction | BER laboratory: convolutional, Reed–Solomon, LDPC, Polar vs theory |
| Filters & DSP | FIR designer with live response |
| Extensions | LoRa chirps · FMCW radar · CubeSat AX.25 telemetry · machine learning for RF |
| Interop | ZeroMQ bridge · running a `.grc` file with C# code generation |

| Web | Desktop |
|---|---|
| ![](../images/web-carrier-sync.png) | ![](../images/desktop-carrier-sync.png) |
| ![](../images/web-fading.png) | ![](../images/desktop-fmcw-radar.png) |

## Adding a demo

Derive from `Demo`, build the flowgraph in `Build()`, register panels with `AddView`, controls with `AddParameter`,
readouts with `AddReadout`, and add the factory to `DemoCatalog`:

```csharp
public sealed class ToneDemo : Demo
{
    private SignalSource<Complex32>? _tone;

    public ToneDemo() => AddParameter(new NumberParameter("f", new("Tone", "Nada"), -20e3, 20e3, 100, 5e3,
        v => { if (_tone is not null) _tone.Frequency = v; }) { Unit = "Hz" });

    public override string Id => "tone";
    public override DemoCategory Category => DemoCategory.Spectrum;
    public override Text Title => new("Tone", "Nada");
    public override Text Summary => new("A single tone.", "Satu nada.");
    public override string Code => "…";

    protected override Flowgraph Build()
    {
        _tone = new SignalSource<Complex32>(48e3, Waveform.Cosine, 5e3);
        var spectrum = new FrequencySink(1024, 48e3);
        var fg = new Flowgraph("tone");
        fg.Connect(_tone, new Throttle<Complex32>(48e3), spectrum);
        AddView(new SpectrumPanel(new("Spectrum", "Spektrum"), spectrum));
        return fg;
    }
}
```

Both galleries pick it up automatically. `GalleryDemoTests` starts every demo headlessly in CI.

Screenshots: the web ones are taken with headless Edge; the desktop app renders them itself with
`dotnet run --project samples/GnuRadio.Net.Gallery.Desktop -- --screenshots docs/images` (Avalonia headless + Skia).
