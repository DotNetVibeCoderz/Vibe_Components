using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CesiumNet.Gallery.Avalonia.ViewModels;

public sealed record LogEntry(string Time, string Message, bool IsError);

public sealed partial class ActionViewModel(SampleAction action, SampleViewModel owner) : ObservableObject
{
    public SampleAction Action { get; } = action;
    public string Label => owner.L.Get(Action.Label);

    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task Run()
    {
        if (owner.Context is null || IsBusy) return;
        IsBusy = true;
        try { await Action.Run(owner.Context); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { owner.Log("⚠ " + ex.Message, true); }
        finally { IsBusy = false; }
    }

    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>State of one running sample: texts, actions, log and the live reticle readout.</summary>
public sealed partial class SampleViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public SampleViewModel(GallerySample sample, Strings strings, string? ionToken)
    {
        Sample = sample;
        L = strings;
        IonToken = ionToken;
        Options = sample.CreateOptions(ionToken);
        Options.Events.MouseMove = true;
        Options.Events.MouseMoveThrottleMs = 80;
        foreach (var a in sample.Actions) Actions.Add(new ActionViewModel(a, this));
    }

    public GallerySample Sample { get; }
    public Strings L { get; }
    public string? IonToken { get; }
    public CesiumViewerOptions Options { get; }
    public SampleContext? Context { get; private set; }

    public ObservableCollection<ActionViewModel> Actions { get; } = [];
    public ObservableCollection<LogEntry> Entries { get; } = [];

    public string Title => L.Get(Sample.Title);
    public string Summary => L.Get(Sample.Summary);
    public string Eyebrow => L.Get(SampleCatalog.Categories.First(c => c.Id == Sample.CategoryId).Title).ToUpperInvariant();
    public string Coord => Sample.Location is { } p ? GeoFormat.Short(p, 4) : "";
    public string Code => Sample.SourceCode;
    public string FileName => Sample.GetType().Name + ".cs";
    public bool UsesIon => Sample.UsesIon;
    public bool HasActions => Actions.Count > 0;
    public string IonNote => L.Get(GalleryText.IonHint);
    public string LastEvent => Entries.Count > 0 ? Entries[^1].Message : L.Get(GalleryText.NoEvents);

    [ObservableProperty] private string _lat = "—";
    [ObservableProperty] private string _lon = "—";
    [ObservableProperty] private string _alt = "—";
    [ObservableProperty] private string _heading = "—";
    [ObservableProperty] private string _pitch = "—";
    [ObservableProperty] private string _cursor = "—";
    [ObservableProperty] private bool _loadFailed;

    public async void Start(CesiumController cesium)
    {
        _cts = new CancellationTokenSource();
        Context = new SampleContext(cesium, m => Log(m, false), _cts.Token) { IonToken = IonToken };
        cesium.LeftClick += (_, p) => Log(GeoFormat.Describe(p), false);
        cesium.CameraChanged += (_, c) => SetCamera(c);
        cesium.MouseMove += (_, p) => Cursor = p.Position is { } g ? $"{GeoFormat.Lat(g.Latitude)} {GeoFormat.Lon(g.Longitude)} · {GeoFormat.Altitude(g.Height)}" : "—";
        cesium.Error += (_, e) => Log("⚠ " + e, true);
        cesium.DrawCompleted += (_, r) => Log($"Drawn {r.Mode}: {r.Positions.Count} vertices, length {Geodesy.FormatDistance(r.Length)}, area {Geodesy.FormatArea(r.Area)}", false);
        cesium.EntityEdited += (_, r) => Log($"Edited {r.EntityId}: {r.Positions.Count} vertices", false);
        try
        {
            await Sample.RunAsync(Context);
            SetCamera(await cesium.Camera.GetStateAsync());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("⚠ " + ex.Message, true); }
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (Context is not null)
        {
            foreach (var r in Context.Resources) _ = r.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
        }
        Context = null;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(IonNote));
        OnPropertyChanged(nameof(LastEvent));
        foreach (var a in Actions) a.Refresh();
    }

    public void Log(string message, bool isError)
    {
        if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(message, isError));
            return;
        }
        Entries.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, isError));
        if (Entries.Count > 200) Entries.RemoveAt(0);
        OnPropertyChanged(nameof(LastEvent));
    }

    private void SetCamera(CameraState c)
    {
        Lat = GeoFormat.Lat(c.Position.Latitude);
        Lon = GeoFormat.Lon(c.Position.Longitude);
        Alt = GeoFormat.Altitude(c.Position.Height);
        Heading = GeoFormat.Degrees(c.Heading);
        Pitch = GeoFormat.Degrees(c.Pitch);
    }
}
