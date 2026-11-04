using System.Numerics;
using OpenSceneGraph.Net.Gallery;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Particles;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;
using OsgColor = OpenSceneGraph.Net.State.Color;

namespace OpenSceneGraph.Net.Simulator;

public enum VehicleKind { Aircraft, Quadcopter, Ground, Ship }

public enum TerrainKind { Mountains, Desert, Highlands, Archipelago }

public enum Precipitation { None, Rain, Snow }

public enum CameraMode { Chase, Cockpit }

/// <summary>Scenario chosen in the UI. Terrain and vehicle changes rebuild the world; the rest is applied live.</summary>
public sealed class SimSettings
{
    public VehicleKind Vehicle { get; set; } = VehicleKind.Aircraft;
    public TerrainKind Terrain { get; set; } = TerrainKind.Mountains;
    public int Seed { get; set; } = 77;

    /// <summary>Local solar time, 0..24 h.</summary>
    public float Hour { get; set; } = 9.5f;

    /// <summary>Fog end distance in metres.</summary>
    public float Visibility { get; set; } = 5000;

    public Precipitation Precipitation { get; set; } = Precipitation.None;
}

/// <summary>
/// The simulated world: procedural terrain, the own vehicle and its dynamics model, mission waypoints, weather
/// (sun position, sky colours, fog, rain/snow particles), cameras and optional DIS output. One instance per scenario;
/// it owns its <see cref="Viewer"/>.
/// </summary>
public sealed class SimWorld : IDisposable
{
    /// <summary>Geodetic origin of the local ENU frame (Jakarta Bay), used for DIS positions.</summary>
    public static readonly GeoPoint Origin = new(-6.05, 106.85, 0);

    private readonly Group _root = new() { Name = "SimRoot" };
    private readonly Light _sun = Light.Sun(new Vector3(-0.4f, 0.5f, -0.7f), 1.1f, shadows: true);
    private readonly PositionAttitudeTransform _vehicleNode = new() { Name = "OwnVehicle" };
    private readonly PositionAttitudeTransform _modelNode = new();
    private readonly Group _beacons = new() { Name = "Waypoints" };
    private readonly List<(PositionAttitudeTransform Node, StateSet Column, StateSet Ring)> _beaconNodes = [];
    private readonly KeyState _keys = new();
    private readonly ChaseCamera _chase;
    private readonly CockpitCamera _cockpit;
    private PositionAttitudeTransform? _turret;
    private ParticleSystem? _precip;
    public int PrecipitationParticles => _precip?.AliveCount ?? 0;
    private ParticleSystem? _wake;
    private DisNetwork? _dis;
    private double _nextPdu;
    private float _throttle;
    private float _lastZ;
    private float _agroundTimer;
    private float _boundaryTimer;
    private Vector3 _spawn;

    public SimWorld(SimSettings settings)
    {
        Settings = settings;
        if (settings.Vehicle == VehicleKind.Ship) settings.Terrain = TerrainKind.Archipelago;
        Viewer = new Viewer();
        Viewer.Camera.FarPlane = 40000;
        Viewer.EventHandlers.Add(_keys);

        (Field, Ramp) = BuildField(settings.Terrain, settings.Seed);
        (HeightMin, HeightMax) = Field.Range();
        HasWater = settings.Terrain == TerrainKind.Archipelago;

        _root.AddChild(new LightSource(_sun));
        _root.AddChild(TerrainBuilder.Build(Field, tilesPerSide: 8, levels: 3, ramp: Ramp, lodRangeFactor: 2.2f));
        if (HasWater) _root.AddChild(Water());
        AddScenery();

        _modelNode.AddChild(CreateModel(settings.Vehicle, out _turret));
        _vehicleNode.AddChild(_modelNode);
        _root.AddChild(_vehicleNode);
        _root.AddChild(_beacons);
        if (settings.Vehicle == VehicleKind.Ground)
        {
            _wake = ParticleEffects.Dust(Vector3.Zero, 1.1f);
            _root.AddChild(_wake);
        }
        else if (settings.Vehicle == VehicleKind.Ship)
        {
            _wake = ParticleEffects.Dust(Vector3.Zero, 2.5f);
            _wake.ColorStart = new Vector4(1, 1, 1, 0.65f);
            _wake.ColorMid = new Vector4(0.9f, 0.95f, 1, 0.35f);
            _wake.ColorEnd = new Vector4(1, 1, 1, 0);
            _wake.Emitter.Rate = 70;
            _root.AddChild(_wake);
        }

        Vehicle = Spawn();
        var (dist, height) = settings.Vehicle switch
        {
            VehicleKind.Aircraft => (42f, 11f),
            VehicleKind.Quadcopter => (11f, 4f),
            VehicleKind.Ground => (19f, 6.5f),
            _ => (115f, 34f),
        };
        _chase = new ChaseCamera(() => _vehicleNode.Position, () => Vehicle.Heading, dist, height);
        _cockpit = new CockpitCamera(() => Vehicle, CockpitOffset(settings.Vehicle), settings.Vehicle == VehicleKind.Quadcopter ? -14 : 0);
        CameraMode = CameraMode.Chase;

        BuildMission();
        ApplyEnvironment();
        Viewer.SceneData = _root;
        // keep the chase camera's authored pose instead of the automatic "home" on the first frame
        Viewer.CameraManipulator = null;
        Viewer.Frame();
        Viewer.CameraManipulator = _chase;
        Viewer.Updated += (_, fs) => Update(fs);
    }

