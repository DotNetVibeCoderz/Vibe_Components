using System.Globalization;

namespace Blazor.AI.ThreeD;

/// <summary>
/// glTF models and 2-D art shipped in the package (static web assets). Generated with Rodin and rigged/animated in
/// Blender for Blazor.AI; free to use in apps built with this library.
/// </summary>
public static class ThreeDAssets
{
    private const string Root = "_content/Blazor.AI.ThreeD/";

    /// <summary>Rigged guide robot. Clips: Idle, Wave, Talk, Nod, Point, Walk, Happy, Think. Morphs on the visor voice bar: Talk, Smile, Sad.</summary>
    public const string GuideBot = Root + "models/guide-bot.glb";

    /// <summary>Rigged fish with a looping "Swim" clip; 1 m long, facing +Z.</summary>
    public const string Fish = Root + "models/fish.glb";

    /// <summary>Industrial robot arm with a "PickPlace" clip (crate pick, turn 160°, place).</summary>
    public const string RobotArm = Root + "models/robot-arm.glb";

    /// <summary>Anatomical heart, 0.25 m tall.</summary>
    public const string Heart = Root + "models/heart.glb";

    /// <summary>Borobudur-style stupa, 3 m tall.</summary>
    public const string Stupa = Root + "models/stupa.glb";

    /// <summary>Young maize plant, 1.6 m tall.</summary>
    public const string Corn = Root + "models/corn.glb";

    /// <summary>Compact city car, 4 m long, facing +Z.</summary>
    public const string Car = Root + "models/car.glb";

    /// <summary>Neutral anatomy mannequin, 1.75 m tall, facing +Z.</summary>
    public const string Mannequin = Root + "models/mannequin.glb";

    public const string HeroArt = Root + "art/hero-3d.webp";
    public const string WorldConcept = Root + "art/world-concept.webp";
    public const string NpcBlacksmith = Root + "art/npc-blacksmith.webp";
    public const string NpcMerchant = Root + "art/npc-merchant.webp";
    public const string NpcGuardian = Root + "art/npc-guardian.webp";
}

/// <summary>The kawung batik palette used by all 3D components, tuned to read on dark and light backgrounds.</summary>
public static class ThreeDPalette
{
    public const string Night = "#0b0f29";
    public const string Tarum = "#3d4fb8";
    public const string TarumDeep = "#1a2052";
    public const string Soga = "#d9964f";
    public const string Nila = "#3cc4b1";
    public const string Danger = "#e5534b";
    public const string Warning = "#e8b84a";
    public const string Ink = "#e8ebff";
    public const string Muted = "#8a8fb0";
    public const string Leaf = "#5fae5a";
    public const string Water = "#2f86c9";

    /// <summary>Distinct, colour-blind-aware categorical colours.</summary>
    public static readonly IReadOnlyList<string> Categorical =
        ["#d9964f", "#3cc4b1", "#7b8cff", "#e5534b", "#c45ad6", "#e8b84a", "#5fae5a", "#2f86c9", "#f07aa8", "#9aa3c7"];

    public static string Category(int index) => Categorical[((index % Categorical.Count) + Categorical.Count) % Categorical.Count];

    /// <summary>Sequential nila→soga→danger ramp for values in [0, 1].</summary>
    public static string Heat(double t)
    {
        t = Math.Clamp(double.IsFinite(t) ? t : 0, 0, 1);
        (double r, double g, double b) a = t < 0.5 ? (0x3c, 0xc4, 0xb1) : (0xd9, 0x96, 0x4f);
        (double r, double g, double b) z = t < 0.5 ? (0xd9, 0x96, 0x4f) : (0xe5, 0x53, 0x4b);
        var k = t < 0.5 ? t * 2 : (t - 0.5) * 2;
        return Hex(a.r + (z.r - a.r) * k, a.g + (z.g - a.g) * k, a.b + (z.b - a.b) * k);
    }

    /// <summary>Cool→warm ramp (deep blue → teal → amber) for values in [0, 1], used by surfaces and fields.</summary>
    public static string Ramp(double t)
    {
        t = Math.Clamp(double.IsFinite(t) ? t : 0, 0, 1);
        (double, double, double)[] stops = [(0x1a, 0x2a, 0x8f), (0x2f, 0x86, 0xc9), (0x3c, 0xc4, 0xb1), (0xe8, 0xb8, 0x4a), (0xe5, 0x53, 0x4b)];
        var x = t * (stops.Length - 1);
        var i = Math.Min((int)x, stops.Length - 2);
        var k = x - i;
        var (r0, g0, b0) = stops[i];
        var (r1, g1, b1) = stops[i + 1];
        return Hex(r0 + (r1 - r0) * k, g0 + (g1 - g0) * k, b0 + (b1 - b0) * k);
    }

    public static string Hex(double r, double g, double b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(int)Math.Round(r):x2}{(int)Math.Round(g):x2}{(int)Math.Round(b):x2}");
}
