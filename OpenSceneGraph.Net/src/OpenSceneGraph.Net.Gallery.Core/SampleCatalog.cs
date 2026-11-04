using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Particles;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Science;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;

namespace OpenSceneGraph.Net.Gallery;

/// <summary>All gallery samples. Each sample is self-contained and builds its scene from the core library only.</summary>
public static partial class SampleCatalog
{
    public static IReadOnlyList<GallerySample> All { get; } = Build();

    public static GallerySample? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    public static Text CategoryName(SampleCategory c) => c switch
    {
        SampleCategory.Military => new("Defense & Military", "Pertahanan & Militer"),
        SampleCategory.Academic => new("Academic", "Akademik"),
        SampleCategory.Science => new("Science & Research", "Sains & Riset"),
        SampleCategory.Medical => new("Medical", "Medis"),
        SampleCategory.Office => new("Office & Business", "Perkantoran & Bisnis"),
        SampleCategory.Entertainment => new("Entertainment & Games", "Hiburan & Game"),
        SampleCategory.Simulator => new("Simulators", "Simulator"),
        SampleCategory.Gis => new("GIS & Geospatial", "GIS & Geospasial"),
        _ => new("Fundamentals", "Dasar-dasar"),
    };

    private static List<GallerySample> Build() =>
    [
        Battlefield(), UavGroundStation(), AirDefense(),
        SolarSystem(), Molecule(), MathSurface(),
        CfdFlow(), Seismic(), SatelliteConstellation(),
        CtScan(),
        BarChart(), DigitalTwin(),
        ParticleShowcase(), CreatureRig(), NightCity(),
        FlightSim(), MaritimeSim(), DrivingSim(),
        TerrainLod(), GeoGlobe(),
        SceneGraphBasics(), RenderToTexture(),
    ];

    // ---------------------------------------------------------------- helpers
    private static Group Ground(float size, Vector4 color)
    {
        var g = new Group();
        var plane = Shapes.Plane(size, size, 1, 1, color);
        plane.StateSet!.Material!.Specular = new Vector3(0.02f);
        g.AddChild(plane);
        return g;
    }

