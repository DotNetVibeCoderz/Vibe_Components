using System.Globalization;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenSceneGraph.Net.Rendering;

namespace OpenSceneGraph.Net.Simulator;

public sealed record KeyRow(string Keys, string Action);

public partial class MainWindow : Window
{
    private readonly SimSettings _settings;
    private SimWorld? _world;
    private bool _syncing;
    private int _frames;
    private RenderStats? _stats;
    private double _lastStatus;

    public MainWindow() : this(new SimSettings()) { }

    public MainWindow(SimSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        VAircraft.Click += (_, _) => ChangeVehicle(VehicleKind.Aircraft);
        VQuad.Click += (_, _) => ChangeVehicle(VehicleKind.Quadcopter);
        VGround.Click += (_, _) => ChangeVehicle(VehicleKind.Ground);
        VShip.Click += (_, _) => ChangeVehicle(VehicleKind.Ship);
        CamChase.Click += (_, _) => SetCamera(CameraMode.Chase);
        CamCockpit.Click += (_, _) => SetCamera(CameraMode.Cockpit);
        PauseButton.Click += (_, _) => TogglePause();
        ResetButton.Click += (_, _) => { _world?.Reset(); FocusView(); };
        LangEn.Click += (_, _) => Loc.Instance.Lang = "en";
        LangId.Click += (_, _) => Loc.Instance.Lang = "id";
        AboutButton.Click += (_, _) => AboutPanel.IsVisible = true;
        AboutClose.Click += (_, _) => { AboutPanel.IsVisible = false; FocusView(); };
        NewTerrainButton.Click += (_, _) => { _settings.Seed = Random.Shared.Next(1, 100000); Rebuild(); };
        TerrainBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || TerrainBox.SelectedIndex < 0) return;
            var t = (TerrainKind)TerrainBox.SelectedIndex;
            if (t == _settings.Terrain) return;
            _settings.Terrain = t;
            if (t != TerrainKind.Archipelago && _settings.Vehicle == VehicleKind.Ship) _settings.Vehicle = VehicleKind.Aircraft;
            Rebuild();
        };
        HourSlider.ValueChanged += (_, e) => { if (_syncing) return; _settings.Hour = (float)e.NewValue; ApplyLive(); };
        VisSlider.ValueChanged += (_, e) => { if (_syncing) return; _settings.Visibility = (float)e.NewValue; ApplyLive(); };
        PNone.Click += (_, _) => SetPrecip(Precipitation.None);
        PRain.Click += (_, _) => SetPrecip(Precipitation.Rain);
        PSnow.Click += (_, _) => SetPrecip(Precipitation.Snow);
        DisSwitch.IsCheckedChanged += (_, _) => { if (_syncing) return; _world?.SetDis(DisSwitch.IsChecked == true); FocusView(); };

        View.FrameRendered += (_, st) => { _stats = st; _frames++; UpdateHud(); };
        View.RenderingFailed += (_, ex) => { ErrorText.Text = Loc.Instance["RenderFailed"] + "\n\n" + ex.Message; ErrorPanel.IsVisible = true; };

        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        Loc.Instance.LanguageChanged += OnLanguageChanged;

        var v = typeof(Viewer).Assembly.GetName().Version;
        VersionText.Text = $"{v?.ToString(3)} · .NET {Environment.Version.ToString(2)} · Avalonia 11.3";
        Rebuild();
        Opened += (_, _) => FocusView();
        Closed += (_, _) => { _world?.Dispose(); _world = null; };
    }

    public SimWorld? World => _world;

    /// <summary>Applies command-line camera / DIS choices once the window is open.</summary>
    public void ApplyStartOptions(CameraMode camera, bool dis)
    {
        if (_world is null) return;
        _world.CameraMode = camera;
        if (dis) _world.SetDis(true);
        SyncControls();
    }
    public int FramesRendered => _frames;
    public Avalonia.OsgView OsgView => View;

    // ------------------------------------------------------------------ scenario
    private void Rebuild()
    {
        bool dis = _world?.DisEnabled ?? DisSwitch.IsChecked == true;
        var mode = _world?.CameraMode ?? CameraMode.Chase;
        var old = _world;
        var world = new SimWorld(_settings);
        if (Program.Options.ScreenshotPath is not null) world.Viewer.FixedTimeStep = 1.0 / 60;
        View.Viewer = world.Viewer;
        View.Scene = world.Scene;
        old?.Dispose();
        _world = world;
        world.CameraMode = mode;
        world.SetDis(dis);
        Map.SetTerrain(world.Field, world.Ramp, world.HeightMin, world.HeightMax, world.HasWater);
        Map.ViewSize = _settings.Vehicle switch { VehicleKind.Aircraft => 0, VehicleKind.Quadcopter => 700, VehicleKind.Ground => 800, _ => 2600 };
        SyncControls();
        FocusView();
    }

    private void ChangeVehicle(VehicleKind kind)
    {
        if (_settings.Vehicle == kind && _world is not null) { SyncControls(); FocusView(); return; }
        _settings.Vehicle = kind;
        if (kind == VehicleKind.Ship) _settings.Terrain = TerrainKind.Archipelago;
        Rebuild();
    }

    private void SetCamera(CameraMode mode)
    {
        if (_world is not null) _world.CameraMode = mode;
        SyncControls();
        FocusView();
    }

    private void SetPrecip(Precipitation p)
    {
        _settings.Precipitation = p;
        ApplyLive();
        SyncControls();
        FocusView();
    }

    private void ApplyLive()
    {
        _world?.ApplyEnvironment();
        HourText.Text = FormatHour(_settings.Hour);
        VisText.Text = _settings.Visibility >= 1000 ? $"{_settings.Visibility / 1000:F1} km" : $"{_settings.Visibility:F0} m";
    }

    private void TogglePause()
    {
        if (_world is null) return;
        _world.Paused = !_world.Paused;
        SyncControls();
        FocusView();
    }

    private void FocusView() => Dispatcher.UIThread.Post(() => View.Focus(NavigationMethod.Pointer), DispatcherPriority.Input);

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (AboutPanel.IsVisible && e.Key == Key.Escape) { AboutPanel.IsVisible = false; e.Handled = true; return; }
        if (e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.C: SetCamera(_world?.CameraMode == CameraMode.Chase ? CameraMode.Cockpit : CameraMode.Chase); break;
            case Key.P: TogglePause(); break;
            case Key.R: _world?.Reset(); break;
            case Key.H: KeyMapPanel.IsVisible = !KeyMapPanel.IsVisible; break;
            default: return;
        }
        // let the 3D view see the key as well only for driving keys; these toggles are consumed here
        e.Handled = true;
    }

    private static string FormatHour(float h)
    {
        int m = (int)MathF.Round(h * 60) % (24 * 60);
        return $"{m / 60:00}:{m % 60:00}";
    }

    private void SyncControls()
    {
        _syncing = true;
        var t = Loc.Instance;
        var vk = _settings.Vehicle;
        VAircraft.IsChecked = vk == VehicleKind.Aircraft;
        VQuad.IsChecked = vk == VehicleKind.Quadcopter;
        VGround.IsChecked = vk == VehicleKind.Ground;
        VShip.IsChecked = vk == VehicleKind.Ship;
        var cam = _world?.CameraMode ?? CameraMode.Chase;
        CamChase.IsChecked = cam == CameraMode.Chase;
        CamCockpit.IsChecked = cam == CameraMode.Cockpit;
        PNone.IsChecked = _settings.Precipitation == Precipitation.None;
        PRain.IsChecked = _settings.Precipitation == Precipitation.Rain;
        PSnow.IsChecked = _settings.Precipitation == Precipitation.Snow;
        LangEn.IsChecked = !t.IsIndonesian;
        LangId.IsChecked = t.IsIndonesian;
        PauseLabel.Text = _world?.Paused == true ? t["Resume"] : t["Pause"];

        int sel = (int)_settings.Terrain;
        TerrainBox.ItemsSource = new[] { t["Mountains"], t["Desert"], t["Highlands"], t["Archipelago"] };
        TerrainBox.SelectedIndex = sel;
        HourSlider.Value = _settings.Hour;
        VisSlider.Value = _settings.Visibility;
        DisSwitch.IsChecked = _world?.DisEnabled == true;
        HourText.Text = FormatHour(_settings.Hour);
        VisText.Text = _settings.Visibility >= 1000 ? $"{_settings.Visibility / 1000:F1} km" : $"{_settings.Visibility:F0} m";
        KeyList.ItemsSource = KeyMap(vk);
        SpeedUnit.Text = vk switch { VehicleKind.Aircraft => "kt", VehicleKind.Ship => "kn", _ => "km/h" };
        ThrottleGauge.Minimum = vk == VehicleKind.Ship ? -0.3f : vk == VehicleKind.Quadcopter ? 0 : 0;
        Title = t["AppTitle"];
        _syncing = false;
    }

    private static List<KeyRow> KeyMap(VehicleKind v)
    {
        var t = Loc.Instance;
        var rows = v switch
        {
            VehicleKind.Aircraft => new List<KeyRow>
            {
                new("W / S", t["kThrottle"]), new("A / D", t["kRoll"]), new("↑ / ↓", t["kPitch"]), new("Q / E", t["kRudder"]),
            },
            VehicleKind.Quadcopter => [new("W / S", t["kMove"]), new("A / D", t["kStrafe"]), new("↑ / ↓", t["kClimb"]), new("← / →", t["kYaw"])],
            VehicleKind.Ground => [new("W / S", t["kDrive"]), new("A / D", t["kSteer"]), new("Q / E", t["kTurret"])],
            _ => [new("W / S", t["kTelegraph"]), new("A / D", t["kHelm"])],
        };
        rows.Add(new("C", t["kCamera"]));
        rows.Add(new("P", t["kPause"]));
        rows.Add(new("R", t["kReset"]));
        return rows;
    }

    private void OnLanguageChanged()
    {
        SyncControls();
        UpdateHud();
    }

    // ------------------------------------------------------------------ HUD
    private void UpdateHud()
    {
        if (_world is not { } w) return;
        var v = w.Vehicle;
        var t = Loc.Instance;
        var ci = CultureInfo.InvariantCulture;

        float speed = w.Settings.Vehicle switch
        {
            VehicleKind.Aircraft or VehicleKind.Ship => v.Speed * 1.94384f,
            _ => v.Speed * 3.6f,
        };
        SpeedText.Text = speed.ToString(speed < 100 ? "F1" : "F0", ci);
        AltText.Text = v.Position.Z.ToString("F0", ci);
        AglText.Text = $"{w.Agl:F0} m";
        VsText.Text = $"{w.VerticalSpeed:+0.0;-0.0;0.0}";
        HeadingText.Text = $"{(int)MathF.Round(v.Heading) % 360:000}°";
        Tape.Heading = v.Heading;
        Adi.Pitch = v.Pitch;
        Adi.Roll = v.Roll;
        ThrottleGauge.Value = w.Throttle;
        ThrottleText.Text = $"{w.Throttle * 100:F0}%";

        // mission
        if (w.MissionComplete)
        {
            WaypointText.Text = "✓ " + t["MissionComplete"];
            DistText.Text = BearingText.Text = EtaText.Text = "—";
            Tape.Bug = null;
        }
        else
        {
            var wp = w.Waypoints[w.ActiveWaypoint];
            var d = wp - v.Position;
            float dist = new Vector2(d.X, d.Y).Length();
            float brg = (MathF.Atan2(d.X, d.Y) * 180 / MathF.PI + 360) % 360;
            WaypointText.Text = $"WP {w.ActiveWaypoint + 1}/{w.Waypoints.Count}";
            DistText.Text = dist >= 1000 ? $"{dist / 1000:F2} km" : $"{dist:F0} m";
            BearingText.Text = $"{brg:000}°";
            double eta = dist / MathF.Max(v.Speed, 0.5f);
            EtaText.Text = eta > 5999 ? "--:--" : $"{(int)eta / 60:00}:{(int)eta % 60:00}";
            Tape.Bug = brg;
        }
        var mt = TimeSpan.FromSeconds(w.MissionTime);
        MissionTimeText.Text = $"{(int)mt.TotalMinutes:00}:{mt.Seconds:00}";

        Map.Position = v.Position;
        Map.Heading = v.Heading;
        Map.Waypoints = w.Waypoints;
        Map.Active = w.ActiveWaypoint;

        // DIS
        DisBadge.IsVisible = w.DisEnabled || w.DisError is not null;
        PduText.Text = w.DisEnabled ? w.PdusSent.ToString("N0", ci) : w.DisError is not null ? t["DisError"] : t["DisOff"];
        DisBadgeText.Text = w.DisError is not null ? t["DisError"] : $"{w.PdusSent:N0} PDU";
        DisDot.Fill = w.DisError is not null ? HudPalette.CautionBrush : HudPalette.PhosphorBrush;
        if (w.DisError is not null) ToolTip.SetTip(PduText, w.DisError);

        // banners, most important first
        (string Title, string Text)? banner =
            w.Paused ? (t["Paused"], t["PausedHelp"]) :
            w.Crashed ? (t["Crashed"], t["CrashedHelp"]) :
            w.Aground ? (t["Aground"], t["AgroundHelp"]) :
            w.MissionComplete ? (t["MissionComplete"], t["MissionCompleteHelp"]) :
            w.AtBoundary ? (t["Boundary"], "") : null;
        Banner.IsVisible = banner is not null;
        if (banner is { } b) { BannerTitle.Text = b.Title; BannerText.Text = b.Text; BannerText.IsVisible = b.Text.Length > 0; }

        Tape.InvalidateVisual();
        Adi.InvalidateVisual();
        ThrottleGauge.InvalidateVisual();
        Map.InvalidateVisual();

        double now = Environment.TickCount64 / 1000.0;
        if (now - _lastStatus > 0.5 && _stats is { } st)
        {
            _lastStatus = now;
            StatusText.Text = $"{(w.PrecipitationParticles > 0 ? $"{w.PrecipitationParticles:N0} drops · " : "")}{w.Viewer.FramesPerSecond:F0} fps · {st.DrawItems} draws · {st.Triangles:N0} tris · {View.RendererName}";
        }
    }
}