    public SimSettings Settings { get; }
    public Viewer Viewer { get; }
    public Node Scene => _root;
    public HeightField Field { get; }
    public ColorRamp Ramp { get; }
    public float HeightMin { get; }
    public float HeightMax { get; }
    public bool HasWater { get; }
    public Vehicle Vehicle { get; private set; }

    public List<Vector3> Waypoints { get; } = [];
    public int ActiveWaypoint { get; private set; }
    public bool MissionComplete => ActiveWaypoint >= Waypoints.Count;
    public double MissionTime { get; private set; }

    public bool Crashed => Vehicle is Aircraft { Crashed: true };
    public bool Aground => _agroundTimer > 0;
    public bool AtBoundary => _boundaryTimer > 0;
    public float Throttle => Settings.Vehicle switch
    {
        VehicleKind.Quadcopter => Vehicle.Controls.Throttle,
        VehicleKind.Ground => Vehicle.Controls.Throttle,
        _ => _throttle,
    };
    public float VerticalSpeed { get; private set; }
    public float GroundElevation => Field.Sample(Vehicle.Position.X, Vehicle.Position.Y) is { } g ? MathF.Max(g, HasWater ? 0 : float.MinValue) : 0;
    public float Agl => Vehicle.Position.Z - GroundElevation;

    public CameraMode CameraMode
    {
        get => field;
        set
        {
            field = value;
            Viewer.CameraManipulator = value == CameraMode.Chase ? _chase : _cockpit;
            // hide the airframe from inside the cockpit (aircraft, quadcopter); hull and turret stay visible
            bool hide = value == CameraMode.Cockpit && Settings.Vehicle is VehicleKind.Aircraft or VehicleKind.Quadcopter;
            _modelNode.NodeMask = hide ? 0u : 0xFFFFFFFFu;
        }
    }

    public bool Paused
    {
        get => Viewer.Paused;
        set => Viewer.Paused = value;
    }

    // ================================================================== DIS
    public bool DisEnabled => _dis is not null;
    public long PdusSent => _dis?.Sent ?? 0;
    public string? DisError { get; private set; }

    public void SetDis(bool enabled)
    {
        if (enabled == DisEnabled) return;
        if (!enabled)
        {
            _dis!.Dispose();
            _dis = null;
            return;
        }
        try
        {
            DisError = null;
            _dis = new DisNetwork(3000, listen: false);
        }
        catch (Exception ex)
        {
            DisError = ex.Message;
            _dis = null;
        }
    }

    private async Task SendPduAsync(DisNetwork net, EntityStatePdu pdu)
    {
        try { await net.SendAsync(pdu); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException) { DisError = ex.Message; }
    }

    private EntityType DisType => Settings.Vehicle switch
    {
        VehicleKind.Aircraft => EntityType.FighterAircraft(),
        VehicleKind.Quadcopter => EntityType.Uav(),
        VehicleKind.Ground => EntityType.Tank(),
        _ => EntityType.Ship(),
    };