    private static PositionAttitudeTransform Place(Node n, Vector3 at, float headingDeg = 0, float scale = 1)
    {
        var p = new PositionAttitudeTransform { Position = at, Attitude = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -headingDeg * MathF.PI / 180), Scale = new Vector3(scale) };
        p.AddChild(n);
        return p;
    }

    private static void Sun(Group root, Vector3 dir, float intensity = 1.1f, bool shadows = true) =>
        root.AddChild(new LightSource(Light.Sun(dir, intensity, shadows)));

    private static void Orbit(Viewer v, float distance, float heading = -35, float pitch = 28, Vector3? center = null)
    {
        var tb = new TrackballManipulator { Distance = distance, Heading = heading, Pitch = pitch, Center = center ?? Vector3.Zero };
        v.CameraManipulator = tb;
    }

    // ================================================================ MILITARY
    private static GallerySample Battlefield() => new()
    {
        Id = "battlefield",
        Category = SampleCategory.Military,
        Accent = "#a3b18a",
        Title = new("Digital Battlefield", "Medan Tempur Digital"),
        Description = new("Armoured platoon manoeuvring over tactical terrain with artillery impacts, smoke screens and live DIS Entity State output.",
                          "Peleton lapis baja bermanuver di atas terrain taktis dengan ledakan artileri, tabir asap, dan keluaran DIS Entity State secara langsung."),
        Tags = ["terrain", "particles", "DIS", "animation path"],
        Features = ["TerrainBuilder", "AnimationPath", "ParticleSystem", "EntityStatePdu", "ShadowMap", "OBJ asset (Rodin + Blender)"],
        Code = """
            var hf = TerrainGenerator.Fractal(129, 4f, 45, seed: 12);
            root.AddChild(TerrainBuilder.Build(hf, ramp: ColorRamp.Tactical()));
            var tank = new PositionAttitudeTransform();
            tank.AddChild(Assets.Tank()); // Rodin-generated, Blender-cleaned model
            tank.AddUpdateCallback(new AnimationPathCallback(AnimationPath.ThroughWaypoints(route, 8)));
            root.AddChild(ParticleEffects.Explosion(new Vector3(40, 20, h)));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group { Name = "Battlefield" };
            var hf = TerrainGenerator.Fractal(129, 4f, 45, seed: 12, roughness: 0.45f);
            root.AddChild(TerrainBuilder.Build(hf, tilesPerSide: 4, levels: 3, ramp: ColorRamp.Tactical()));
            Sun(root, new Vector3(-0.5f, 0.4f, -0.8f));
            float H(float x, float y) => hf.Sample(x, y) ?? 0;

            var route = new List<Vector3>();
            for (int i = 0; i < 12; i++)
            {
                float a = i / 12f * MathF.Tau;
                float x = MathF.Cos(a) * 120, y = MathF.Sin(a * 2) * 70;
                route.Add(new Vector3(x, y, H(x, y) + 0.2f));
            }
            var tanks = new List<PositionAttitudeTransform>();
            for (int i = 0; i < 4; i++)
            {
                var tank = new PositionAttitudeTransform { Name = $"tank{i}" };
                tank.AddChild(Models.Tank());
                tank.AddUpdateCallback(new AnimationPathCallback(AnimationPath.ThroughWaypoints(route, 8)) { TimeOffset = i * 4 });
                // keep on the ground after the path moves it
                tank.OnUpdate((n, _) => { var p = (PositionAttitudeTransform)n; p.Position = p.Position with { Z = H(p.Position.X, p.Position.Y) + 0.1f }; });
                root.AddChild(tank);
                tanks.Add(tank);
            }

            var rng = new Random(3);
            for (int i = 0; i < 40; i++)
            {
                float x = rng.NextSingle() * 440 - 220, y = rng.NextSingle() * 440 - 220;
                root.AddChild(Place(Models.Tree(5 + rng.NextSingle() * 5, rng), new Vector3(x, y, H(x, y))));
            }

            var smoke = ParticleEffects.Smoke(new Vector3(-30, 40, H(-30, 40)), 3);
            root.AddChild(smoke);
            var fire = ParticleEffects.Fire(new Vector3(-30, 40, H(-30, 40)), 1.5f);
            root.AddChild(fire);
            var boom = ParticleEffects.Explosion(Vector3.Zero, 3, autoBurst: false);
            root.AddChild(boom);
            double nextImpact = 1;
            int sent = 0;
            v.Updated += (viewer, fs) =>
            {
                if (fs.SimulationTime > nextImpact)
                {
                    nextImpact = fs.SimulationTime + 2.5;
                    float x = rng.NextSingle() * 200 - 100, y = rng.NextSingle() * 200 - 100;
                    boom.Emitter.Position = new Vector3(x, y, H(x, y) + 1);
                    boom.Burst(700);
                }
                var origin = new GeoPoint(-6.9, 107.6, 700); // Bandung area
                var pdu = new EntityStatePdu
                {
                    Id = new EntityId(1, 1, 1),
                    Type = EntityType.Tank(),
                    Location = Ellipsoid.Wgs84.ToEcef(Ellipsoid.Wgs84.FromEnu(tanks[0].Position, origin)),
                    Marking = "ALPHA-1",
                };
                sent++;
                var geo = Ellipsoid.Wgs84.FromEcef(pdu.Location);
                ctx.Telemetry.Set("Lead tank", geo.ToString());
                ctx.Telemetry.Set("DIS PDUs", $"{sent:N0} × {EntityStatePdu.Size} B");
                ctx.Telemetry.Set("Particles", $"{smoke.AliveCount + fire.AliveCount + boom.AliveCount:N0}");
            };

            v.Environment.Shadows.Enabled = true;
            v.Environment.Shadows.Radius = 220;
            v.Environment.Fog = new FogSettings(true, new Vector3(0.62f, 0.66f, 0.62f), 250, 900);
            v.Environment.SkyTopColor = Color.FromHex("#6d7f8f");
            v.Camera.ClearColor = Color.FromHex("#b9c2bf");
            v.CameraManipulator = new TerrainManipulator { HeightAt = hf.Sample, Distance = 360, Pitch = 32, Heading = -20 };
            return root;
        },
    };

    private static GallerySample UavGroundStation() => new()
    {
        Id = "uav",
        Category = SampleCategory.Military,
        Accent = "#f4a261",
        Title = new("UAV Ground Station", "Stasiun Kendali UAV"),
        Description = new("A reconnaissance UAV flies a waypoint mission; its sensor footprint sweeps the terrain while the station displays telemetry.",
                          "UAV pengintai terbang mengikuti misi waypoint; jejak sensornya menyapu terrain sementara stasiun menampilkan telemetri."),
        Tags = ["UAV", "waypoints", "telemetry", "transparency"],
        Features = ["AnimationPath", "LineStrip", "Transparency", "Geo ENU"],
        Code = """
            var path = AnimationPath.ThroughWaypoints(waypoints, speed: 35);
            uav.AddUpdateCallback(new AnimationPathCallback(path));
            footprint.AddChild(Models.Beam(radius: 40, length: 120, color with { W = 0.18f }));
            root.AddChild(Shapes.LineStrip(waypoints, Color.Yellow));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var hf = TerrainGenerator.Fractal(129, 6f, 70, seed: 99);
            root.AddChild(TerrainBuilder.Build(hf, ramp: ColorRamp.Natural()));
            Sun(root, new Vector3(0.3f, 0.6f, -0.7f), 1.0f, false);
            var wps = new List<Vector3>();
            var rng = new Random(5);
            for (int i = 0; i < 7; i++)
            {
                float a = i / 7f * MathF.Tau;
                float r = 220 + rng.NextSingle() * 90;
                wps.Add(new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 180 + rng.NextSingle() * 40));
            }
            root.AddChild(Shapes.LineStrip([.. wps, wps[0]], Color.Yellow));
            foreach (var w in wps) root.AddChild(Place(Shapes.Sphere(4, 12, 8, Color.Yellow), w));

            var path = AnimationPath.ThroughWaypoints(wps, 45);
            var uav = new PositionAttitudeTransform { Name = "uav" };
            uav.AddChild(Place(Models.Jet(Color.FromHex("#d9dde3")), Vector3.Zero, scale: 1.4f));
            var cb = new AnimationPathCallback(path);
            uav.AddUpdateCallback(cb);
            root.AddChild(uav);

            var footprint = new PositionAttitudeTransform();
            footprint.AddChild(Place(Models.Beam(60, 170, new Vector4(0.3f, 0.9f, 1f, 0.16f)), new Vector3(0, 0, -85)));
            footprint.OnUpdate((n, _) => ((PositionAttitudeTransform)n).Position = uav.Position);
            root.AddChild(footprint);

            v.Updated += (_, fs) =>
            {
                var p = uav.Position;
                float ground = hf.Sample(p.X, p.Y) ?? 0;
                var geo = Ellipsoid.Wgs84.FromEnu(p, new GeoPoint(-7.25, 112.75, 0));
                ctx.Telemetry.Set("Position", geo.ToString());
                ctx.Telemetry.Set("AGL", $"{p.Z - ground:F0} m");
                ctx.Telemetry.Set("Ground speed", "45 m/s (87 kt)");
                ctx.Telemetry.Set("Mission time", TimeSpan.FromSeconds(fs.SimulationTime).ToString(@"mm\:ss"));
            };
            v.Environment.Fog = new FogSettings(true, new Vector3(0.7f, 0.78f, 0.86f), 500, 1600);
            v.Environment.SkyTopColor = Color.FromHex("#3d6a9e");
            v.Camera.ClearColor = Color.FromHex("#b7cde0");
            Orbit(v, 900, -30, 35);
            return root;
        },
    };

    private static GallerySample AirDefense() => new()
    {
        Id = "air-defense",
        Category = SampleCategory.Military,
        Accent = "#e63946",
        Title = new("Air Defense Radar", "Radar Pertahanan Udara"),
        Description = new("Rotating surveillance radar with range rings, a sweeping detection volume and hostile/friendly air tracks.",
                          "Radar pengawas berputar dengan cincin jarak, volume deteksi yang menyapu, dan jejak udara kawan/lawan."),
        Tags = ["radar", "tracks", "C2"],
        Features = ["SpinCallback", "Switch", "Transparency", "LineStrip"],
        Code = """
            var sweep = new PositionAttitudeTransform();
            sweep.AddChild(sector);                       // transparent wedge
            sweep.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 72));
            for (int r = 1; r <= 5; r++) root.AddChild(Models.Ring(r * 200, gridColor));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var ground = Shapes.Plane(2400, 2400, 1, 1, Color.FromHex("#0b1a14"));
            ground.StateSet!.Lighting = false;
            root.AddChild(ground);
            root.AddChild(Place(Shapes.GridLines(2400, 24, new Vector4(0.1f, 0.45f, 0.3f, 1)), new Vector3(0, 0, 0.3f)));
            for (int r = 1; r <= 5; r++) root.AddChild(Place(Models.Ring(r * 220, new Vector4(0.2f, 0.95f, 0.55f, 1)), new Vector3(0, 0, 0.6f)));
            root.AddChild(Place(Models.RadarSite(), Vector3.Zero, scale: 6));

            // sweep wedge
            var wedgePts = new List<Vector3> { Vector3.Zero };
            for (int i = 0; i <= 12; i++) { float a = i / 12f * 0.35f; wedgePts.Add(new Vector3(MathF.Sin(a) * 1100, MathF.Cos(a) * 1100, 0)); }
            var idx = new List<uint>();
            for (uint i = 1; i < wedgePts.Count - 1; i++) idx.AddRange([0, i, i + 1]);
            var wedge = new Geometry { Positions = wedgePts.ToArray(), Indices = idx.ToArray() };
            wedge.ComputeNormals();
            var wss = wedge.GetOrCreateStateSet();
            wss.Material = Material.FromColor(0.25f, 1f, 0.55f, 0.28f);
            wss.MakeTransparent(BlendMode.Additive);
            wss.Lighting = false;
            wss.CullFace = CullFaceMode.None;
            var sweep = new PositionAttitudeTransform { Position = new Vector3(0, 0, 1) };
            sweep.AddChild(wedge);
            sweep.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, -72));
            root.AddChild(sweep);

            var rng = new Random(8);
            int hostile = 0;
            for (int i = 0; i < 9; i++)
            {
                bool enemy = i % 3 == 0;
                if (enemy) hostile++;
                var color = enemy ? Color.Red : Color.FromHex("#4cc9f0");
                float alt = 120 + rng.NextSingle() * 200;
                var path = AnimationPath.Circle(new Vector3(rng.NextSingle() * 600 - 300, rng.NextSingle() * 600 - 300, 0), 200 + rng.NextSingle() * 400, 40 + rng.NextSingle() * 40, height: alt, clockwise: i % 2 == 0);
                var t = new PositionAttitudeTransform();
                t.AddChild(Place(Models.Jet(color), Vector3.Zero, scale: 3));
                t.AddUpdateCallback(new AnimationPathCallback(path) { TimeOffset = rng.NextDouble() * 30 });
                root.AddChild(t);
                root.AddChild(Shapes.LineStrip(Enumerable.Range(0, 65).Select(k => path.GetControlPoint(k / 64.0 * path.Period).Position).ToList(), color with { W = 0.5f }));
            }
            ctx.Telemetry.Set("Tracks", "9");
            ctx.Telemetry.Set("Hostile", hostile.ToString());
            ctx.Telemetry.Set("Sweep", "5 s / rev (12 rpm)");
            v.Environment.HeadLight = true;
            v.Environment.SkyTopColor = Color.FromHex("#02110b");
            v.Camera.ClearColor = Color.FromHex("#04180f");
            v.Environment.Post.Bloom = true;
            v.Environment.Post.BloomThreshold = 0.55f;
            Orbit(v, 1900, 20, 40);
            return root;
        },
    };

    // ================================================================ ACADEMIC
    private static GallerySample SolarSystem() => new()
    {
        Id = "solar-system",
        Category = SampleCategory.Academic,
        Accent = "#ffb703",
        Title = new("Solar System", "Tata Surya"),
        Description = new("Planets orbit an emissive sun on animation paths with true relative periods (scaled); a classic astronomy lesson.",
                          "Planet mengorbit matahari bercahaya mengikuti animation path dengan periode relatif (diskalakan); pelajaran astronomi klasik."),
        Tags = ["education", "astronomy", "hierarchy"],
        Features = ["Transform hierarchy", "AnimationPath", "Emission", "Bloom"],
        Code = """
            var orbit = new PositionAttitudeTransform();
            orbit.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 360 / periodSeconds));
            orbit.AddChild(Place(planet, new Vector3(distance, 0, 0)));
            sun.StateSet.Material = Material.Emissive(new Vector3(1, 0.8f, 0.3f));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var sun = Shapes.Sphere(6, 48, 24);
            sun.GetOrCreateStateSet().Material = Material.Emissive(new Vector3(1f, 0.75f, 0.25f) * 1.6f);
            sun.StateSet.CastShadows = false;
            root.AddChild(sun);
            root.AddChild(new LightSource(Light.Point(Vector3.Zero, new Vector3(1, 0.95f, 0.85f), 400, 1.6f)));
            (string Name, float Dist, float Size, float Period, string Color)[] planets =
            [
                ("Mercury", 11, 0.6f, 4.2f, "#9c8f84"), ("Venus", 15, 1.1f, 10.7f, "#d9b37a"), ("Earth", 20, 1.2f, 17.4f, "#3b82c4"),
                ("Mars", 25, 0.8f, 32.7f, "#c1440e"), ("Jupiter", 36, 3.2f, 60f, "#d2a679"), ("Saturn", 48, 2.7f, 90f, "#e3c88f"),
                ("Uranus", 58, 1.9f, 120f, "#9fd8e0"), ("Neptune", 67, 1.8f, 150f, "#3f5fd1"),
            ];
            foreach (var p in planets)
            {
                var orbit = new PositionAttitudeTransform { Name = p.Name + " orbit" };
                orbit.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 360 / p.Period));
                var body = new PositionAttitudeTransform { Position = new Vector3(p.Dist, 0, 0) };
                body.AddChild(Shapes.Sphere(p.Size, 32, 16, Color.FromHex(p.Color)));
                if (p.Name == "Saturn")
                {
                    var ring = Shapes.Torus(p.Size * 1.9f, p.Size * 0.35f, 64, 6, new Vector4(0.85f, 0.76f, 0.55f, 0.7f));
                    var rp = new PositionAttitudeTransform { Scale = new Vector3(1, 1, 0.08f), Attitude = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.45f) };
                    rp.AddChild(ring);
                    body.AddChild(rp);
                }
                if (p.Name == "Earth")
                {
                    var moonOrbit = new PositionAttitudeTransform();
                    moonOrbit.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 360 / 2.5f));
                    moonOrbit.AddChild(Place(Shapes.Sphere(0.35f, 16, 8, Color.Gray), new Vector3(2.2f, 0, 0)));
                    body.AddChild(moonOrbit);
                }
                orbit.AddChild(body);
                root.AddChild(orbit);
                var ringLine = Models.Ring(p.Dist, new Vector4(0.5f, 0.6f, 0.8f, 0.35f), 128);
                ringLine.StateSet!.MakeTransparent();
                root.AddChild(ringLine);
            }
            var rng = new Random(1);
            var stars = Enumerable.Range(0, 2500).Select(_ =>
            {
                var d = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
                return d * (400 + rng.NextSingle() * 200);
            }).ToArray();
            root.AddChild(Shapes.Points(stars, stars.Select(_ => new Vector4(1, 1, 1, 0.5f + rng.NextSingle() * 0.5f)).ToArray(), 2));
            ctx.Telemetry.Set("Bodies", "1 star, 8 planets, 1 moon");
            ctx.Telemetry.Set("Time scale", "1 Earth year ≈ 17 s");
            v.Environment.HeadLight = false;
            v.Environment.AmbientLight = new Vector3(0.06f);
            v.Environment.SkyTopColor = Color.FromHex("#030712");
            v.Camera.ClearColor = Color.FromHex("#000000");
            v.Environment.Post.Bloom = true;
            v.Environment.Post.Hdr = true;
            Orbit(v, 150, -20, 35);
            return root;
        },
    };

    private static GallerySample Molecule() => new()
    {
        Id = "molecule",
        Category = SampleCategory.Academic,
        Accent = "#8ecae6",
        Title = new("Molecule Viewer: Caffeine", "Penampil Molekul: Kafein"),
        Description = new("Ball-and-stick model of caffeine (C8H10N4O2) with CPK colours; bonds are oriented cylinders built from atom pairs.",
                          "Model bola-dan-batang kafein (C8H10N4O2) dengan warna CPK; ikatan berupa silinder berorientasi dari pasangan atom."),
        Tags = ["chemistry", "education"],
        Features = ["Shapes", "MatrixTransform", "Instancing"],
        Code = """
            foreach (var (element, pos) in atoms)
                root.AddChild(Place(sphereFor[element], pos));   // shared geometry = instancing
            var bond = MatrixTransform(FromTo(a, b)); bond.AddChild(cylinder);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            // caffeine coordinates (Å), PubChem CID 2519 3D conformer (heavy atoms + hydrogens)
            (char E, Vector3 P)[] atoms =
            [
                ('O', new(0.4700f, 2.5688f, 0.0006f)), ('O', new(-3.1271f, -0.4436f, -0.0003f)), ('N', new(-0.9686f, -1.3125f, 0.0000f)),
                ('N', new(2.2182f, 0.1412f, -0.0003f)), ('N', new(-1.3477f, 1.0797f, -0.0001f)), ('N', new(1.4119f, -1.9372f, 0.0002f)),
                ('C', new(0.8579f, 0.2592f, -0.0008f)), ('C', new(0.3897f, -1.0264f, -0.0004f)), ('C', new(0.0307f, 1.4220f, -0.0006f)),
                ('C', new(-1.9061f, -0.2495f, -0.0004f)), ('C', new(2.5032f, -1.1998f, 0.0003f)), ('C', new(-1.4276f, -2.6960f, 0.0008f)),
                ('C', new(3.1926f, 1.2061f, 0.0003f)), ('C', new(-2.2969f, 2.1881f, 0.0007f)),
                ('H', new(3.5163f, -1.5787f, 0.0008f)), ('H', new(-1.0451f, -3.1973f, -0.8937f)), ('H', new(-2.5186f, -2.7596f, 0.0011f)),
                ('H', new(-1.0447f, -3.1963f, 0.8957f)), ('H', new(4.1992f, 0.7801f, 0.0002f)), ('H', new(3.0468f, 1.8092f, -0.8992f)),
                ('H', new(3.0466f, 1.8083f, 0.9004f)), ('H', new(-1.8087f, 3.1651f, -0.0003f)), ('H', new(-2.9322f, 2.1027f, 0.8881f)),
                ('H', new(-2.9346f, 2.1021f, -0.8849f)),
            ];
            var colors = new Dictionary<char, (string Hex, float R)> { ['C'] = ("#3a3a3a", 0.42f), ['N'] = ("#3050f8", 0.4f), ['O'] = ("#ff2a2a", 0.4f), ['H'] = ("#f0f0f0", 0.24f) };
            var spheres = colors.ToDictionary(kv => kv.Key, kv =>
            {
                var s = Shapes.Sphere(kv.Value.R, 32, 16, Color.FromHex(kv.Value.Hex));
                s.StateSet!.Material!.Specular = new Vector3(0.7f);
                s.StateSet.Material.Shininess = 80;
                return s;
            });
            var root = new Group();
            foreach (var (e, p) in atoms) root.AddChild(Place(spheres[e], p));
            var bondGeom = Shapes.Cylinder(0.09f, 1, 16, Color.FromHex("#b0b7c3"));
            int bonds = 0;
            for (int i = 0; i < atoms.Length; i++)
                for (int j = i + 1; j < atoms.Length; j++)
                {
                    float d = Vector3.Distance(atoms[i].P, atoms[j].P);
                    float max = atoms[i].E == 'H' || atoms[j].E == 'H' ? 1.15f : 1.6f;
                    if (d > max) continue;
                    var a = atoms[i].P; var b = atoms[j].P;
                    var dir = Vector3.Normalize(b - a);
                    var axis = Vector3.Cross(Vector3.UnitZ, dir);
                    var rot = axis.LengthSquared() < 1e-8f ? Matrix4x4.Identity : Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(System.Math.Clamp(dir.Z, -1, 1)));
                    var mt = new MatrixTransform(Matrix4x4.CreateScale(1, 1, d) * rot * Matrix4x4.CreateTranslation((a + b) / 2));
                    mt.AddChild(bondGeom);
                    root.AddChild(mt);
                    bonds++;
                }
            var spin = new PositionAttitudeTransform();
            spin.AddChild(root);
            spin.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 12));
            ctx.Telemetry.Set("Formula", "C₈H₁₀N₄O₂");
            ctx.Telemetry.Set("Atoms / bonds", $"{atoms.Length} / {bonds}");
            ctx.Telemetry.Set("Molar mass", "194.19 g/mol");
            v.Environment.SkyTopColor = Color.FromHex("#1e293b");
            v.Camera.ClearColor = Color.FromHex("#0f172a");
            Orbit(v, 14, -20, 30);
            return spin;
        },
    };

    private static GallerySample MathSurface() => new()
    {
        Id = "math-surface",
        Category = SampleCategory.Academic,
        Accent = "#90be6d",
        Title = new("Function Plotter", "Plot Fungsi 3D"),
        Description = new("z = sin(r)/r · cos(θ·3) sampled on a 200×200 grid, coloured with viridis, animated phase; normals via the Rust fast path.",
                          "z = sin(r)/r · cos(θ·3) disampel pada grid 200×200, diwarnai viridis, fase beranimasi; normal dihitung via jalur cepat Rust."),
        Tags = ["math", "plot", "dynamic geometry"],
        Features = ["Dynamic Geometry", "ColorMap", "MeshUtil (Rust)", "Axes"],
        Code = """
            var surface = Shapes.Plane(20, 20, 200, 200, height: (x, y) => F(x, y, t));
            surface.Usage = BufferUsage.Dynamic;
            surface.Colors = surface.Positions.Select(p => ColorMap.Map(Kind.Viridis, p.Z, -1, 3)).ToArray();
            surface.ComputeNormals(); // osgnet_fast when available
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            static float F(float x, float y, float t)
            {
                float r = MathF.Sqrt(x * x + y * y) + 1e-3f;
                float th = MathF.Atan2(y, x);
                return 3 * MathF.Sin(r * 1.6f - t) / (r * 0.6f + 0.6f) * (0.6f + 0.4f * MathF.Cos(th * 3));
            }
            var surface = Shapes.Plane(20, 20, 200, 200, null, (x, y) => F(x, y, 0));
            surface.Usage = BufferUsage.Dynamic;
            surface.GetOrCreateStateSet().Material = new Material { Diffuse = Vector4.One, Specular = new Vector3(0.35f), Shininess = 40 };
            surface.StateSet.CullFace = CullFaceMode.None;
            var basePos = surface.Positions.Select(p => new Vector2(p.X, p.Y)).ToArray();
            var cols = new Vector4[basePos.Length];
            surface.OnUpdate((n, fs) =>
            {
                var g = (Geometry)n;
                float t = (float)fs.SimulationTime * 1.5f;
                var p = g.Positions;
                for (int i = 0; i < p.Length; i++)
                {
                    float z = F(basePos[i].X, basePos[i].Y, t);
                    p[i] = new Vector3(basePos[i], z);
                    cols[i] = ColorMap.Map(ColorMap.Kind.Viridis, z, -2.2f, 3.2f);
                }
                g.Colors = cols;
                g.ComputeNormals();
            });
            var root = new Group();
            root.AddChild(surface);
            root.AddChild(Shapes.Axes(12));
            root.AddChild(Place(Shapes.GridLines(20, 20, new Vector4(0.4f, 0.45f, 0.5f, 1)), new Vector3(0, 0, -3)));
            ctx.Telemetry.Set("Vertices", $"{surface.VertexCount:N0}");
            ctx.Telemetry.Set("Triangles", $"{surface.TriangleCount:N0}");
            ctx.Telemetry.Set("Normals", Interop.FastPath.IsAvailable ? "Rust osgnet_fast" : "managed");
            v.Environment.SkyTopColor = Color.FromHex("#1b2536");
            Orbit(v, 34, -40, 35);
            return root;
        },
    };

    // ================================================================ SCIENCE
    private static GallerySample CfdFlow() => new()
    {
        Id = "cfd",
        Category = SampleCategory.Science,
        Accent = "#219ebc",
        Title = new("CFD: Flow Around a Cylinder", "CFD: Aliran di Sekitar Silinder"),
        Description = new("Potential flow past a cylinder visualised with RK4 streamlines and velocity glyphs coloured by speed.",
                          "Aliran potensial melewati silinder divisualisasikan dengan streamline RK4 dan glyph kecepatan diwarnai menurut kelajuan."),
        Tags = ["CFD", "vector field", "streamlines"],
        Features = ["VectorField.Glyphs", "VectorField.Streamlines", "ColorMap"],
        Code = """
            Vector3 U(Vector3 p) { /* potential flow: uniform + doublet */ }
            root.AddChild(VectorField.Streamlines(U, seeds, steps: 400, stepSize: 0.05f));
            root.AddChild(VectorField.Glyphs(U, min, max, 24, 12, 1, scale: 0.45f));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            const float R = 1.2f, Uinf = 1;
            static Vector3 U(Vector3 p)
            {
                float r2 = p.X * p.X + p.Y * p.Y;
                if (r2 < R * R) return Vector3.Zero;
                float r4 = r2 * r2;
                float u = Uinf * (1 - R * R * (p.X * p.X - p.Y * p.Y) / r4);
                float w = -Uinf * 2 * R * R * p.X * p.Y / r4;
                return new Vector3(u, w, 0);
            }
            var root = new Group();
            var cyl = Shapes.Cylinder(R, 2.4f, 48, Color.FromHex("#adb5bd"));
            root.AddChild(cyl);
            var seeds = new List<Vector3>();
            for (int z = 0; z < 3; z++)
                for (int i = 0; i < 26; i++) seeds.Add(new Vector3(-8, -5 + i * 0.4f, -0.8f + z * 0.8f));
            root.AddChild(VectorField.Streamlines(U, seeds, 420, 0.045f, ColorMap.Kind.Inferno));
            root.AddChild(Place(VectorField.Glyphs(U, new Vector3(-7, -4.5f, 0), new Vector3(7, 4.5f, 0), 22, 12, 1, 0.5f, ColorMap.Kind.CoolWarm), new Vector3(0, 0, -1.4f)));
            ctx.Telemetry.Set("Model", "Inviscid potential flow");
            ctx.Telemetry.Set("Streamlines", $"{seeds.Count}");
            ctx.Telemetry.Set("Max speed", $"{2 * Uinf:F1} U∞ (cylinder shoulder)");
            v.Environment.SkyTopColor = Color.FromHex("#0b2233");
            v.Camera.ClearColor = Color.FromHex("#061019");
            Orbit(v, 20, -10, 55);
            return root;
        },
    };

    private static GallerySample Seismic() => new()
    {
        Id = "seismic",
        Category = SampleCategory.Science,
        Accent = "#bc6c25",
        Title = new("Seismic Volume", "Volume Seismik"),
        Description = new("A synthetic subsurface cube with folded layers and a salt dome: orthogonal slices plus an isosurface of the dome.",
                          "Kubus bawah permukaan sintetis dengan lapisan terlipat dan kubah garam: irisan ortogonal serta isosurface kubah."),
        Tags = ["geology", "oil & gas", "volume"],
        Features = ["ScalarField3D", "VolumeSlices", "Isosurface"],
        Code = """
            var cube = ScalarField3D.FromFunction(72, 10, p => Layers(p) + Dome(p));
            root.AddChild(VolumeSlices.Slice(cube, Axis.X, 36, ColorMap.Kind.CoolWarm));
            root.AddChild(Isosurface.Extract(domeField, 0.5f, Color.FromHex("#f2cc8f")));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var cube = ScalarField3D.FromFunction(72, 10, p => MathF.Sin(p.Z * 3.2f + 0.6f * MathF.Sin(p.X * 0.8f) + 0.4f * MathF.Cos(p.Y * 0.6f)));
            var dome = ScalarField3D.FromFunction(56, 10, p => 1 - (p - new Vector3(0.8f, -0.6f, -3.2f)).Length() / (2.2f + 1.5f * MathF.Max(0, p.Z + 1)));
            var root = new Group();
            root.AddChild(VolumeSlices.Slice(cube, VolumeSlices.Axis.X, 50, ColorMap.Kind.CoolWarm));
            root.AddChild(VolumeSlices.Slice(cube, VolumeSlices.Axis.Y, 20, ColorMap.Kind.CoolWarm));
            root.AddChild(VolumeSlices.Slice(cube, VolumeSlices.Axis.Z, 8, ColorMap.Kind.CoolWarm));
            var iso = Isosurface.Extract(dome, 0.5f, Color.FromHex("#f2cc8f"));
            root.AddChild(iso);
            var box = Shapes.Box(10, 10, 10);
            box.GetOrCreateStateSet().PolygonMode = State.PolygonMode.Line;
            box.StateSet.Lighting = false;
            box.StateSet.Material = Material.FromColor(0.8f, 0.8f, 0.8f);
            root.AddChild(box);
            ctx.Telemetry.Set("Volume", "72³ samples");
            ctx.Telemetry.Set("Isosurface", $"{iso.TriangleCount:N0} triangles");
            v.Environment.SkyTopColor = Color.FromHex("#2b2d42");
            Orbit(v, 26, -35, 25);
            return root;
        },
    };

    private static GallerySample SatelliteConstellation() => new()
    {
        Id = "satellites",
        Category = SampleCategory.Science,
        Accent = "#7209b7",
        Title = new("Satellite Constellation", "Konstelasi Satelit"),
        Description = new("WGS84 globe with Keplerian orbits of a Walker-style LEO constellation plus a geostationary ring.",
                          "Globe WGS84 dengan orbit Kepler konstelasi LEO bergaya Walker serta cincin geostasioner."),
        Tags = ["space", "orbits", "WGS84"],
        Features = ["Globe", "KeplerOrbit", "Ellipsoid", "LineStrip"],
        Code = """
            root.AddChild(Globe.Build(scale: 1e-6f));                // megametres
            var orbit = new KeplerOrbit { SemiMajorAxis = 6_928_000, InclinationDeg = 53, RaanDeg = plane * 30 };
            sat.OnUpdate((n, fs) => pat.Position = orbit.PositionAt(fs.SimulationTime * 60).ToVector3() * 1e-6f);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var earth = new PositionAttitudeTransform();
            earth.AddChild(Globe.Build(scale: 1e-6f));
            earth.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 360f / (86164 / 60f)));
            root.AddChild(earth);
            Sun(root, new Vector3(-1, -0.3f, -0.2f), 1.3f, false);
            var sat = Shapes.Box(0.08f, 0.08f, 0.08f, Color.FromHex("#ffd166"));
            int count = 0;
            for (int plane = 0; plane < 6; plane++)
            {
                var color = Color.FromHsv(plane * 60, 0.6f, 1, 0.55f);
                for (int s = 0; s < 6; s++)
                {
                    var orbit = new KeplerOrbit { SemiMajorAxis = 6_928_000, InclinationDeg = 53, RaanDeg = plane * 30, MeanAnomalyAtEpochDeg = s * 60 + plane * 10 };
                    if (s == 0) root.AddChild(Shapes.LineStrip(orbit.SampleTrack(), color));
                    var p = new PositionAttitudeTransform();
                    p.AddChild(sat);
                    p.OnUpdate((n, fs) => ((PositionAttitudeTransform)n).Position = orbit.PositionAt(fs.SimulationTime * 60).ToVector3() * 1e-6f);
                    root.AddChild(p);
                    count++;
                }
            }
            var geo = new KeplerOrbit { SemiMajorAxis = 42_164_000, InclinationDeg = 0 };
            root.AddChild(Shapes.LineStrip(geo.SampleTrack(256), new Vector4(1, 1, 1, 0.4f)));
            for (int i = 0; i < 3; i++)
            {
                var g = new KeplerOrbit { SemiMajorAxis = 42_164_000, InclinationDeg = 0, MeanAnomalyAtEpochDeg = i * 120 };
                var p = new PositionAttitudeTransform();
                p.AddChild(Shapes.Box(0.3f, 0.3f, 0.3f, Color.White));
                p.OnUpdate((n, fs) => ((PositionAttitudeTransform)n).Position = g.PositionAt(fs.SimulationTime * 60).ToVector3() * 1e-6f);
                root.AddChild(p);
            }
            ctx.Telemetry.Set("LEO", $"{count} sats @ 550 km, i=53°");
            ctx.Telemetry.Set("GEO", "3 sats @ 35 786 km");
            ctx.Telemetry.Set("LEO period", $"{new KeplerOrbit { SemiMajorAxis = 6_928_000 }.PeriodSeconds / 60:F1} min");
            ctx.Telemetry.Set("Time scale", "60×");
            v.Environment.HeadLight = false;
            v.Environment.AmbientLight = new Vector3(0.08f);
            v.Environment.SkyTopColor = Color.FromHex("#020617");
            v.Camera.ClearColor = Color.Black;
            Orbit(v, 60, -30, 20);
            return root;
        },
    };

    private static GallerySample CtScan() => new()
    {
        Id = "ct-scan",
        Category = SampleCategory.Medical,
        Accent = "#ef476f",
        Title = new("CT Volume & Isosurfaces", "Volume CT & Isosurface"),
        Description = new("A synthetic head phantom (64³) rendered as a translucent skull isosurface with axial, coronal and sagittal slices.",
                          "Phantom kepala sintetis (64³) ditampilkan sebagai isosurface tengkorak transparan dengan irisan aksial, koronal, dan sagital."),
        Tags = ["medical imaging", "DICOM-like", "isosurface"],
        Features = ["Isosurface (marching tetrahedra)", "VolumeSlices", "Transparency"],
        Code = """
            var ct = ScalarField3D.Phantom(64);
            var skull = Isosurface.Extract(ct, iso: 600, color: new Vector4(0.95f, 0.92f, 0.85f, 0.35f));
            skull.StateSet.MakeTransparent();
            root.AddChild(VolumeSlices.Slice(ct, Axis.Z, 32, ColorMap.Kind.Bone));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var ct = ScalarField3D.Phantom(64);
            var root = new Group();
            var skull = Isosurface.Extract(ct, 600, new Vector4(0.95f, 0.92f, 0.85f, 0.35f));
            skull.StateSet!.MakeTransparent();
            var brain = Isosurface.Extract(ct, 300, Color.FromHex("#f4a3b5"));
            root.AddChild(brain);
            root.AddChild(skull);
            root.AddChild(VolumeSlices.Slice(ct, VolumeSlices.Axis.Z, 34, ColorMap.Kind.Bone));
            root.AddChild(VolumeSlices.Slice(ct, VolumeSlices.Axis.X, 20, ColorMap.Kind.Bone));
            var spin = new PositionAttitudeTransform();
            spin.AddChild(root);
            spin.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 10));
            ctx.Telemetry.Set("Volume", "64 × 64 × 64 (HU-like)");
            ctx.Telemetry.Set("Skull", $"{skull.TriangleCount:N0} tris @ 600");
            ctx.Telemetry.Set("Soft tissue", $"{brain.TriangleCount:N0} tris @ 300");
            v.Environment.SkyTopColor = Color.FromHex("#111827");
            v.Camera.ClearColor = Color.FromHex("#030712");
            Orbit(v, 5.5f, -30, 15);
            return spin;
        },
    };
}
