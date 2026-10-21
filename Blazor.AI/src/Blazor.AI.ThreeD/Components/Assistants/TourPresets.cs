namespace Blazor.AI.ThreeD;

/// <summary>A stop on a tour: where the guide stands, what it points at, and the facts it narrates from.</summary>
public sealed record TourStop(string Name, double[] Stand, double[] Focus, string Facts);

/// <summary>A tour: its scenery and ordered stops.</summary>
public sealed record TourPreset(string Name, string Icon, string Ground, IReadOnlyList<Object3D> Scenery, IReadOnlyList<TourStop> Stops);

/// <summary>Built-in tours: museum (Borobudur), smart city, campus, factory and tourism.</summary>
public static class TourPresets
{
    public static IReadOnlyList<string> Names { get; } = ["Museum", "Smart City", "Campus", "Factory", "Tourism"];

    public static TourPreset Get(string name) => name switch
    {
        "Smart City" => SmartCity(),
        "Campus" => Campus(),
        "Factory" => Factory(),
        "Tourism" => Tourism(),
        _ => Museum(),
    };

    private static double[] P(double x, double y, double z) => [x, y, z];

    public static TourPreset Museum()
    {
        var scenery = new List<Object3D>
        {
            new() { Id = "terrace1", Kind = "box", Size = [12, 0.4, 12], Position = P(0, 0.2, -2), Color = "#6f6a63", Roughness = 0.95, Pickable = false },
            new() { Id = "terrace2", Kind = "box", Size = [9, 0.4, 9], Position = P(0, 0.6, -2), Color = "#7b766e", Roughness = 0.95, Pickable = false },
            new() { Id = "main", Kind = "model", Url = ThreeDAssets.Stupa, FitHeight = 4.2, Position = P(0, 0.8, -2), Tooltip = "Main stupa: the summit of Borobudur" },
        };
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            scenery.Add(new Object3D { Id = $"bell{i}", Kind = "model", Url = ThreeDAssets.Stupa, FitHeight = 1.5, Position = P(Math.Cos(a) * 3.6, 0.8, -2 + Math.Sin(a) * 3.6), Tooltip = "Perforated bell stupa with a seated Buddha inside" });
        }

        for (var i = 0; i < 5; i++)
        {
            scenery.Add(new Object3D { Id = $"relief{i}", Kind = "box", Size = [1.8, 1.1, 0.12], Position = P(-4 + i * 2, 0.95, 4.3), Color = "#8a7f70", Edges = "#c9b99f", Roughness = 0.9, Tooltip = "Relief panel from the Lalitavistara story" });
        }