    // ================================================================== frame update
    private void Update(FrameStamp fs)
    {
        float dt = (float)fs.DeltaTime;
        if (_dis is { } net && fs.ReferenceTime >= _nextPdu)
        {
            _nextPdu = fs.ReferenceTime + 0.1; // 10 Hz, heartbeat continues while paused
            var pdu = Vehicle.ToPdu(new EntityId(1, 3001, 1), DisType, Origin, "TRAINEE-1");
            _ = SendPduAsync(net, pdu);
        }
        FollowWeather();
        if (dt <= 0) return;

        dt = MathF.Min(dt, 0.1f);
        ReadControls(dt);
        var before = Vehicle.Position;
        Vehicle.Step(dt);
        Constrain(before, dt);
        Vehicle.ApplyTo(_vehicleNode);

        float z = Vehicle.Position.Z;
        VerticalSpeed = float.Lerp(VerticalSpeed, (z - _lastZ) / dt, MathF.Min(1, dt * 4));
        _lastZ = z;
        _agroundTimer = MathF.Max(0, _agroundTimer - dt);
        _boundaryTimer = MathF.Max(0, _boundaryTimer - dt);

        if (_wake is not null)
        {
            _wake.Emitter.Position = Vehicle.Position - Vehicle.Forward * (Settings.Vehicle == VehicleKind.Ship ? 22 : 3);
            _wake.Emitter.Rate = MathF.Min(Settings.Vehicle == VehicleKind.Ship ? 90 : 80, Vehicle.Speed * 6);
        }

        if (!MissionComplete)
        {
            MissionTime += dt;
            var wp = Waypoints[ActiveWaypoint];
            var d = wp - Vehicle.Position;
            float horizontal = new Vector2(d.X, d.Y).Length();
            bool vertical = Settings.Vehicle is VehicleKind.Ground or VehicleKind.Ship || MathF.Abs(d.Z) < CaptureRadius * 1.6f;
            if (horizontal < CaptureRadius && vertical)
            {
                ActiveWaypoint++;
                RefreshBeacons();
            }
        }
    }

