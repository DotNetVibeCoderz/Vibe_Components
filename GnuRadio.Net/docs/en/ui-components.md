# UI components

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/ui-components.md)

Both libraries render the same instrument displays: dark screens with amber and cyan traces, grids with nice ticks,
markers and click-to-tune. They read from the visual sinks in `GnuRadio.Sinks`, which publish frames with a version
counter — a display copies data only when the version changed, so idle displays cost nothing and rendering never
blocks signal processing.

| Sink | Blazor | Avalonia |
|---|---|---|
| `FrequencySink` | `<SpectrumView>` | `SpectrumView` |
| `WaterfallSink` | `<WaterfallView>` | `WaterfallView` |
| `ConstellationSink` | `<ConstellationView>` | `ConstellationView` |
| `TimeSink<T>` | `<TimeScope T="…">` | `TimeScope` |
| `VectorFrameSink` or a data function | `<VectorView>` | `VectorView` |
| series functions | `<LineChart>` | `LineChart` |
| `Flowgraph` | `<FlowgraphView>` | `FlowgraphView` |
| controls | `<Knob>`, `<FrequencyReadout>`, `<LevelMeter>` | `Knob`, `FrequencyReadout` |

## Blazor

Requires interactive rendering (Server or WebAssembly). Add the stylesheet once:

```html
<link rel="stylesheet" href="_content/GnuRadio.Net.Blazor/gnuradio.css" />
```

```razor
@using GnuRadio.Blazor.Components

<SpectrumView Sink="_spectrum" MinDb="-100" MaxDb="-20" Title="RF" Height="260px"
              MarkerSource="() => _markers" OnTune="f => _receiver.CenterFrequency = f" />
<WaterfallView Sink="_waterfall" MinDb="-100" MaxDb="-35" Colormap="Colormap.Phosphor" />
<ConstellationView Sink="_constellation" Reference="Constellation.Qam(16).Points" Scale="1.4f" />
<TimeScope T="float" Sink="_audio" Min="-1" Max="1" />
<FrequencyReadout @bind-Value="_frequency" Min="88e6" Max="108e6" Label="Tuned" />
<Knob Label="Noise" @bind-Value="_noise" Min="0" Max="1" Step="0.01" />
```

Data travels to the browser as raw float bytes (Blazor sends `byte[]` as binary), and drawing is canvas 2D in
`gnuradio.js`. Colours are CSS custom properties (`--gr-screen`, `--gr-trace`, `--gr-accent`…): redefine them on
`:root` to re-theme; a dark chassis is applied automatically under `prefers-color-scheme: dark`.

Live parameters: `MarkerSource`, `DataSource` (VectorView) and `SeriesSource`/`RevisionSource` (LineChart) are
functions polled every frame, so values computed by your flowgraph appear without re-rendering the page.

## Avalonia

```csharp
using GnuRadio.Avalonia;

var spectrum = new SpectrumView { Sink = freqSink, MinDb = -100, MaxDb = -20, Title = "RF", Height = 260 };
spectrum.Tuned += f => xlate.CenterFrequency = f - center;
var waterfall = new WaterfallView { Sink = waterfallSink };
var knob = new Knob { Label = "Noise", Minimum = 0, Maximum = 1, Step = 0.01 };
knob.ValueChangedByUser += v => noise.NoiseVoltage = (float)v;
```

Each display polls its sink with a `DispatcherTimer` while it is on screen (`Fps`, default 30). The waterfall renders
into a `WriteableBitmap` that shifts one row per spectrum. Colours and fonts live in `GrTheme`.

![Desktop gallery](../images/desktop-ofdm.png)
