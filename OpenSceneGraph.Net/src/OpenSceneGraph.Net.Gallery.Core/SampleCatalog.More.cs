using System.Numerics;
using OpenSceneGraph.Net.Animation;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Particles;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.State;
using OpenSceneGraph.Net.Terrain;

namespace OpenSceneGraph.Net.Gallery;

public static partial class SampleCatalog
{
    // ================================================================ OFFICE
    private static GallerySample BarChart() => new()
    {
        Id = "bar-chart",
        Category = SampleCategory.Office,
        Accent = "#06d6a0",
        Title = new("3D Sales Dashboard", "Dasbor Penjualan 3D"),
        Description = new("Quarterly revenue per region as animated 3D bars that grow in with an ease-out curve; hover-free, presentation ready.",
                          "Pendapatan per kuartal tiap wilayah sebagai batang 3D beranimasi yang tumbuh dengan kurva ease-out; siap presentasi."),
        Tags = ["business", "charts", "KPI"],
        Features = ["PositionAttitudeTransform", "Update callbacks", "Shadows"],
        Code = """
            var bar = new PositionAttitudeTransform { Position = new(x, y, 0) };
            bar.AddChild(unitBox);                                    // box from z=0..1
            bar.OnUpdate((n, fs) => ((PositionAttitudeTransform)n).Scale = new(1, 1, value * Ease(fs.SimulationTime)));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            string[] regions = ["Jakarta", "Surabaya", "Bandung", "Medan", "Makassar"];
            string[] palette = ["#4cc9f0", "#4361ee", "#7209b7", "#f72585", "#ffb703"];
            float[,] revenue = { { 8.2f, 9.1f, 10.4f, 12.0f }, { 5.1f, 5.8f, 6.3f, 7.4f }, { 4.4f, 4.9f, 5.6f, 6.1f }, { 3.2f, 3.9f, 4.1f, 4.8f }, { 2.1f, 2.6f, 3.3f, 3.9f } };
            var root = new Group();
            root.AddChild(Ground(40, Color.FromHex("#c9ced6")));
            Sun(root, new Vector3(-0.4f, 0.6f, -0.9f), 0.85f);
            var unit = Shapes.Box(1.4f, 1.4f, 1);
            for (int i = 0; i < 5; i++)
            {
                var mat = new StateSet { Material = Material.FromHex(palette[i]) };
                for (int q = 0; q < 4; q++)
                {
                    float value = revenue[i, q];
                    var inner = new PositionAttitudeTransform { Position = new Vector3(0, 0, 0.5f) };
                    inner.AddChild(unit);
                    var bar = new PositionAttitudeTransform { Position = new Vector3(q * 2.6f - 4, i * 2.6f - 5.2f, 0), StateSet = mat };
                    bar.AddChild(inner);
                    float delay = i * 0.15f + q * 0.1f;
                    bar.OnUpdate((n, fs) =>
                    {
                        float t = MathF.Min(1, MathF.Max(0, (float)fs.SimulationTime - delay) / 1.5f);
                        float ease = 1 - MathF.Pow(1 - t, 3);
                        ((PositionAttitudeTransform)n).Scale = new Vector3(1, 1, MathF.Max(0.001f, value * ease));
                    });
                    root.AddChild(bar);
                }
                ctx.Telemetry.Set(regions[i], $"Rp {Enumerable.Range(0, 4).Sum(q => revenue[i, q]):F1} M");
            }
            v.Environment.Shadows.Enabled = true;
            v.Environment.Shadows.Radius = 20;
            v.Environment.SkyTopColor = Color.FromHex("#dee2e6");
            v.Camera.ClearColor = Color.FromHex("#f8f9fa");
            v.Environment.AmbientLight = new Vector3(0.16f);
            v.Environment.HeadLightIntensity = 0.25f;
            Orbit(v, 48, -35, 34, new Vector3(0, -1, 3));
            return root;
        },
    };

    private static GallerySample DigitalTwin() => new()
    {
        Id = "digital-twin",
        Category = SampleCategory.Office,
        Accent = "#ffd166",
        Title = new("Smart Building Digital Twin", "Digital Twin Gedung Pintar"),
        Description = new("A 6-floor office where every room is colour-coded by live IoT temperature; floors can be isolated with a Switch node.",
                          "Kantor 6 lantai dengan tiap ruangan diberi warna sesuai suhu IoT secara langsung; lantai dapat diisolasi dengan node Switch."),
        Tags = ["IoT", "digital twin", "facility"],
        Features = ["Switch", "StateSet sharing", "Telemetry", "Transparency"],
        Code = """
            var floors = new Switch();
            foreach (var floor in building) floors.AddChild(floor, true);
            room.StateSet.Material.Diffuse = ColorMap.Map(Kind.CoolWarm, sensor.Temperature, 18, 30);
            floors.SetSingleChildOn(3);                 // isolate floor 4
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(80, Color.FromHex("#2b2d42")));
            Sun(root, new Vector3(-0.3f, 0.5f, -0.8f), 0.9f, false);
            var floors = new Switch { Name = "floors" };
            var rooms = new List<(Material M, float Phase)>();
            var rng = new Random(4);
            for (int f = 0; f < 6; f++)
            {
                var floor = new Group { Name = $"floor{f + 1}" };
                var slab = Shapes.Box(24, 16, 0.3f, Color.FromHex("#adb5bd"));
                floor.AddChild(Place(slab, new Vector3(0, 0, f * 3.6f)));
                for (int rx = 0; rx < 4; rx++)
                    for (int ry = 0; ry < 3; ry++)
                    {
                        var m = new Material { Diffuse = new Vector4(0.5f, 0.5f, 0.5f, 0.75f) };
                        var room = Shapes.Box(5.4f, 4.8f, 3);
                        room.GetOrCreateStateSet().Material = m;
                        room.StateSet.MakeTransparent();
                        floor.AddChild(Place(room, new Vector3(rx * 6 - 9, ry * 5.2f - 5.2f, f * 3.6f + 1.7f)));
                        rooms.Add((m, rng.NextSingle() * 10));
                    }
                floors.AddChild(floor, true);
            }
            root.AddChild(floors);
            int isolate = -1;
            double nextSwitch = 6;
            v.Updated += (_, fs) =>
            {
                float t = (float)fs.SimulationTime;
                float sum = 0, max = 0;
                foreach (var (m, phase) in rooms)
                {
                    float temp = 23 + 4 * MathF.Sin(t * 0.4f + phase) + 1.5f * MathF.Sin(t * 1.3f + phase * 2);
                    sum += temp; max = MathF.Max(max, temp);
                    m.Diffuse = Science.ColorMap.Map(Science.ColorMap.Kind.CoolWarm, temp, 18, 30, 0.72f);
                }
                if (fs.SimulationTime > nextSwitch)
                {
                    nextSwitch += 6;
                    isolate = isolate >= 5 ? -1 : isolate + 1;
                    if (isolate < 0) floors.SetAllChildrenOn(); else floors.SetSingleChildOn(isolate);
                }
                ctx.Telemetry.Set("Sensors", $"{rooms.Count}");
                ctx.Telemetry.Set("Avg temp", $"{sum / rooms.Count:F1} °C");
                ctx.Telemetry.Set("Max temp", $"{max:F1} °C");
                ctx.Telemetry.Set("View", isolate < 0 ? "All floors" : $"Floor {isolate + 1}");
            };
            v.Environment.SkyTopColor = Color.FromHex("#14213d");
            v.Camera.ClearColor = Color.FromHex("#0b132b");
            Orbit(v, 55, -35, 30, new Vector3(0, 0, 9));
            return root;
        },
    };

    // ================================================================ ENTERTAINMENT
    private static GallerySample ParticleShowcase() => new()
    {
        Id = "particles",
        Category = SampleCategory.Entertainment,
        Accent = "#fb8500",
        Title = new("Particle Effects", "Efek Partikel"),
        Description = new("Fire, smoke, sparks, dust, snow and periodic explosions from osgParticle-style emitters, drawn as soft point sprites with bloom.",
                          "Api, asap, percikan, debu, salju, dan ledakan berkala dari emitter bergaya osgParticle, digambar sebagai point sprite lembut dengan bloom."),
        Tags = ["VFX", "games"],
        Features = ["ParticleSystem", "Additive blending", "Bloom", "HDR"],
        Code = """
            root.AddChild(ParticleEffects.Fire(new Vector3(0, 0, 0), scale: 2));
            root.AddChild(ParticleEffects.Smoke(new Vector3(0, 0, 2), scale: 2));
            var boom = ParticleEffects.Explosion(pos, autoBurst: false);
            boom.Burst(800);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(80, Color.FromHex("#3a3a3a")));
            var fire = ParticleEffects.Fire(new Vector3(0, 0, 0.2f), 2);
            root.AddChild(fire);
            root.AddChild(ParticleEffects.Smoke(new Vector3(0, 0, 3), 2));
            root.AddChild(ParticleEffects.Sparks(new Vector3(12, 0, 1), 1.5f));
            root.AddChild(ParticleEffects.Dust(new Vector3(-12, 0, 0.3f), 1.5f));
            root.AddChild(ParticleEffects.Snow(Vector3.Zero, 70, 30, 0.6f));
            var boom = ParticleEffects.Explosion(new Vector3(0, 14, 1), 2, autoBurst: false);
            root.AddChild(boom);
            root.AddChild(new LightSource(Light.Point(new Vector3(0, 0, 3), new Vector3(1, 0.55f, 0.2f), 30, 2.2f)));
            for (int i = 0; i < 8; i++) root.AddChild(Place(Shapes.Box(1.6f, 1.6f, 1.6f, Color.FromHex("#6c584c")), new Vector3(MathF.Cos(i) * 5, MathF.Sin(i) * 5, 0.8f), i * 40));
            double next = 1;
            v.Updated += (_, fs) =>
            {
                if (fs.SimulationTime > next) { boom.Burst(800); next = fs.SimulationTime + 3; }
                ctx.Telemetry.Set("Fire particles", fire.AliveCount.ToString("N0"));
                ctx.Telemetry.Set("Explosion particles", boom.AliveCount.ToString("N0"));
                ctx.Telemetry.Set("Integrator", Interop.FastPath.IsAvailable ? "Rust (rayon) for >4k" : "managed");
            };
            v.Environment.HeadLight = false;
            v.Environment.AmbientLight = new Vector3(0.12f, 0.12f, 0.16f);
            v.Environment.SkyTopColor = Color.FromHex("#0d1b2a");
            v.Camera.ClearColor = Color.FromHex("#1b263b");
            v.Environment.Post.Bloom = true;
            v.Environment.Post.Hdr = true;
            v.Environment.Post.Vignette = true;
            Orbit(v, 42, -25, 18, new Vector3(0, 0, 4));
            return root;
        },
    };

    private static GallerySample CreatureRig() => new()
    {
        Id = "skeletal",
        Category = SampleCategory.Entertainment,
        Accent = "#e0567a",
        Title = new("Skeletal Animation", "Animasi Skeletal"),
        Description = new("A sea anemone of skinned tentacles: bone hierarchies driven by keyframed clips, linear-blend skinning on the CPU.",
                          "Anemon laut dengan tentakel ber-skin: hierarki tulang digerakkan klip keyframe, skinning linear-blend di CPU."),
        Tags = ["osgAnimation", "rigging", "keyframes"],
        Features = ["Skeleton", "Bone", "RigGeometry", "AnimationManager"],
        Code = """
            var skeleton = RigFactory.Tentacle(segments: 6, length: 6);
            // or build your own: new Skeleton(); bones; skeleton.SetBindPose();
            // rig = new RigGeometry { BoneIndices = ..., BoneWeights = ... };
            manager.Register(clip).Play("wave");
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(40, Color.FromHex("#264653")));
            Sun(root, new Vector3(-0.3f, 0.5f, -0.8f), 1.0f);
            var rng = new Random(2);
            int bones = 0;
            for (int i = 0; i < 9; i++)
            {
                float a = i / 9f * MathF.Tau;
                var sk = RigFactory.Tentacle(6, 5 + rng.NextSingle() * 3, 0.45f, Color.FromHsv(330 + i * 8, 0.65f, 0.95f));
                bones += sk.Bones.Count;
                var p = new PositionAttitudeTransform { Position = new Vector3(MathF.Cos(a) * 2.2f, MathF.Sin(a) * 2.2f, 0), Attitude = Quaternion.CreateFromAxisAngle(new Vector3(-MathF.Sin(a), MathF.Cos(a), 0), 0.25f) };
                p.AddChild(sk);
                root.AddChild(p);
            }
            root.AddChild(Place(Shapes.Sphere(2.6f, 32, 16, Color.FromHex("#9d4edd")), new Vector3(0, 0, -1.2f)));
            ctx.Telemetry.Set("Skeletons", "9");
            ctx.Telemetry.Set("Bones", bones.ToString());
            ctx.Telemetry.Set("Skinning", "CPU linear blend (parallel)");
            v.Environment.Shadows.Enabled = true;
            v.Environment.Shadows.Radius = 14;
            v.Environment.Fog = new FogSettings(true, new Vector3(0.05f, 0.2f, 0.28f), 25, 70);
            v.Environment.SkyTopColor = Color.FromHex("#023047");
            v.Camera.ClearColor = Color.FromHex("#0d3b4f");
            Orbit(v, 22, -30, 20, new Vector3(0, 0, 3));
            return root;
        },
    };

    private static GallerySample NightCity() => new()
    {
        Id = "night-city",
        Category = SampleCategory.Entertainment,
        Accent = "#f72585",
        Title = new("Neon Night City", "Kota Neon Malam Hari"),
        Description = new("Procedural city blocks with emissive windows, coloured street point lights, traffic on looping paths, fog and bloom.",
                          "Blok kota prosedural dengan jendela bercahaya, lampu jalan berwarna, lalu lintas pada jalur melingkar, kabut, dan bloom."),
        Tags = ["procedural", "lighting", "games"],
        Features = ["8 point lights", "Fog", "Bloom", "Small feature culling", "LOD"],
        Code = """
            root.AddChild(new LightSource(Light.Point(pos, neon, range: 40, intensity: 2)));
            viewer.Environment.Fog = new FogSettings(true, fogColor, 40, 220);
            viewer.Environment.Post.Bloom = true;
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(400, Color.FromHex("#14141c")));
            var rng = new Random(11);
            int buildings = 0;
            for (int bx = -6; bx <= 6; bx++)
                for (int by = -6; by <= 6; by++)
                {
                    if (bx == 0 || by == 0) continue; // avenues
                    float h = 8 + MathF.Pow(rng.NextSingle(), 2) * 60 * (1 - MathF.Sqrt(bx * bx + by * by) / 10);
                    var col = Color.FromHsv(220 + rng.NextSingle() * 40, 0.25f, 0.18f + rng.NextSingle() * 0.12f);
                    var b = Models.Building(9, 9, MathF.Max(6, h), col, lit: true);
                    var lod = new Lod();
                    lod.AddChild(b, 0, 260);
                    lod.AddChild(Place(Shapes.Box(9, 9, MathF.Max(6, h), col), new Vector3(0, 0, MathF.Max(6, h) / 2)), 260, float.MaxValue);
                    root.AddChild(Place(lod, new Vector3(bx * 14, by * 14, 0)));
                    buildings++;
                }
            string[] neon = ["#f72585", "#4cc9f0", "#7209b7", "#06d6a0", "#ffbe0b", "#fb5607", "#3a86ff", "#ff006e"];
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * MathF.Tau;
                var c = Color.FromHex(neon[i]);
                var pos = new Vector3(MathF.Cos(a) * 45, MathF.Sin(a) * 45, 6);
                root.AddChild(new LightSource(Light.Point(pos, new Vector3(c.X, c.Y, c.Z), 55, 2.4f)));
                var orb = Shapes.Sphere(0.8f, 16, 8);
                orb.GetOrCreateStateSet().Material = Material.Emissive(new Vector3(c.X, c.Y, c.Z) * 2);
                root.AddChild(Place(orb, pos));
            }
            for (int i = 0; i < 10; i++)
            {
                var car = new PositionAttitudeTransform();
                car.AddChild(Models.Car(Color.FromHsv(rng.Next(360), 0.8f, 0.9f)));
                var lamp = Shapes.Box(1.6f, 0.2f, 0.3f);
                lamp.GetOrCreateStateSet().Material = Material.Emissive(new Vector3(1, 0.95f, 0.8f) * 2);
                car.AddChild(Place(lamp, new Vector3(0, 2.15f, 0.7f)));
                bool ns = i % 2 == 0;
                float lane = (i % 4 < 2 ? -2.5f : 2.5f);
                var wps = ns
                    ? new List<Vector3> { new(lane, -90, 0), new(lane, 90, 0) }
                    : new List<Vector3> { new(-90, lane, 0), new(90, lane, 0) };
                car.AddUpdateCallback(new AnimationPathCallback(AnimationPath.ThroughWaypoints(wps, 14 + i, LoopMode.Loop)) { TimeOffset = i * 2.3 });
                root.AddChild(car);
            }
            ctx.Telemetry.Set("Buildings", buildings.ToString());
            ctx.Telemetry.Set("Lights", "8 point + headlight");
            v.Environment.HeadLight = false;
            v.Environment.AmbientLight = new Vector3(0.05f, 0.05f, 0.09f);
            v.Environment.Fog = new FogSettings(true, new Vector3(0.08f, 0.05f, 0.14f), 60, 260);
            v.Environment.SkyTopColor = Color.FromHex("#10002b");
            v.Camera.ClearColor = Color.FromHex("#240046");
            v.Environment.Post.Bloom = true;
            v.Environment.Post.BloomThreshold = 0.6f;
            v.Environment.Post.Hdr = true;
            v.Environment.Post.Vignette = true;
            Orbit(v, 160, -30, 22, new Vector3(0, 0, 10));
            return root;
        },
    };

    // ================================================================ SIMULATORS
    private static GallerySample FlightSim() => new()
    {
        Id = "flight-sim",
        Category = SampleCategory.Simulator,
        Accent = "#48cae4",
        Title = new("Flight Simulator", "Simulator Penerbangan"),
        Description = new("Fly a jet over procedural mountains: bank-to-turn dynamics, stall behaviour, terrain collision and a chase camera.",
                          "Terbangkan jet di atas pegunungan prosedural: dinamika bank-to-turn, perilaku stall, tabrakan terrain, dan kamera pengejar."),
        Tags = ["interactive", "aircraft", "physics"],
        Features = ["Aircraft dynamics", "Chase camera", "TerrainBuilder", "Fog", "OBJ asset (Rodin + Blender)"],
        Code = """
            var jet = new Aircraft { Position = new(0, -400, 250), GroundHeight = hf.Sample };
            jet.Controls.Throttle = keys["w"] ? 1 : 0.6f;
            jet.Controls.Roll = (keys["d"] ? 1 : 0) - (keys["a"] ? 1 : 0);
            jet.Step(dt); jet.ApplyTo(pat);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var hf = TerrainGenerator.Fractal(129, 20f, 380, seed: 77);
            root.AddChild(TerrainBuilder.Build(hf, 4, 3, ColorRamp.Natural()));
            Sun(root, new Vector3(-0.4f, 0.5f, -0.6f), 1.1f, false);
            var jet = new Aircraft { Position = new Vector3(0, -900, 520), Heading = 0, GroundHeight = hf.Sample };
            var pat = new PositionAttitudeTransform();
            pat.AddChild(Assets.Jet()); // Rodin-generated, Blender-cleaned model
            root.AddChild(pat);
            var trail = ParticleEffects.Smoke(Vector3.Zero, 1);
            trail.Emitter.Rate = 60; trail.Gravity = Vector3.Zero; trail.ColorStart = new Vector4(1, 1, 1, 0.4f);
            root.AddChild(trail);
            var keys = new KeyState();
            v.EventHandlers.Add(keys);
            var chase = new ChaseCamera(() => pat.Position, () => jet.Heading, 45, 14);
            v.CameraManipulator = chase;
            v.Updated += (_, fs) =>
            {
                float dt = (float)fs.DeltaTime;
                jet.Controls.Throttle = keys.Any("w", "ArrowUp") ? 1 : keys.Any("s", "ArrowDown") ? 0.15f : 0.62f;
                jet.Controls.Roll = (keys.Any("d", "ArrowRight") ? 1 : 0) - (keys.Any("a", "ArrowLeft") ? 1 : 0);
                jet.Controls.Pitch = (keys["i"] ? 1 : 0) - (keys["k"] ? 1 : 0);
                if (keys["r"] || jet.Crashed) { jet = new Aircraft { Position = new Vector3(0, -900, 520), GroundHeight = hf.Sample }; }
                jet.Step(dt);
                jet.ApplyTo(pat);
                trail.Emitter.Position = pat.Position - jet.Forward * 7;
                float agl = jet.Position.Z - (hf.Sample(jet.Position.X, jet.Position.Y) ?? 0);
                ctx.Telemetry.Set("Airspeed", $"{jet.Speed * 1.944f:F0} kt");
                ctx.Telemetry.Set("Altitude", $"{jet.Position.Z:F0} m (AGL {agl:F0})");
                ctx.Telemetry.Set("Heading", $"{jet.Heading:000}°");
                ctx.Telemetry.Set("Bank / Pitch", $"{jet.Roll:F0}° / {jet.Pitch:F0}°");
            };
            ctx.Controls.Add(("W / S", new("Throttle up / idle", "Gas naik / idle")));
            ctx.Controls.Add(("A / D", new("Bank left / right", "Miring kiri / kanan")));
            ctx.Controls.Add(("I / K", new("Pitch up / down", "Hidung naik / turun")));
            ctx.Controls.Add(("R", new("Reset", "Ulang")));
            v.Environment.Fog = new FogSettings(true, new Vector3(0.72f, 0.8f, 0.9f), 900, 3200);
            v.Environment.SkyTopColor = Color.FromHex("#2a6fb0");
            v.Camera.ClearColor = Color.FromHex("#bcd4e6");
            v.Camera.FarPlane = 20000;
            return root;
        },
    };

    private static GallerySample MaritimeSim() => new()
    {
        Id = "maritime",
        Category = SampleCategory.Simulator,
        Accent = "#0077b6",
        Title = new("Maritime Patrol", "Patroli Maritim"),
        Description = new("A frigate on an animated ocean (vertex-displaced waves) with sea-state motion, wake particles and an escort.",
                          "Fregat di atas laut beranimasi (gelombang hasil displacement vertex) dengan gerakan sea-state, partikel buih, dan kapal pengawal."),
        Tags = ["naval", "ocean", "interactive"],
        Features = ["Ship dynamics", "Dynamic Geometry", "Reflectivity", "Particles"],
        Code = """
            ocean.OnUpdate((n, fs) => { /* Gerstner-like sum of sines, MarkDirty() */ });
            var ship = new Ship { SeaState = 4 };
            ship.Controls.Throttle = 0.7f; ship.Step(dt); ship.ApplyTo(pat);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var ocean = Shapes.Plane(1600, 1600, 160, 160, Color.FromHex("#1d4e89"));
            ocean.Usage = BufferUsage.Dynamic;
            ocean.StateSet!.Material!.Reflectivity = 0.55f;
            ocean.StateSet.Material.Specular = new Vector3(0.9f);
            ocean.StateSet.Material.Shininess = 90;
            ocean.StateSet.CastShadows = false;
            var basePos = ocean.Positions.ToArray();
            ocean.OnUpdate((n, fs) =>
            {
                var g = (Geometry)n;
                float t = (float)fs.SimulationTime;
                var p = g.Positions;
                for (int i = 0; i < p.Length; i++)
                {
                    var b = basePos[i];
                    p[i].Z = 0.9f * MathF.Sin(b.X * 0.035f + t * 1.1f) + 0.6f * MathF.Sin(b.Y * 0.05f + t * 0.8f) + 0.25f * MathF.Sin((b.X + b.Y) * 0.12f + t * 2.1f);
                }
                g.ComputeNormals();
            });
            root.AddChild(ocean);
            Sun(root, new Vector3(-0.6f, 0.3f, -0.5f), 1.2f, false);
            var ship = new Ship { SeaState = 4, Heading = 20 };
            var pat = new PositionAttitudeTransform();
            pat.AddChild(Models.Ship());
            root.AddChild(pat);
            var escort = new PositionAttitudeTransform();
            escort.AddChild(Place(Models.Ship(Color.FromHex("#6c757d")), Vector3.Zero, scale: 0.6f));
            escort.AddUpdateCallback(new AnimationPathCallback(AnimationPath.Circle(Vector3.Zero, 300, 120)));
            root.AddChild(escort);
            var wake = ParticleEffects.Dust(Vector3.Zero, 2);
            wake.ColorStart = new Vector4(1, 1, 1, 0.6f); wake.ColorMid = new Vector4(0.9f, 0.95f, 1, 0.35f); wake.ColorEnd = new Vector4(1, 1, 1, 0); wake.Emitter.Rate = 70;
            root.AddChild(wake);
            var keys = new KeyState();
            v.EventHandlers.Add(keys);
            v.CameraManipulator = new ChaseCamera(() => pat.Position, () => ship.Heading, 120, 45);
            v.Updated += (_, fs) =>
            {
                ship.Controls.Throttle = keys.Any("w", "ArrowUp") ? 1 : keys.Any("s", "ArrowDown") ? 0 : 0.55f;
                ship.Controls.Yaw = (keys.Any("d", "ArrowRight") ? 1 : 0) - (keys.Any("a", "ArrowLeft") ? 1 : 0);
                ship.Step((float)fs.DeltaTime);
                ship.ApplyTo(pat);
                wake.Emitter.Position = pat.Position - ship.Forward * 22;
                ctx.Telemetry.Set("Speed", $"{ship.Speed * 1.944f:F1} kn");
                ctx.Telemetry.Set("Course", $"{ship.Heading:000}°");
                ctx.Telemetry.Set("Sea state", "4 (moderate)");
            };
            ctx.Controls.Add(("W / S", new("Ahead / stop", "Maju / berhenti")));
            ctx.Controls.Add(("A / D", new("Rudder", "Kemudi")));
            v.Environment.Fog = new FogSettings(true, new Vector3(0.62f, 0.72f, 0.8f), 300, 1400);
            v.Environment.SkyTopColor = Color.FromHex("#4a7fb5");
            v.Environment.ReflectionColor = new Vector3(0.6f, 0.72f, 0.85f);
            v.Camera.ClearColor = Color.FromHex("#a9c3d9");
            return root;
        },
    };

    private static GallerySample DrivingSim() => new()
    {
        Id = "driving",
        Category = SampleCategory.Simulator,
        Accent = "#80b918",
        Title = new("Off-road Driving", "Mengemudi Off-road"),
        Description = new("Drive a ground vehicle across hills: terrain following, slope-aligned pitch/roll and dust thrown by the wheels.",
                          "Kendarai kendaraan darat melintasi perbukitan: mengikuti terrain, pitch/roll mengikuti kemiringan, dan debu dari roda."),
        Tags = ["vehicle", "terrain", "interactive"],
        Features = ["GroundVehicle", "HeightField.Sample", "Particles", "Shadows"],
        Code = """
            var car = new GroundVehicle { GroundHeight = hf.Sample };
            car.Controls.Throttle = keys["w"] ? 1 : 0;
            car.Controls.Yaw = steer;
            car.Step(dt); car.ApplyTo(pat);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            var hf = TerrainGenerator.Fractal(129, 3f, 26, seed: 5);
            root.AddChild(TerrainBuilder.Build(hf, 4, 3, ColorRamp.Desert()));
            Sun(root, new Vector3(-0.5f, 0.3f, -0.7f), 1.15f);
            var rng = new Random(9);
            for (int i = 0; i < 60; i++)
            {
                float x = rng.NextSingle() * 360 - 180, y = rng.NextSingle() * 360 - 180;
                var rock = Shapes.Sphere(1 + rng.NextSingle() * 2, 7, 5, Color.FromHex("#8d6e55"));
                root.AddChild(Place(rock, new Vector3(x, y, hf.Sample(x, y) ?? 0), rng.Next(360)));
            }
            var car = new GroundVehicle { GroundHeight = hf.Sample, MaxSpeed = 22 };
            var pat = new PositionAttitudeTransform();
            pat.AddChild(Models.Car(Color.FromHex("#e63946")));
            root.AddChild(pat);
            var dust = ParticleEffects.Dust(Vector3.Zero, 0.8f);
            root.AddChild(dust);
            var keys = new KeyState();
            v.EventHandlers.Add(keys);
            v.CameraManipulator = new ChaseCamera(() => pat.Position, () => car.Heading, 14, 5);
            v.Updated += (_, fs) =>
            {
                car.Controls.Throttle = keys.Any("w", "ArrowUp") ? 1 : 0;
                car.Controls.Brake = keys.Any("s", "ArrowDown") ? 1 : 0;
                car.Controls.Yaw = (keys.Any("d", "ArrowRight") ? 1 : 0) - (keys.Any("a", "ArrowLeft") ? 1 : 0);
                car.Step((float)fs.DeltaTime);
                car.ApplyTo(pat);
                dust.Emitter.Position = pat.Position - car.Forward * 2;
                dust.Emitter.Rate = MathF.Min(80, car.Speed * 5);
                ctx.Telemetry.Set("Speed", $"{car.Speed * 3.6f:F0} km/h");
                ctx.Telemetry.Set("Slope", $"pitch {car.Pitch:F0}° roll {car.Roll:F0}°");
            };
            ctx.Controls.Add(("W / S", new("Accelerate / brake", "Gas / rem")));
            ctx.Controls.Add(("A / D", new("Steer", "Belok")));
            v.Environment.Shadows.Enabled = true;
            v.Environment.Shadows.Radius = 40;
            v.Environment.Fog = new FogSettings(true, new Vector3(0.86f, 0.78f, 0.66f), 120, 420);
            v.Environment.SkyTopColor = Color.FromHex("#6a9fd4");
            v.Camera.ClearColor = Color.FromHex("#e9d8b8");
            return root;
        },
    };

    // ================================================================ GIS
    private static GallerySample TerrainLod() => new()
    {
        Id = "terrain-lod",
        Category = SampleCategory.Gis,
        Accent = "#52b788",
        Title = new("Terrain LOD & Culling", "LOD Terrain & Culling"),
        Description = new("A 513×513 heightfield split into 64 tiles with 4 LOD levels; watch triangle counts drop as you zoom out.",
                          "Heightfield 513×513 dibagi menjadi 64 tile dengan 4 level LOD; lihat jumlah segitiga turun saat Anda zoom out."),
        Tags = ["LOD", "terrain", "culling", "DEM"],
        Features = ["LOD", "View frustum culling", "Small feature culling", "Stats"],
        Code = """
            var hf = TerrainGenerator.Fractal(513, 10f, 900);      // or HeightFieldReaderWriter.ReadHeightField("dem.tif")
            var terrain = TerrainBuilder.Build(hf, tilesPerSide: 8, levels: 4, ColorRamp.Natural());
            viewer.CameraManipulator = new TerrainManipulator { HeightAt = hf.Sample };
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var hf = TerrainGenerator.Fractal(513, 10f, 900, seed: 2024, octaves: 7);
            var root = new Group();
            root.AddChild(TerrainBuilder.Build(hf, 8, 4, ColorRamp.Natural(), 1.2f));
            Sun(root, new Vector3(-0.5f, 0.2f, -0.6f), 1.15f, false);
            v.Updated += (viewer, _) =>
            {
                var st = viewer.LastRenderList.Stats;
                ctx.Telemetry.Set("Rendered triangles", st.Triangles.ToString("N0"));
                ctx.Telemetry.Set("Full resolution", $"{512L * 512 * 2:N0}");
                ctx.Telemetry.Set("Draw items", st.DrawItems.ToString());
                ctx.Telemetry.Set("Culled nodes", st.NodesCulled.ToString());
                ctx.Telemetry.Set("Cull time", $"{st.CullMs:F2} ms");
            };
            v.Environment.Fog = new FogSettings(true, new Vector3(0.75f, 0.82f, 0.9f), 2500, 9000);
            v.Environment.SkyTopColor = Color.FromHex("#3b6ea5");
            v.Camera.ClearColor = Color.FromHex("#c5d7e8");
            v.Camera.FarPlane = 30000;
            v.CameraManipulator = new TerrainManipulator { HeightAt = hf.Sample, Distance = 4200, Pitch = 30, Heading = -30 };
            return root;
        },
    };

    private static GallerySample GeoGlobe() => new()
    {
        Id = "geo-globe",
        Category = SampleCategory.Gis,
        Accent = "#2a9d8f",
        Title = new("WGS84 Globe & Cities", "Globe WGS84 & Kota"),
        Description = new("Geodetic ↔ ECEF ↔ UTM in action: major Indonesian and world cities placed on the ellipsoid with great-circle routes.",
                          "Konversi geodetik ↔ ECEF ↔ UTM: kota-kota besar Indonesia dan dunia ditempatkan di ellipsoid dengan rute lingkaran besar."),
        Tags = ["WGS84", "ECEF", "UTM", "great circle"],
        Features = ["Ellipsoid", "Utm", "Globe", "LineStrip"],
        Code = """
            var ecef = Ellipsoid.Wgs84.ToEcef(new GeoPoint(-6.2088, 106.8456));   // Jakarta
            var utm  = Utm.FromGeo(new GeoPoint(-6.2088, 106.8456));              // 48S 701xxx E
            var mid  = Ellipsoid.Wgs84.Destination(jakarta, bearing, distance / 2);
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var e = Ellipsoid.Wgs84;
            const float s = 1e-6f;
            var root = new Group();
            root.AddChild(Globe.Build(e, s));
            Sun(root, new Vector3(-1, -0.4f, -0.3f), 1.2f, false);
            (string Name, double Lat, double Lon)[] cities =
            [
                ("Jakarta", -6.2088, 106.8456), ("Surabaya", -7.2575, 112.7521), ("Makassar", -5.1477, 119.4327), ("Medan", 3.5952, 98.6722),
                ("Jayapura", -2.5337, 140.7181), ("Singapore", 1.3521, 103.8198), ("Tokyo", 35.6762, 139.6503), ("London", 51.5074, -0.1278),
                ("New York", 40.7128, -74.0060), ("Sydney", -33.8688, 151.2093),
            ];
            var marker = Shapes.Sphere(0.04f, 12, 8, Color.FromHex("#ffd166"));
            marker.StateSet!.Material = Material.Emissive(new Vector3(1, 0.8f, 0.3f));
            foreach (var c in cities)
            {
                var p = e.ToEcef(new GeoPoint(c.Lat, c.Lon, 20000)).ToVector3() * s;
                root.AddChild(Place(marker, p));
            }
            var jkt = new GeoPoint(-6.2088, 106.8456);
            foreach (var target in cities.Skip(5))
            {
                var to = new GeoPoint(target.Lat, target.Lon);
                double dist = e.Distance(jkt, to), bearing0 = Ellipsoid.Bearing(jkt, to);
                var pts = new List<Vector3>();
                var cur = jkt;
                for (int i = 0; i <= 64; i++)
                {
                    double f = i / 64.0;
                    var gp = Slerp(jkt, to, f);
                    double alt = 60000 + 600000 * System.Math.Sin(f * System.Math.PI) * dist / 2e7;
                    pts.Add(e.ToEcef(gp with { Height = alt }).ToVector3() * s);
                }
                root.AddChild(Shapes.LineStrip(pts, new Vector4(0.3f, 0.85f, 1f, 0.9f)));
                ctx.Telemetry.Set($"JKT → {target.Name}", $"{dist / 1000:N0} km, {bearing0:000}°");
            }
            var utm = Utm.FromGeo(jkt);
            ctx.Telemetry.Set("Jakarta UTM", utm.ToString());
            ctx.Telemetry.Set("Jakarta ECEF", $"{e.ToEcef(jkt).X / 1000:N0}, {e.ToEcef(jkt).Y / 1000:N0}, {e.ToEcef(jkt).Z / 1000:N0} km");
            v.Environment.HeadLight = false;
            v.Environment.AmbientLight = new Vector3(0.12f);
            v.Environment.SkyTopColor = Color.FromHex("#020617");
            v.Camera.ClearColor = Color.Black;
            var tb = new TrackballManipulator();
            tb.SetLookAt(e.ToEcef(new GeoPoint(-5, 115, 12_000_000)).ToVector3() * s, Vector3.Zero);
            v.CameraManipulator = tb;
            return root;

            static GeoPoint Slerp(GeoPoint a, GeoPoint b, double t)
            {
                var ea = Ellipsoid.Wgs84.ToEcef(a).Normalized();
                var eb = Ellipsoid.Wgs84.ToEcef(b).Normalized();
                double omega = System.Math.Acos(System.Math.Clamp(Vector3d.Dot(ea, eb), -1, 1));
                if (omega < 1e-9) return a;
                var p = ea * (System.Math.Sin((1 - t) * omega) / System.Math.Sin(omega)) + eb * (System.Math.Sin(t * omega) / System.Math.Sin(omega));
                var g = Ellipsoid.Wgs84.FromEcef(p * Ellipsoid.Wgs84.A);
                return g with { Height = 0 };
            }
        },
    };

    // ================================================================ FUNDAMENTALS
    private static GallerySample SceneGraphBasics() => new()
    {
        Id = "scene-graph",
        Category = SampleCategory.Fundamentals,
        Accent = "#adb5bd",
        Title = new("Scene Graph Fundamentals", "Dasar-dasar Scene Graph"),
        Description = new("Group, transforms, Switch, LOD, state inheritance with OVERRIDE, an occluder wall that culls what is behind it, and a HUD camera.",
                          "Group, transform, Switch, LOD, pewarisan state dengan OVERRIDE, dinding occluder yang meng-cull objek di belakangnya, dan kamera HUD."),
        Tags = ["tutorial", "culling", "HUD"],
        Features = ["Switch", "LOD", "StateSet OVERRIDE", "OccluderNode", "CameraNode HUD"],
        Code = """
            var lod = new Lod();
            lod.AddChild(Shapes.Sphere(1, 64, 32), 0, 30);
            lod.AddChild(Shapes.Sphere(1, 8, 4), 30, 1000);
            var wire = new Group { StateSet = new StateSet { PolygonMode = PolygonMode.Line, Override = true } };
            var occluder = new OccluderNode { Occluder = [new(-6,0,0), new(6,0,0), new(6,0,8), new(-6,0,8)] };
            var hud = new CameraNode { RenderOrder = CameraRenderOrder.PostRender };  // pixel ortho
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(60, Color.FromHex("#495057")));
            Sun(root, new Vector3(-0.3f, 0.6f, -0.8f));

            // LOD spheres
            for (int i = 0; i < 5; i++)
            {
                var lod = new Lod();
                lod.AddChild(Shapes.Sphere(1.2f, 48, 24, Color.FromHex("#4cc9f0")), 0, 25);
                lod.AddChild(Shapes.Sphere(1.2f, 12, 6, Color.FromHex("#f72585")), 25, 60);
                lod.AddChild(Shapes.Sphere(1.2f, 5, 3, Color.FromHex("#ffb703")), 60, float.MaxValue);
                root.AddChild(Place(lod, new Vector3(-12 + i * 6, -10, 1.2f)));
            }
            // switch cycling shapes
            var sw = new Switch();
            sw.AddChild(Place(Shapes.Torus(1.4f, 0.45f, 48, 16, Color.FromHex("#06d6a0")), new Vector3(0, 0, 2)), true);
            sw.AddChild(Place(Shapes.Cone(1.4f, 0, 2.8f, 32, Color.FromHex("#ef476f")), new Vector3(0, 0, 1.4f)), false);
            sw.AddChild(Place(Shapes.Cube(2.2f, Color.FromHex("#118ab2")), new Vector3(0, 0, 1.1f)), false);
            var swHolder = new PositionAttitudeTransform { Position = new Vector3(10, -10, 0) };
            swHolder.AddChild(sw);
            swHolder.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, 45));
            root.AddChild(swHolder);
            // OVERRIDE: everything under 'wire' is wireframe even if children say otherwise
            var wire = new Group { StateSet = new StateSet { PolygonMode = State.PolygonMode.Line, Override = true } };
            var child = Shapes.Sphere(2, 24, 12, Color.FromHex("#ffd166"));
            child.StateSet!.PolygonMode = State.PolygonMode.Fill; // ignored because of the parent's override
            wire.AddChild(child);
            root.AddChild(Place(wire, new Vector3(-10, 6, 2)));
            // occluder wall and hidden boxes behind it
            var wall = new OccluderNode { Occluder = [new(-8, 0, 0), new(8, 0, 0), new(8, 0, 7), new(-8, 0, 7)] };
            wall.AddChild(Place(Shapes.Box(16, 0.4f, 7, Color.FromHex("#adb5bd")), new Vector3(0, 0, 3.5f)));
            root.AddChild(Place(wall, new Vector3(4, 4, 0)));
            for (int i = 0; i < 6; i++) root.AddChild(Place(Shapes.Cube(1.5f, Color.FromHex("#e76f51")), new Vector3(-2 + i * 2, 9, 0.75f)));
            // HUD: a coloured bar in pixel coordinates
            var hud = new CameraNode { RenderOrder = CameraRenderOrder.PostRender };
            var bar = Shapes.Box(240, 10, 1);
            var bss = bar.GetOrCreateStateSet();
            bss.Lighting = false;
            bss.Material = Material.FromColor(0.3f, 0.8f, 1f, 0.85f);
            bss.MakeTransparent();
            bss.DepthTest = false;
            hud.AddChild(Place(bar, new Vector3(140, 24, 0)));
            root.AddChild(hud);

            double next = 2;
            int current = 0;
            v.Updated += (viewer, fs) =>
            {
                if (fs.SimulationTime > next) { next += 2; current = (current + 1) % 3; sw.SetSingleChildOn(current); }
                var st = viewer.LastRenderList.Stats;
                ctx.Telemetry.Set("Draw items", st.DrawItems.ToString());
                ctx.Telemetry.Set("Culled", st.NodesCulled.ToString());
                ctx.Telemetry.Set("Switch child", current.ToString());
                ctx.Telemetry.Set("Nodes visited", st.NodesVisited.ToString());
            };
            ctx.Controls.Add(("Mouse", new("Orbit / pan (Shift) / zoom (wheel)", "Orbit / geser (Shift) / zoom (roda)")));
            ctx.Controls.Add(("Space", new("Home", "Kembali ke awal")));
            v.Environment.Shadows.Enabled = true;
            v.Environment.Shadows.Radius = 30;
            Orbit(v, 42, -20, 30);
            return root;
        },
    };

    private static GallerySample RenderToTexture() => new()
    {
        Id = "render-to-texture",
        Category = SampleCategory.Fundamentals,
        Accent = "#c77dff",
        Title = new("Render to Texture & Shaders", "Render ke Tekstur & Shader"),
        Description = new("A pre-render camera draws a spinning scene into a texture shown on a monitor, next to a sphere using a custom animated GLSL program (desktop renderer).",
                          "Kamera pre-render menggambar scene berputar ke tekstur yang ditampilkan di monitor, di samping bola dengan program GLSL kustom beranimasi (renderer desktop)."),
        Tags = ["RTT", "GLSL", "cameras"],
        Features = ["CameraNode PreRender", "Texture2D render target", "ShaderProgram", "Uniforms"],
        Code = """
            var target = new Texture2D(512, 512);
            var rtt = new CameraNode { RenderOrder = CameraRenderOrder.PreRender, RenderTarget = target,
                                       ViewMatrix = view, ProjectionMatrix = proj, ClearColor = background };
            rtt.AddChild(subScene);
            screen.GetOrCreateStateSet().Texture = target;
            sphere.StateSet.Program = new ShaderProgram { VertexSource = vs, FragmentSource = fs };
            sphere.StateSet.Uniforms.Set("tint", new Vector3(0.8f, 0.3f, 1f));
            """,
        Build = ctx =>
        {
            var v = ctx.Viewer;
            var root = new Group();
            root.AddChild(Ground(40, Color.FromHex("#343a40")));
            Sun(root, new Vector3(-0.3f, 0.5f, -0.8f), 1f, false);

            // the sub-scene rendered into the texture
            var sub = new Group();
            sub.AddChild(new LightSource(Light.Sun(new Vector3(-0.4f, 0.6f, -0.7f), 1.2f, false)));
            var spin = new PositionAttitudeTransform();
            spin.AddChild(Shapes.Torus(1.6f, 0.55f, 48, 20, Color.FromHex("#ffb703")));
            spin.AddChild(Shapes.Sphere(0.7f, 24, 12, Color.FromHex("#4cc9f0")));
            spin.AddUpdateCallback(new SpinCallback(new Vector3(1, 0.4f, 0.2f), 60));
            sub.AddChild(spin);

            var target = new Texture2D(512, 512) { Filter = TextureFilter.Linear };
            root.AddChild(new CameraNode
            {
                RenderOrder = CameraRenderOrder.PreRender,
                RenderTarget = target,
                ViewMatrix = Matrix4x4.CreateLookAt(new Vector3(0, -6, 1.5f), Vector3.Zero, Vector3.UnitZ),
                ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 16f / 9, 0.1f, 100),
                ClearColor = Color.FromHex("#14213d"),
            }.AddChild(sub));

            // monitor: frame + screen quad (GL render targets are bottom-up, so v = 0 is the bottom edge)
            root.AddChild(Place(Shapes.Box(9.4f, 0.4f, 5.4f, Color.FromHex("#212529")), new Vector3(-3, 0, 3.6f)));
            var screen = new Geometry
            {
                Name = "screen",
                Positions = [new(-7.5f, -0.22f, 1.1f), new(1.5f, -0.22f, 1.1f), new(1.5f, -0.22f, 6.1f), new(-7.5f, -0.22f, 6.1f)],
                Normals = [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
                TexCoords = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                Indices = [0, 1, 2, 0, 2, 3],
            };
            var sss = screen.GetOrCreateStateSet();
            sss.Lighting = false;
            sss.CullFace = CullFaceMode.None;
            sss.Material = new Material { Diffuse = Vector4.One };
            sss.Texture = target;
            root.AddChild(screen);

            // custom GLSL: animated bands driven by built-in time and a user uniform
            var sphere = Shapes.Sphere(2.2f, 64, 32, new Vector4(0.75f, 0.25f, 1f, 1f)); // material = fallback where custom programs are not supported
            var ss = sphere.GetOrCreateStateSet();
            ss.Program = new ShaderProgram
            {
                Name = "bands",
                VertexSource = """
                    out vec3 vN;
                    out vec3 vP;
                    void main() {
                        vN = osg_NormalMatrix * osg_Normal;
                        vP = osg_Vertex;
                        gl_Position = osg_ProjectionMatrix * osg_ViewMatrix * osg_ModelMatrix * vec4(osg_Vertex, 1.0);
                    }
                    """,
                FragmentSource = """
                    in vec3 vN;
                    in vec3 vP;
                    uniform vec3 tint;
                    out vec4 fragColor;
                    void main() {
                        float band = 0.5 + 0.5 * sin(vP.z * 6.0 - osg_SimulationTime * 3.0);
                        float light = 0.35 + 0.65 * max(dot(normalize(vN), normalize(vec3(-0.4, -0.6, 0.7))), 0.0);
                        fragColor = vec4(mix(tint, vec3(1.0), band * 0.6) * light, 1.0);
                    }
                    """,
            };
            ss.Uniforms.Set("tint", new Vector3(0.75f, 0.25f, 1f));
            root.AddChild(Place(sphere, new Vector3(5, -1, 2.4f)));

            ctx.Telemetry.Set("Render target", "512 × 512");
            v.Environment.SkyTopColor = Color.FromHex("#1b2536");
            Orbit(v, 24, 8, 12, new Vector3(0, 0, 3.4f));
            return root;
        },
    };
}

/// <summary>Camera that follows a target from behind (used by the vehicle samples).</summary>
public sealed class ChaseCamera(Func<Vector3> target, Func<float> heading, float distance, float height) : ICameraManipulator
{
    private Vector3 _eye;
    private bool _init;
    public string Name => "Chase";
    public float Distance { get; set; } = distance;
    public float Height { get; set; } = height;
    public float Stiffness { get; set; } = 4f;
    public Vector3 Center => target();

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(_eye, target() + new Vector3(0, 0, Height * 0.25f), Vector3.UnitZ);

    public void Home(Math.BoundingSphere bound) => _init = false;

    public bool Handle(in InputEvent e, Viewer viewer)
    {
        if (e.Type == InputEventType.Scroll) { Distance = System.Math.Clamp(Distance * MathF.Exp(-e.ScrollDelta * 0.1f), 3, 2000); return true; }
        return false;
    }

    public void Update(double deltaTime)
    {
        float h = heading() * MathF.PI / 180;
        var desired = target() - new Vector3(MathF.Sin(h), MathF.Cos(h), 0) * Distance + new Vector3(0, 0, Height);
        if (!_init) { _eye = desired; _init = true; }
        _eye = Vector3.Lerp(_eye, desired, 1 - MathF.Exp(-Stiffness * (float)deltaTime));
    }
}