    private void ReadControls(float dt)
    {
        var k = _keys;
        float Axis(string plus1, string plus2, string minus1, string minus2) =>
            (k.Any(plus1, plus2) ? 1 : 0) - (k.Any(minus1, minus2) ? 1 : 0);
        ref var c = ref Vehicle.Controls;
        switch (Settings.Vehicle)
        {
            case VehicleKind.Aircraft:
                _throttle = System.Math.Clamp(_throttle + Axis("w", "w", "s", "s") * 0.45f * dt, 0, 1);
                c.Throttle = _throttle;
                c.Roll = Axis("d", "ArrowRight", "a", "ArrowLeft");
                c.Pitch = Axis("ArrowUp", "i", "ArrowDown", "k");
                c.Yaw = Axis("e", "e", "q", "q");
                break;
            case VehicleKind.Quadcopter:
                c.Pitch = Axis("w", "w", "s", "s");
                c.Roll = Axis("d", "d", "a", "a");
                c.Yaw = Axis("ArrowRight", "e", "ArrowLeft", "q");
                c.Throttle = 0.5f + 0.5f * Axis("ArrowUp", "ArrowUp", "ArrowDown", "ArrowDown");
                break;
            case VehicleKind.Ground:
                c.Throttle = k.Any("w", "ArrowUp") ? 1 : 0;
                c.Brake = k.Any("s", "ArrowDown") ? 1 : 0;
                c.Yaw = Axis("d", "ArrowRight", "a", "ArrowLeft");
                if (_turret is not null)
                {
                    var g = (GroundVehicle)Vehicle;
                    g.TurretHeading += Axis("e", "e", "q", "q") * 40 * dt;
                    _turret.Attitude = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -g.TurretHeading * MathF.PI / 180);
                }
                break;
            case VehicleKind.Ship:
                _throttle = System.Math.Clamp(_throttle + Axis("w", "ArrowUp", "s", "ArrowDown") * 0.3f * dt, -0.3f, 1);
                c.Throttle = _throttle;
                c.Yaw = Axis("d", "ArrowRight", "a", "ArrowLeft");
                break;
        }
    }

    /// <summary>Keeps the vehicle inside the training area and on a valid surface.</summary>
    private void Constrain(Vector3 before, float dt)
    {
        var v = Vehicle;
        float half = Field.Width / 2, inset = Field.Width * 0.06f;
        var p = v.Position;
        bool outside = MathF.Abs(p.X) > half - inset || MathF.Abs(p.Y) > half - inset;
        if (outside)
        {
            _boundaryTimer = 1.5f;
            p.X = System.Math.Clamp(p.X, -half + inset, half - inset);
            p.Y = System.Math.Clamp(p.Y, -half + inset, half - inset);
            if (v is Aircraft or Quadcopter)
            {
                // instructor override: turn back towards the centre of the area
                float toCentre = MathF.Atan2(-p.X, -p.Y) * 180 / MathF.PI;
                float delta = ((toCentre - v.Heading) % 360 + 540) % 360 - 180;
                v.Heading = (v.Heading + System.Math.Clamp(delta, -60 * dt, 60 * dt) + 360) % 360;
            }
            v.Position = p;
        }

        float ground = Field.Sample(p.X, p.Y) ?? 0;
        switch (v)
        {
            case Ship when ground > -2.5f:
                v.Position = before with { Z = v.Position.Z };
                _throttle = MathF.Min(_throttle, 0);
                v.Controls.Throttle = _throttle;
                _agroundTimer = 2;
                break;
            case GroundVehicle when HasWater && ground < 0.6f:
                v.Position = before;
                _agroundTimer = 2;
                break;
            case Quadcopter q when v.Position.Z < ground + 0.4f:
                v.Position = v.Position with { Z = ground + 0.4f };
                q.TargetAltitude = MathF.Max(q.TargetAltitude, ground + 0.4f);
                break;
            case Aircraft when HasWater && v.Position.Z < 1.5f:
                v.Position = v.Position with { Z = 1.5f };
                break;
        }
    }

    // ================================================================== spawn & mission
    public float CaptureRadius => Settings.Vehicle switch
    {
        VehicleKind.Aircraft => 140,
        VehicleKind.Quadcopter => 10,
        VehicleKind.Ground => 14,
        _ => 70,
    };

    /// <summary>Puts the vehicle back at the start and restarts the mission.</summary>
    public void Reset()
    {
        Vehicle = Spawn();
        Vehicle.ApplyTo(_vehicleNode);
        ActiveWaypoint = 0;
        MissionTime = 0;
        _agroundTimer = _boundaryTimer = 0;
        RefreshBeacons();
        _chase.Home(default);
    }

    private Vehicle Spawn()
    {
        Func<float, float, float?> gh = Field.Sample;
        float half = Field.Width / 2;
        _throttle = 0;
        Vehicle v;
        switch (Settings.Vehicle)
        {
            case VehicleKind.Aircraft:
            {
                var start = new Vector2(0, -half * 0.55f);
                _spawn = new Vector3(start, HeightMax + 220);
                _throttle = 0.62f;
                v = new Aircraft { Position = _spawn, Heading = 0, GroundHeight = HasWater ? (x, y) => MathF.Max(gh(x, y) ?? 0, 0) : gh };
                break;
            }
            case VehicleKind.Quadcopter:
            {
                var s = FindSpot(new Vector2(0, -half * 0.25f), h => !HasWater || h > 4);
                float g = gh(s.X, s.Y) ?? 0;
                _spawn = new Vector3(s, g + 22);
                v = new Quadcopter { Position = _spawn, Heading = 0, GroundHeight = HasWater ? (x, y) => MathF.Max(gh(x, y) ?? 0, 0) : gh, TargetAltitude = g + 22 };
                break;
            }
            case VehicleKind.Ground:
            {
                var s = FindSpot(new Vector2(0, -half * 0.25f), h => (!HasWater || h > 4));
                _spawn = new Vector3(s, gh(s.X, s.Y) ?? 0);
                v = new GroundVehicle { Position = _spawn, Heading = 0, GroundHeight = gh, MaxSpeed = 17 };
                break;
            }
            default:
            {
                var s = FindSpot(new Vector2(0, -half * 0.4f), h => h < -12);
                _spawn = new Vector3(s, 0);
                v = new Ship { Position = _spawn, Heading = 0, SeaState = 3 };
                break;
            }
        }
        _lastZ = v.Position.Z;
        VerticalSpeed = 0;
        return v;
    }

    /// <summary>Spirals outwards from <paramref name="start"/> until <paramref name="ok"/> accepts the terrain height.</summary>
    private Vector2 FindSpot(Vector2 start, Func<float, bool> ok)
    {
        float half = Field.Width / 2 * 0.88f;
        for (float r = 0; r < Field.Width; r += 30)
            for (int i = 0; i < (r == 0 ? 1 : 32); i++)
            {
                float a = i / 32f * MathF.Tau;
                var p = start + new Vector2(MathF.Sin(a), MathF.Cos(a)) * r;
                if (MathF.Abs(p.X) > half || MathF.Abs(p.Y) > half) continue;
                if (Field.Sample(p.X, p.Y) is { } h && ok(h) && Flat(p)) return p;
            }
        return start;
    }

    private bool Flat(Vector2 p)
    {
        if (Settings.Vehicle != VehicleKind.Ground) return true;
        float c = Field.Sample(p.X, p.Y) ?? 0;
        float e = Field.Sample(p.X + 6, p.Y) ?? c, n = Field.Sample(p.X, p.Y + 6) ?? c;
        return MathF.Abs(e - c) < 2.2f && MathF.Abs(n - c) < 2.2f;
    }

    private void BuildMission()
    {
        Waypoints.Clear();
        float radius = Settings.Vehicle switch
        {
            VehicleKind.Aircraft => Field.Width * 0.3f,
            VehicleKind.Quadcopter => 170,
            VehicleKind.Ground => 230,
            _ => Field.Width * 0.18f,
        };
        var centre = new Vector2(_spawn.X, _spawn.Y + radius);
        var rng = new Random(Settings.Seed + (int)Settings.Vehicle * 31);
        for (int i = 0; i < 5; i++)
        {
            float a = MathF.PI + (i + 1) * MathF.Tau / 6f + (rng.NextSingle() - 0.5f) * 0.3f;
            var p = centre + new Vector2(MathF.Sin(a), MathF.Cos(a)) * radius;
            Func<float, bool> ok = Settings.Vehicle switch
            {
                VehicleKind.Ship => h => h < -12,
                VehicleKind.Ground => h => !HasWater || h > 4,
                _ => _ => true,
            };
            p = FindSpot(p, ok);
            float g = Field.Sample(p.X, p.Y) ?? 0;
            if (HasWater) g = MathF.Max(g, 0);
            float z = Settings.Vehicle switch
            {
                VehicleKind.Aircraft => MathF.Max(g + 240, _spawn.Z - 120),
                VehicleKind.Quadcopter => g + 18,
                VehicleKind.Ship => 0,
                _ => g,
            };
            Waypoints.Add(new Vector3(p, z));
        }

        // beacons: a column from the ground up to the gate, a ring at the capture radius and a route line
        bool air = Settings.Vehicle is VehicleKind.Aircraft or VehicleKind.Quadcopter;
        for (int i = 0; i < Waypoints.Count; i++)
        {
            var wp = Waypoints[i];
            float ground = MathF.Max(Field.Sample(wp.X, wp.Y) ?? 0, HasWater ? 0 : float.MinValue);
            float columnHeight = air ? wp.Z - ground : Settings.Vehicle == VehicleKind.Ship ? 60 : 24;
            var node = new PositionAttitudeTransform { Position = new Vector3(wp.X, wp.Y, ground), Name = $"wp{i + 1}" };
            var column = Shapes.Cylinder(CaptureRadius * (air ? 0.12f : 0.35f), columnHeight, 20);
            var cs = BeaconState(column);
            node.AddChild(new PositionAttitudeTransform { Position = new Vector3(0, 0, columnHeight / 2) }.AddChild(column));
            var ring = Models.Ring(CaptureRadius, Vector4.One, 64);
            var rs = BeaconState(ring);
            node.AddChild(new PositionAttitudeTransform { Position = new Vector3(0, 0, air ? wp.Z - ground : 0.6f) }.AddChild(ring));
            _beacons.AddChild(node);
            _beaconNodes.Add((node, cs, rs));
        }
        var route = new List<Vector3> { _spawn };
        route.AddRange(Waypoints.Select(w => w with { Z = w.Z + (air ? 0 : 1.5f) }));
        var line = Shapes.LineStrip(route, new Vector4(0.55f, 0.97f, 0.77f, 0.35f));
        BeaconState(line).Material = Material.FromColor(0.55f, 0.97f, 0.77f, 0.35f);
        _beacons.AddChild(line);
        RefreshBeacons();
    }

    private static StateSet BeaconState(Geometry g)
    {
        var ss = g.GetOrCreateStateSet();
        ss.MakeTransparent();
        ss.Lighting = false;
        ss.CullFace = CullFaceMode.None;
        ss.CastShadows = false;
        ss.Fog = false;
        return ss;
    }

    private void RefreshBeacons()
    {
        for (int i = 0; i < _beaconNodes.Count; i++)
        {
            var (node, column, ring) = _beaconNodes[i];
            node.NodeMask = i < ActiveWaypoint ? 0u : 0xFFFFFFFFu;
            bool active = i == ActiveWaypoint;
            var c = active ? new Vector4(1f, 0.71f, 0.28f, 1) : new Vector4(0.36f, 0.88f, 0.9f, 1);
            column.Material = Material.FromColor(c with { W = active ? 0.42f : 0.16f });
            ring.Material = Material.FromColor(c with { W = active ? 0.95f : 0.45f });
        }
    }

    // ================================================================== environment
    /// <summary>Applies time of day, visibility and precipitation from <see cref="Settings"/>.</summary>
    public void ApplyEnvironment()
    {
        var s = Settings;
        var env = Viewer.Environment;
        // sun path: rises in the east at 06:00, culminates north of the zenith (southern hemisphere), sets at 18:00
        float dayAngle = (s.Hour - 6) / 12 * MathF.PI;
        float sinEl = MathF.Sin(dayAngle) * 0.93f;
        float el = MathF.Asin(System.Math.Clamp(sinEl, -1, 1));
        float az = MathUtilDeg(90 - (s.Hour - 6) / 12 * 180); // east -> (north) -> west
        var toSun = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Cos(az) * MathF.Cos(el) * 0.6f, MathF.Sin(el));

        float day = SmoothStep(-0.02f, 0.45f, sinEl);
        float night = SmoothStep(0.06f, -0.18f, sinEl);
        float dusk = MathF.Max(0, 1 - MathF.Abs(sinEl - 0.05f) / 0.28f) * (1 - night * 0.6f);

        Vector3 Mix(string a, string b, float t) => Vector3.Lerp(Rgb(a), Rgb(b), t);
        var top = Mix("#3a4f86", "#2a6fb0", day);
        var horizon = Mix("#f2a36a", "#c2d8ea", day);
        top = Vector3.Lerp(top, Rgb("#03060d"), night);
        horizon = Vector3.Lerp(horizon, Rgb("#0d1424"), night);
        horizon = Vector3.Lerp(horizon, Rgb("#f08a5d"), dusk * 0.35f);
        if (s.Precipitation != Precipitation.None)
        {
            var overcast = s.Precipitation == Precipitation.Snow ? Rgb("#c9d1db") : Rgb("#7c8794");
            float k = 0.65f * (1 - night * 0.7f);
            top = Vector3.Lerp(top, overcast * 0.85f, k);
            horizon = Vector3.Lerp(horizon, overcast, k);
        }

        if (sinEl > -0.05f)
        {
            _sun.Direction = Vector3.Normalize(-new Vector3(toSun.X, toSun.Y, MathF.Max(toSun.Z, 0.08f)));
            _sun.Color = Vector3.Lerp(Rgb("#ffb27a"), Rgb("#fff4e2"), day);
            _sun.Intensity = 0.25f + 0.9f * day;
        }
        else
        {
            // moonlight
            _sun.Direction = Vector3.Normalize(new Vector3(0.3f, -0.4f, -0.85f));
            _sun.Color = Rgb("#9fb4ff");
            _sun.Intensity = 0.22f;
        }
        if (s.Precipitation != Precipitation.None) _sun.Intensity *= 0.6f;

        env.SkyTopColor = new Vector4(top, 1);
        Viewer.Camera.ClearColor = new Vector4(horizon, 1);
        env.AmbientLight = Vector3.Lerp(new Vector3(0.05f, 0.06f, 0.1f), new Vector3(0.24f, 0.26f, 0.29f), MathF.Max(day, 0.15f * (1 - night)));
        env.HeadLight = true;
        env.HeadLightIntensity = float.Lerp(0.3f, 0.32f, day);
        env.ReflectionColor = Vector3.Lerp(horizon, top, 0.4f);
        float vis = s.Visibility;
        env.Fog = new FogSettings(true, horizon, vis * 0.12f, vis);
        bool shadows = Settings.Vehicle is VehicleKind.Ground or VehicleKind.Quadcopter && sinEl > 0.08f;
        env.Shadows.Enabled = shadows;
        _sun.CastShadows = shadows;
        env.Shadows.Radius = Settings.Vehicle == VehicleKind.Ground ? 55 : 35;
        Viewer.Camera.FarPlane = MathF.Max(vis * 1.2f, 3000);

        // precipitation particles follow the camera
        var kind = s.Precipitation;
        if (_precip is not null && (_precip.Name != kind.ToString() || kind == Precipitation.None))
        {
            _root.RemoveChild(_precip);
            _precip = null;
        }
        if (_precip is null && kind != Precipitation.None)
        {
            _precip = kind == Precipitation.Rain
                ? ParticleEffects.Rain(Vector3.Zero, area: 110, height: 40, intensity: 0.7f)
                : ParticleEffects.Snow(Vector3.Zero, area: 110, height: 35, intensity: 1.0f);
            _precip.Name = kind.ToString();
            // real 6 cm drops are sub-pixel at chase distance: draw them as larger soft streak-like sprites
            if (kind == Precipitation.Rain)
            {
                _precip.SizeStart = _precip.SizeEnd = 0.2f;
                _precip.ColorStart = _precip.ColorMid = new Vector4(0.78f, 0.84f, 0.95f, 0.75f);
                _precip.ColorEnd = new Vector4(0.78f, 0.84f, 0.95f, 0.6f);
            }
            else _precip.SizeStart = _precip.SizeEnd = 0.32f;
            _precip.GetOrCreateStateSet().CastShadows = false;
            _root.AddChild(_precip);
            FollowWeather();
        }
    }

    private void FollowWeather()
    {
        if (_precip is null) return;
        var eye = Viewer.Camera.Eye;
        var vel = Vehicle.Velocity;
        // lead the emitter along the direction of travel so fast vehicles fly into the weather
        var centre = eye + new Vector3(vel.X, vel.Y, 0) * 1.2f;
        _precip.Emitter.Position = new Vector3(centre.X, centre.Y, eye.Z + (_precip.Name == "Rain" ? 30 : 25));
    }

    // ================================================================== scene construction
    private static (HeightField, ColorRamp) BuildField(TerrainKind kind, int seed)
    {
        const int size = 257;
        HeightField hf;
        ColorRamp ramp;
        switch (kind)
        {
            case TerrainKind.Desert:
                hf = TerrainGenerator.Fractal(size, 16, 110, seed, roughness: 0.42f, baseFrequency: 2.0f);
                ramp = ColorRamp.Desert();
                break;
            case TerrainKind.Highlands:
                hf = TerrainGenerator.Fractal(size, 16, 190, seed, roughness: 0.5f, baseFrequency: 3.0f);
                ramp = ColorRamp.Tactical();
                break;
            case TerrainKind.Archipelago:
                hf = TerrainGenerator.Fractal(size, 16, 330, seed, roughness: 0.5f, baseFrequency: 3.2f);
                for (int i = 0; i < hf.Heights.Length; i++) hf.Heights[i] -= 70;
                ramp = new ColorRamp()
                    .Add(0f, OsgColor.FromHex("#0b2a4a")).Add(0.22f, OsgColor.FromHex("#1f5f8b"))
                    .Add(0.25f, OsgColor.FromHex("#e6d2a0")).Add(0.29f, OsgColor.FromHex("#6a994e"))
                    .Add(0.55f, OsgColor.FromHex("#386641")).Add(0.8f, OsgColor.FromHex("#7f7f6e")).Add(1f, OsgColor.FromHex("#f4f4f4"));
                break;
            default:
                hf = TerrainGenerator.Fractal(size, 16, 460, seed, roughness: 0.5f);
                ramp = ColorRamp.Natural();
                break;
        }
        return (hf, ramp);
    }

    private Node Water()
    {
        var water = Shapes.Plane(Field.Width * 3, Field.Depth * 3, 1, 1, OsgColor.FromHex("#1c4f7c"));
        var ss = water.StateSet!;
        ss.Material!.Reflectivity = 0.5f;
        ss.Material.Specular = new Vector3(0.8f);
        ss.Material.Shininess = 80;
        ss.CastShadows = false;
        return water;
    }

    private void AddScenery()
    {
        if (Settings.Terrain == TerrainKind.Desert)
        {
            var rng = new Random(Settings.Seed + 5);
            for (int i = 0; i < 90; i++)
            {
                float x = (rng.NextSingle() - 0.5f) * Field.Width * 0.9f, y = (rng.NextSingle() - 0.5f) * Field.Depth * 0.9f;
                var rock = Shapes.Sphere(2 + rng.NextSingle() * 5, 8, 5, OsgColor.FromHex("#8d6e55"));
                var p = new PositionAttitudeTransform { Position = new Vector3(x, y, (Field.Sample(x, y) ?? 0) - 1), Scale = new Vector3(1, 1.3f, 0.7f) };
                p.AddChild(rock);
                _root.AddChild(p);
            }
            return;
        }
        // forests on gentle, mid-altitude slopes
        var r = new Random(Settings.Seed + 11);
        int placed = 0;
        for (int tries = 0; tries < 4000 && placed < 260; tries++)
        {
            float x = (r.NextSingle() - 0.5f) * Field.Width * 0.92f, y = (r.NextSingle() - 0.5f) * Field.Depth * 0.92f;
            float h = Field.Sample(x, y) ?? 0;
            float t = (h - HeightMin) / MathF.Max(1, HeightMax - HeightMin);
            if (HasWater ? h < 6 || t > 0.6f : t < 0.08f || t > 0.5f) continue;
            float slope = MathF.Abs((Field.Sample(x + 8, y) ?? h) - h) + MathF.Abs((Field.Sample(x, y + 8) ?? h) - h);
            if (slope > 7) continue;
            var tree = new PositionAttitudeTransform { Position = new Vector3(x, y, h - 0.3f) };
            tree.AddChild(Models.Tree(9 + r.NextSingle() * 8, r));
            _root.AddChild(tree);
            placed++;
        }
    }

    private static Node CreateModel(VehicleKind kind, out PositionAttitudeTransform? turret)
    {
        turret = null;
        switch (kind)
        {
            case VehicleKind.Aircraft:
                return Assets.Jet();
            case VehicleKind.Quadcopter:
            {
                var p = new PositionAttitudeTransform { Scale = new Vector3(1.6f), Position = new Vector3(0, 0, 0.1f) };
                p.AddChild(Models.Quadcopter());
                return p;
            }
            case VehicleKind.Ground:
            {
                // procedural tank so the turret can be traversed independently (Q / E)
                var tank = Models.Tank();
                turret = FindNodesVisitor.FindAll<PositionAttitudeTransform>(tank).FirstOrDefault(n => n.Name == "turret");
                return tank;
            }
            default:
                return Models.Ship();
        }
    }

    private static Vector3 CockpitOffset(VehicleKind kind) => kind switch
    {
        VehicleKind.Aircraft => new Vector3(0, 2.5f, 1.2f),
        VehicleKind.Quadcopter => new Vector3(0, 0.6f, -0.25f),
        VehicleKind.Ground => new Vector3(0, -0.6f, 3.4f),
        _ => new Vector3(0, -3.5f, 13.5f),
    };

    private static Vector3 Rgb(string hex) { var c = OsgColor.FromHex(hex); return new Vector3(c.X, c.Y, c.Z); }
    private static float SmoothStep(float a, float b, float x) { float t = System.Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    private static float MathUtilDeg(float deg) => deg * MathF.PI / 180;

    public void Dispose()
    {
        _dis?.Dispose();
        _dis = null;
        Viewer.Dispose();
    }
}

/// <summary>First-person camera fixed to the vehicle (pilot / driver / bridge eye point).</summary>
public sealed class CockpitCamera(Func<Vehicle> vehicle, Vector3 offset, float tiltDegrees) : ICameraManipulator
{
    public string Name => "Cockpit";
    public Vector3 Center => vehicle().Position;
    public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;

    public void Home(Math.BoundingSphere bound) { }
    public bool Handle(in InputEvent e, Viewer viewer) => false;

    public void Update(double deltaTime)
    {
        var v = vehicle();
        var q = v.Attitude;
        if (tiltDegrees != 0) q *= Quaternion.CreateFromAxisAngle(Vector3.UnitX, tiltDegrees * MathF.PI / 180);
        var eye = v.Position + Vector3.Transform(offset, v.Attitude);
        var fwd = Vector3.Transform(Vector3.UnitY, q);
        var up = Vector3.Transform(Vector3.UnitZ, q);
        ViewMatrix = Matrix4x4.CreateLookAt(eye, eye + fwd, up);
    }
}