        return new TourPreset("Museum", "bi-bank", "#3f4a3a", scenery,
        [
            new("Entrance gate", P(0, 0, 6.5), P(0, 1.2, 4.3), "Borobudur, Magelang, Central Java. Built in the 8th–9th century under the Sailendra dynasty. Largest Buddhist temple in the world; UNESCO World Heritage Site since 1991."),
            new("Relief galleries", P(-3, 0, 5.4), P(-3, 1, 4.3), "2,672 relief panels line the galleries, telling stories such as the Lalitavistara (life of the Buddha) and the Karmawibhangga (cause and effect). Pilgrims walk clockwise for about 5 km."),
            new("Bell stupas", P(4.4, 0.8, 0.5), P(3.6, 1.6, -2), "72 perforated bell-shaped stupas on the round terraces each hold a seated Buddha statue. The lattice shows form emerging from formlessness."),
            new("Main stupa", P(1.4, 0.8, 1.2), P(0, 3.2, -2), "The central stupa crowns the monument, symbolising enlightenment (Arupadhatu). The whole temple is a mandala built from about 55,000 m³ of andesite stone without mortar."),
            new("Sunrise viewpoint", P(-5.2, 0.4, -5.5), P(0, 2, -2), "At sunrise Mount Merapi and Mount Merbabu appear behind the stupas. The temple was restored by Indonesia and UNESCO between 1975 and 1982."),
        ]);
    }

    public static TourPreset SmartCity()
    {
        var scenery = new List<Object3D>
        {
            new() { Id = "road-x", Kind = "box", Size = [22, 0.02, 2], Position = P(0, 0.01, 0), Color = "#23263a", Pickable = false, ReceiveShadow = true },
            new() { Id = "road-z", Kind = "box", Size = [2, 0.02, 22], Position = P(0, 0.012, 0), Color = "#23263a", Pickable = false },
            new() { Id = "river", Kind = "box", Size = [22, 0.03, 1.6], Position = P(0, 0.015, 7.5), Color = ThreeDPalette.Water, Opacity = 0.85, Roughness = 0.1, Metalness = 0.3, Tooltip = "Ciliwung river with flood sensors" },
        };
        var rng = new Random(5);
        var b = 0;
        foreach (var (cx, cz) in new[] { (-5.5, -5.5), (5.5, -5.5), (-5.5, 4), (5.5, 3.5) })
        {
            for (var i = 0; i < 4; i++)
            {
                var h = 1.5 + rng.NextDouble() * 5;
                var x = cx + (i % 2 == 0 ? -1.5 : 1.5);
                var z = cz + (i < 2 ? -1.3 : 1.3);
                scenery.Add(new Object3D { Id = $"bld{b++}", Kind = "box", Size = [2, h, 2], Position = P(x, h / 2, z), Color = rng.NextDouble() > 0.5 ? "#2c3570" : "#3a4688", Emissive = "#ffd27a", EmissiveIntensity = 0.06 + rng.NextDouble() * 0.1, Edges = "#7b8cff", Tooltip = $"Building {b}: {(h * 3.2):0} m" });
            }
        }

        double[][] loop = [P(-1.2, 0, -9), P(-1.2, 0, 9), P(1.2, 0, 9), P(1.2, 0, -9)];
        for (var c = 0; c < 3; c++)
        {
            scenery.Add(new Object3D { Id = $"car{c}", Kind = "model", Url = ThreeDAssets.Car, FitSize = 1.3, Animate = Animate3D.Path(loop, 2.2 + c * 0.4, true) with { Phase = c * 4 }, Tooltip = "Connected EV reporting speed and position" });
        }

        scenery.Add(new Object3D { Id = "charger", Kind = "cylinder", Radius = 0.25, Height = 1.2, Position = P(3, 0.6, -1.8), Color = ThreeDPalette.Nila, Emissive = ThreeDPalette.Nila, EmissiveIntensity = 0.6, Tooltip = "EV fast charger, 120 kW" });
        scenery.Add(new Object3D { Id = "sensor", Kind = "cone", Radius = 0.25, Height = 0.8, Position = P(-3, 0.4, 6.4), Color = ThreeDPalette.Warning, Emissive = ThreeDPalette.Warning, EmissiveIntensity = 0.5, Animate = Animate3D.Bob(0.08, 1.5), Tooltip = "Water-level sensor: 1.4 m (normal)" });
        scenery.Add(new Object3D { Id = "park", Kind = "cylinder", Radius = 2.2, Height = 0.06, Position = P(-5.5, 0.03, 4), Color = ThreeDPalette.Leaf, Pickable = false });

        return new TourPreset("Smart City", "bi-buildings", "#191c2e", scenery,
        [
            new("Traffic control centre", P(2.2, 0, 2.2), P(0, 0.5, 0), "Adaptive traffic lights at this junction use camera counts every 5 seconds; average wait fell from 94 s to 61 s. 1,200 connected vehicles report position anonymously."),
            new("EV charging hub", P(3, 0, -0.6), P(3, 0.8, -1.8), "Six 120 kW chargers powered partly by rooftop solar. Charging is shifted to midday when solar output peaks; utilisation is 63%."),
            new("Flood early warning", P(-2.2, 0, 5.2), P(-3, 0.6, 7.5), "River sensors measure water level every minute. Above 2.5 m the city sends alerts and reroutes traffic; warnings now arrive 40 minutes earlier than before."),
            new("Smart park", P(-3.4, 0, 2.4), P(-5.5, 0.5, 4), "Soil-moisture sensors water only when needed, saving 35% of water. Air-quality stations publish PM2.5 every 10 minutes on the open-data portal."),
        ]);
    }

    public static TourPreset Campus()
    {
        var scenery = new List<Object3D>();
        (string Name, double X, double Z, double W, double H, double D, string Color)[] buildings =
        [
            ("Library", -5, -4, 4, 3, 3, "#5b4a8a"), ("Engineering Lab", 5, -4, 4.5, 4, 3, "#2f5d8a"), ("Auditorium", -5, 4, 4, 2.4, 4, "#8a5a2f"), ("Student Centre", 5, 4, 4, 2.2, 3, "#2f8a6a"),
        ];
        var i = 0;
        foreach (var bd in buildings)
        {
            scenery.Add(new Object3D { Id = $"b{i++}", Kind = "box", Size = [bd.W, bd.H, bd.D], Position = P(bd.X, bd.H / 2, bd.Z), Color = bd.Color, Edges = "#ffffff", Roughness = 0.6, Label = bd.Name, LabelClass = "is-plain", Tooltip = bd.Name });
        }

        scenery.Add(new Object3D { Id = "plaza", Kind = "cylinder", Radius = 2.4, Height = 0.05, Position = P(0, 0.025, 0), Color = "#c9c2b0", Pickable = false });
        scenery.Add(new Object3D { Id = "fountain", Kind = "cylinder", Radius = 0.6, Height = 0.5, Position = P(0, 0.25, 0), Color = ThreeDPalette.Water, Emissive = ThreeDPalette.Water, EmissiveIntensity = 0.3, Tooltip = "Fountain plaza" });
        for (var t = 0; t < 10; t++)
        {
            var a = t * Math.PI / 5;
            scenery.Add(new Object3D { Id = $"tree{t}", Kind = "cone", Radius = 0.5, Height = 1.6, Position = P(Math.Cos(a) * 8.5, 0.8, Math.Sin(a) * 8.5), Color = ThreeDPalette.Leaf, Pickable = false });
        }

        return new TourPreset("Campus", "bi-mortarboard", "#24402c", scenery,
        [
            new("Fountain plaza", P(1.6, 0, 1.6), P(0, 0.5, 0), "The central plaza connects all faculties; 18,000 students and 1,100 lecturers. Free Wi-Fi covers the whole campus."),
            new("Library", P(-3.5, 0, -1.8), P(-5, 1.5, -4), "Open 24 hours during exams, 450,000 books and 60,000 e-journals. An AI search assistant answers questions in Bahasa Indonesia and English."),
            new("Engineering Lab", P(3.4, 0, -1.8), P(5, 2, -4), "Robotics, renewable energy and IoT labs. Students build flood sensors and smart-farm prototypes with local partners."),
            new("Auditorium", P(-3.4, 0, 1.7), P(-5, 1.2, 4), "Seats 1,200 people; hosts graduation, seminars and the annual innovation expo."),
            new("Student Centre", P(3.4, 0, 1.8), P(5, 1.1, 4), "Clubs, canteen and the career office. More than 70 student organisations meet here."),
        ]);
    }

    public static TourPreset Factory()
    {
        var scenery = new List<Object3D>
        {
            new() { Id = "floor-lines", Kind = "box", Size = [14, 0.01, 0.12], Position = P(0, 0.01, 2.2), Color = ThreeDPalette.Warning, Pickable = false },
            new() { Id = "conveyor", Kind = "box", Size = [12, 0.6, 1.1], Position = P(0, 0.3, -1.2), Color = "#3a3f5c", Edges = "#8f9bd9", Tooltip = "Conveyor line 1: 42 boxes per minute" },
            new() { Id = "control", Kind = "box", Size = [3, 2.4, 2.2], Position = P(-6, 1.2, 4.5), Color = "#2c3570", Edges = "#7b8cff", Emissive = ThreeDPalette.Nila, EmissiveIntensity = 0.1, Label = "Control room", LabelClass = "is-plain", Tooltip = "Control room with the digital twin dashboard" },
            new() { Id = "qa", Kind = "box", Size = [1.4, 1.6, 1.4], Position = P(5.6, 0.8, -1.2), Color = "#5a3a6e", Edges = "#c45ad6", Tooltip = "Vision quality inspection station" },
        };
        for (var r = 0; r < 3; r++)
        {
            scenery.Add(new Object3D { Id = $"arm{r}", Kind = "model", Url = ThreeDAssets.RobotArm, FitHeight = 1.7, Position = P(-3.5 + r * 3.5, 0, -2.9), Rotation = [0, Math.PI, 0], Clip = "PickPlace", ClipSpeed = 0.8 + r * 0.1, Tooltip = $"Robot arm R{r + 1}: pick-and-place, OEE 87%" });
        }

        for (var c = 0; c < 5; c++)
        {
            scenery.Add(new Object3D { Id = $"crate{c}", Kind = "box", Size = [0.35, 0.35, 0.35], Color = "#e8dcc4", Animate = Animate3D.Path([P(-6, 0.78, -1.2), P(6, 0.78, -1.2)], 1.2, true) with { Phase = c * 2.4 }, Pickable = false });
        }

        return new TourPreset("Factory", "bi-gear-wide-connected", "#20222e", scenery,
        [
            new("Line overview", P(0, 0, 2.6), P(0, 0.6, -1.2), "Line 1 assembles battery packs. Throughput 42 units per minute, overall equipment effectiveness 84% this week."),
            new("Robot cell", P(-2, 0, 0.6), P(-3.5, 1, -2.9), "Three 6-axis arms pick and place cells. Vibration sensors predict bearing wear; the last unplanned stop was 23 days ago."),
            new("Quality inspection", P(4.2, 0, 0.6), P(5.6, 0.9, -1.2), "Cameras inspect every pack; an AI model flags weld defects with 98.6% recall. Defect rate is 0.4%."),
            new("Control room", P(-4, 0, 3.2), P(-6, 1.2, 4.5), "The digital twin mirrors every machine in real time. Operators ask the AI copilot questions such as why OEE dropped on night shift."),
        ]);
    }

    public static TourPreset Tourism()
    {
        var scenery = new List<Object3D>
        {
            new() { Id = "sea", Kind = "box", Size = [24, 0.05, 8], Position = P(0, 0.02, 8), Color = "#2f86c9", Opacity = 0.9, Roughness = 0.08, Metalness = 0.2, Tooltip = "Indian Ocean" },
            new() { Id = "beach", Kind = "box", Size = [24, 0.04, 2.5], Position = P(0, 0.03, 3.3), Color = "#e6d3a3", Pickable = false },
            new() { Id = "temple", Kind = "model", Url = ThreeDAssets.Stupa, FitHeight = 3, Position = P(-6, 0, -4), Tooltip = "Hilltop temple" },
            new() { Id = "boat", Kind = "box", Size = [0.6, 0.3, 2], Position = P(4, 0.15, 7), Color = "#8a5a2f", Animate = Animate3D.Bob(0.06, 1.2), Tooltip = "Traditional jukung outrigger boat" },
            new() { Id = "sail", Kind = "cone", Radius = 0.5, Height = 1.4, Position = P(4, 1, 7), Color = "#f2efe6", Animate = Animate3D.Bob(0.06, 1.2), Pickable = false },
        };
        for (var t = 0; t < 5; t++)
        {
            var w = 9 - t * 1.4;
            scenery.Add(new Object3D { Id = $"terrace{t}", Kind = "box", Size = [w, 0.35, 1.6], Position = P(4.5, 0.18 + t * 0.35, -2 - t * 1.4), Color = t % 2 == 0 ? "#5fae5a" : "#6fbf5f", Pickable = false });
        }

        for (var m = 0; m < 3; m++)
        {
            scenery.Add(new Object3D { Id = $"stall{m}", Kind = "box", Size = [1.4, 1, 1], Position = P(-2 + m * 1.8, 0.5, 0.2), Color = "#7a4a2a", Tooltip = "Market stall: coffee, spices and batik" });
            scenery.Add(new Object3D { Id = $"canopy{m}", Kind = "cone", Radius = 1.1, Height = 0.6, Segments = 4, Position = P(-2 + m * 1.8, 1.3, 0.2), Color = ThreeDPalette.Category(m), Pickable = false });
        }

        return new TourPreset("Tourism", "bi-sun", "#3a5a34", scenery,
        [
            new("Beach", P(1.5, 0, 3.6), P(4, 0.4, 7), "A west-facing beach famous for sunsets. Lifeguards are on duty 07:00–18:00; swim between the red and yellow flags."),
            new("Rice terraces", P(2.5, 0, 0.8), P(4.5, 1, -4), "The terraces use the subak irrigation system, a thousand-year-old cooperative water system recognised by UNESCO in 2012."),
            new("Village market", P(-0.2, 0, 2), P(0, 0.8, 0.2), "Local farmers sell coffee, cloves and batik. Paying with QRIS is accepted at most stalls."),
            new("Hilltop temple", P(-4, 0, -1.6), P(-6, 1.6, -4), "A sacred temple: wear a sarong and sash, speak quietly, and do not climb the shrine structures."),
        ]);
    }
}
