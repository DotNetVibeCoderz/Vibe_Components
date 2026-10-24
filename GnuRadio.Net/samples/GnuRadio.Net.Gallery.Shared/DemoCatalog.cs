using GnuRadio.Gallery.Demos;

namespace GnuRadio.Gallery;

/// <summary>Every gallery demo, in display order. Each call creates a fresh instance.</summary>
public static class DemoCatalog
{
    public const string Attribution = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";

    private static readonly Func<Demo>[] Factories =
    [
        () => new BandScannerDemo(),
        () => new FmReceiverDemo(),
        () => new AmSsbDemo(),
        () => new MorseDemo(),
        () => new LinearModemDemo(),
        () => new FskDemo(),
        () => new OfdmDemo(),
        () => new CarrierSyncDemo(),
        () => new FadingDemo(),
        () => new BerLabDemo(),
        () => new FilterDesignerDemo(),
        () => new LoRaDemo(),
        () => new RadarDemo(),
        () => new SatelliteTelemetryDemo(),
        () => new ClassifierDemo(),
        () => new ZeroMqBridgeDemo(),
        () => new GrcImportDemo(),
    ];

    private static readonly Lazy<IReadOnlyList<DemoInfo>> s_infos = new(() =>
        [.. Factories.Select(f =>
        {
            var d = f();
            return new DemoInfo(d.Id, d.Category, d.Title, d.Summary, f);
        })]);

    /// <summary>Metadata of every demo (without starting any flowgraph).</summary>
    public static IReadOnlyList<DemoInfo> All => s_infos.Value;

    public static DemoInfo? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}

/// <summary>Catalogue entry: what the navigation needs, plus a factory for the live demo.</summary>
public sealed record DemoInfo(string Id, DemoCategory Category, Text Title, Text Summary, Func<Demo> Create);
