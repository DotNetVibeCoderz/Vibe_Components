namespace Medical.Rounds;

/// <summary>
/// Medical.Net Rounds — the ward-round companion of the HIS. Works offline (SQLite), syncs every 30 seconds when the
/// network is there. Made by Gravicode Studios, led by Kang Fadhil.
/// </summary>
public partial class App : Application
{
    public static RoundsApp Model { get; private set; } = default!;
    public static string Lang { get; set; } = Preferences.Default.Get("lang", "id");
    public static string T(string en, string id) => Lang == "en" ? en : id;

    /// <summary><c>--capture &lt;dir&gt; [en|id]</c>: renders every page to PNG (documentation screenshots) and exits.</summary>
    public static (string Dir, string Lang)? Capture { get; } = ParseCapture();

    private IDispatcherTimer? _timer;

    public App()
    {
        InitializeComponent();
        if (Capture is { } c) Lang = c.Lang;
        Model = Create(LoadSettings());
    }

    public static RoundsSettings LoadSettings()
    {
        var d = RoundsSettings.Demo;
        var p = Preferences.Default;
        return new RoundsSettings(p.Get("server", DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5300" : d.ServerUrl),
            p.Get("tenant", d.Tenant), p.Get("ward", d.Ward), p.Get("nurse", d.Nurse), p.Get("key", d.DeviceKey), p.Get("device", d.DeviceId));
    }

    public static void SaveSettings(RoundsSettings s)
    {
        var p = Preferences.Default;
        p.Set("server", s.ServerUrl); p.Set("tenant", s.Tenant); p.Set("ward", s.Ward); p.Set("nurse", s.Nurse); p.Set("key", s.DeviceKey); p.Set("device", s.DeviceId);
        Model.Dispose();
        Model = Create(s);
    }

    private static RoundsApp Create(RoundsSettings s)
    {
        string db = Capture is not null ? ":memory:" : Path.Combine(FileSystem.AppDataDirectory, $"rounds-{s.Tenant}-{s.Ward}.db");
        return new RoundsApp(s, db);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new NavigationPage(new WardPage()) { BarBackgroundColor = Color.FromArgb("#14233A"), BarTextColor = Colors.White })
        {
            Title = "Medical.Net Rounds", Width = 520, Height = 940,
        };
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += async (_, _) => { if (Capture is null) await Model.SyncAsync(); };
        _timer.Start();
        if (Capture is { } c) window.Created += async (_, _) => await Screens.CaptureAllAsync(window, c.Dir);
        return window;
    }

    private static (string, string)? ParseCapture()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--capture");
        return i >= 0 && i + 1 < args.Length ? (args[i + 1], i + 2 < args.Length ? args[i + 2] : "id") : null;
    }
}
