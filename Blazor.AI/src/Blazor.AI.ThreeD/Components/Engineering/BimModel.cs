using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blazor.AI.ThreeD;

/// <summary>A BIM element: an axis-aligned box with type, level and properties (metres).</summary>
public sealed record BimElement(string Id, string Type, int Level, string Name, double[] Center, double[] Size, string Material, string FireRating)
{
    public double Volume => Size[0] * Size[1] * Size[2];

    /// <summary>Face area used in take-offs: walls/doors/windows use their elevation area, slabs their plan area.</summary>
    public double Area => Type switch
    {
        "slab" => Size[0] * Size[2],
        "column" => Size[0] * Size[2],
        _ => Math.Max(Size[0], Size[2]) * Size[1],
    };

    public bool Intersects(BimElement o, double tolerance = 0.005)
    {
        for (var a = 0; a < 3; a++)
        {
            if (Math.Abs(Center[a] - o.Center[a]) >= (Size[a] + o.Size[a]) / 2 - tolerance)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A structured query over the model, produced by the model or the keyword parser.</summary>
public sealed record BimQuery(string Action, string? Type, int? Level, string? Material)
{
    public override string ToString() => $"{Action} {Type ?? "any"}{(Level is { } l ? $" on level {l}" : string.Empty)}{(Material is null ? string.Empty : $" of {Material}")}";
}

/// <summary>Sample two-storey building and the query engine.</summary>
public static partial class BimModel
{
    public static readonly string[] Types = ["wall", "door", "window", "column", "slab", "duct"];

    public static List<BimElement> SampleBuilding()
    {
        var list = new List<BimElement>();
        const double w = 12, d = 8, h = 3.2, t = 0.2;
        for (var level = 1; level <= 2; level++)
        {
            var y0 = (level - 1) * h;
            list.Add(new($"L{level}-slab", "slab", level, $"Floor slab L{level}", [0, y0 + 0.1, 0], [w, 0.2, d], "Concrete C30", "REI 120"));
            list.Add(new($"L{level}-wall-n", "wall", level, "North wall", [0, y0 + h / 2 + 0.1, -d / 2], [w, h - 0.2, t], "Brick", "EI 60"));
            list.Add(new($"L{level}-wall-s", "wall", level, "South wall", [0, y0 + h / 2 + 0.1, d / 2], [w, h - 0.2, t], "Brick", "EI 60"));
            list.Add(new($"L{level}-wall-e", "wall", level, "East wall", [w / 2, y0 + h / 2 + 0.1, 0], [t, h - 0.2, d], "Brick", "EI 60"));
            list.Add(new($"L{level}-wall-w", "wall", level, "West wall", [-w / 2, y0 + h / 2 + 0.1, 0], [t, h - 0.2, d], "Brick", "EI 60"));
            list.Add(new($"L{level}-wall-i", "wall", level, "Partition", [1, y0 + h / 2 + 0.1, 0], [0.12, h - 0.2, d - 0.4], "Gypsum", "EI 30"));
            foreach (var (cx, cz) in new[] { (-w / 2 + 0.3, -d / 2 + 0.3), (w / 2 - 0.3, -d / 2 + 0.3), (-w / 2 + 0.3, d / 2 - 0.3), (w / 2 - 0.3, d / 2 - 0.3), (-1.5, 0) })
            {
                list.Add(new($"L{level}-col-{list.Count}", "column", level, "Column 40×40", [cx, y0 + h / 2 + 0.1, cz], [0.4, h - 0.2, 0.4], "Concrete C30", "R 120"));
            }

            var doors = level == 1 ? new[] { (-3.0, d / 2, false), (3.0, d / 2, false), (1.0, 2.0, true) } : [(1.0, -2.0, true), (1.0, 2.0, true)];
            foreach (var (dx, dz, interior) in doors)
            {
                list.Add(new($"L{level}-door-{list.Count}", "door", level, interior ? "Interior door" : "Entrance door",
                    interior ? [dx, y0 + 1.15, dz] : [dx, y0 + 1.15, dz], interior ? [0.14, 2.1, 0.9] : [1.0, 2.1, 0.24], interior ? "Timber" : "Steel", interior ? "EI 30" : "EI 60"));
            }

            for (var i = 0; i < 3; i++)
            {
                var x = -4 + i * 4;
                list.Add(new($"L{level}-win-n{i}", "window", level, "Window 1.6×1.4", [x, y0 + 1.7, -d / 2], [1.6, 1.4, 0.24], "Glass", "-"));
                list.Add(new($"L{level}-win-s{i}", "window", level, "Window 1.2×1.2", [x + (level == 1 ? 1.2 : 0), y0 + 1.8, d / 2], [1.2, 1.2, 0.24], level == 1 && i == 1 ? "Glass" : "Glass", "-"));
            }
        }

        // An HVAC duct that runs through a column on level 2: a clash to find.
        list.Add(new("L2-duct-1", "duct", 2, "Supply duct 600×300", [-1.5, 3.2 + 2.6, 0], [9, 0.3, 0.6], "Galvanised steel", "-"));
        list.Add(new("L1-duct-1", "duct", 1, "Return duct 400×250", [2.5, 2.6, -2.5], [5, 0.25, 0.4], "Galvanised steel", "-"));
        return list;
    }

    /// <summary>Keyword parser (English and Indonesian) used offline and to validate model output.</summary>
    public static BimQuery Parse(string question)
    {
        var q = question.ToLowerInvariant();
        string? type = null;
        if (q.Contains("door") || q.Contains("pintu")) type = "door";
        else if (q.Contains("window") || q.Contains("jendela")) type = "window";
        else if (q.Contains("column") || q.Contains("kolom")) type = "column";
        else if (q.Contains("slab") || q.Contains("floor") || q.Contains("pelat")) type = "slab";
        else if (q.Contains("duct") || q.Contains("hvac") || q.Contains("saluran")) type = "duct";
        else if (q.Contains("wall") || q.Contains("dinding") || q.Contains("tembok")) type = "wall";
        var level = LevelRegex().Match(q) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        string? material = null;
        foreach (var mat in new[] { "concrete", "brick", "glass", "timber", "steel", "gypsum" })
        {
            if (q.Contains(mat)) material = mat;
        }

        var action = q.Contains("clash") || q.Contains("bentrok") ? "clash"
            : q.Contains("area") || q.Contains("luas") ? "area"
            : q.Contains("volume") ? "volume"
            : q.Contains("how many") || q.Contains("count") || q.Contains("berapa") || q.Contains("number of") ? "count"
            : "list";
        return new BimQuery(action, type, level, material);
    }

    public static BimQuery? FromJson(JsonNode? json)
    {
        var action = json?["action"]?.ToString()?.ToLowerInvariant();
        if (action is not ("count" or "area" or "volume" or "list" or "clash"))
        {
            return null;
        }

        var type = json!["type"]?.ToString()?.ToLowerInvariant();
        var level = json["level"] is JsonValue v && v.TryGetValue<double>(out var l) && l is >= 1 and <= 50 ? (int)l : (int?)null;
        var material = json["material"]?.ToString() is { Length: > 2 } m ? m.ToLowerInvariant() : null;
        return new BimQuery(action, Types.Contains(type) ? type : null, level, material);
    }

    public static IReadOnlyList<BimElement> Select(IEnumerable<BimElement> all, BimQuery q) =>
        all.Where(e => (q.Type is null || e.Type == q.Type) && (q.Level is null || e.Level == q.Level) &&
                       (q.Material is null || e.Material.Contains(q.Material, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Hard clashes between building services and structure/architecture (doors and windows inside walls are intended).</summary>
    public static List<(BimElement A, BimElement B)> Clashes(IReadOnlyList<BimElement> all)
    {
        var result = new List<(BimElement, BimElement)>();
        foreach (var duct in all.Where(e => e.Type == "duct"))
        {
            foreach (var e in all.Where(e => e.Type is "column" or "wall" or "slab" && e.Level == duct.Level))
            {
                if (duct.Intersects(e))
                {
                    result.Add((duct, e));
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?:level|lantai|floor|lt\.?)\s*(\d+)")]
    private static partial Regex LevelRegex();
}
