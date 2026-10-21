using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blazor.AI.ThreeD;

/// <summary>One CAD feature, millimetres. Size is [x, y, z] for boxes; Diameter/Height for cylinders and holes.</summary>
public sealed class CadFeature
{
    public string Name { get; set; } = "Feature";

    /// <summary>box, cylinder or hole (a cut).</summary>
    public string Type { get; set; } = "box";

    public double[] Size { get; set; } = [10, 10, 10];
    public double Diameter { get; set; } = 10;
    public double Height { get; set; } = 10;

    /// <summary>Centre position, mm (Y up).</summary>
    public double[] Position { get; set; } = [0, 0, 0];

    public bool IsCut => Type == "hole";
}

/// <summary>A parametric part made of additive features and cuts.</summary>
public sealed class CadPart
{
    public string Name { get; set; } = "Part";
    public string Source { get; set; } = "local";
    public List<CadFeature> Features { get; set; } = [];

    public double MaxDimension => Features.Count == 0 ? 10 : Features.Max(f => f.Type == "box"
        ? Math.Max(f.Size[0], Math.Max(f.Size[1], f.Size[2])) + Math.Abs(f.Position[0]) + Math.Abs(f.Position[2])
        : Math.Max(f.Diameter, f.Height) + Math.Abs(f.Position[0]) + Math.Abs(f.Position[2]));

    /// <summary>Volume estimate (mm³): additive volume minus cuts.</summary>
    public double Volume => Features.Sum(f => (f.IsCut ? -1 : 1) * (f.Type == "box" ? f.Size[0] * f.Size[1] * f.Size[2] : Math.PI * f.Diameter * f.Diameter / 4 * f.Height));

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

/// <summary>Turns plain-language part descriptions into features without a model (flanges, brackets, plates, shafts, tubes).</summary>
public static partial class CadParser
{
    public static CadPart Parse(string text)
    {
        var t = text.ToLowerInvariant();
        var numbers = Numbers().Matches(t).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        var dims = Dims().Match(t);
        var holes = HoleCount(t);

        if (t.Contains("flange") || t.Contains("flens"))
        {
            var d = numbers.FirstOrDefault(n => n >= 20, 60);
            return Flange(d, holes ?? 6);
        }

        if (t.Contains("shaft") || t.Contains("poros") || t.Contains("rod"))
        {
            var d = numbers.ElementAtOrDefault(0) is > 0 and var a ? a : 20;
            var l = numbers.ElementAtOrDefault(1) is > 0 and var b ? b : 120;
            return new CadPart { Name = $"Shaft Ø{d:0} × {l:0}", Features = [new() { Name = "Shaft", Type = "cylinder", Diameter = d, Height = l, Position = [0, l / 2, 0] }] };
        }

        if (t.Contains("tube") || t.Contains("pipe") || t.Contains("pipa") || t.Contains("bushing"))
        {
            var d = numbers.ElementAtOrDefault(0) is > 0 and var a ? a : 40;
            var l = numbers.ElementAtOrDefault(1) is > 0 and var b ? b : 60;
            return new CadPart
            {
                Name = $"Tube Ø{d:0} × {l:0}",
                Features = [new() { Name = "Tube", Type = "cylinder", Diameter = d, Height = l, Position = [0, l / 2, 0] }, new() { Name = "Bore", Type = "hole", Diameter = d * 0.7, Height = l + 2, Position = [0, l / 2, 0] }],
            };
        }

        double x = 100, z = 40, y = 5;
        if (dims.Success)
        {
            x = Num(dims.Groups[1].Value);
            z = Num(dims.Groups[2].Value);
            y = dims.Groups[3].Success ? Num(dims.Groups[3].Value) : 5;
        }

        var part = new CadPart { Name = t.Contains("bracket") || t.Contains("braket") ? $"Bracket {x:0}×{z:0}×{y:0}" : $"Plate {x:0}×{z:0}×{y:0}" };
        part.Features.Add(new() { Name = "Base", Type = "box", Size = [x, y, z], Position = [0, y / 2, 0] });
        if (t.Contains("l-bracket") || t.Contains("angle") || t.Contains("siku"))
        {
            part.Name = $"L-bracket {x:0}×{z:0}×{y:0}";
            part.Features.Add(new() { Name = "Upright", Type = "box", Size = [y, z * 0.9, z], Position = [-x / 2 + y / 2, y + z * 0.45, 0] });
        }

        var n = holes ?? (t.Contains("hole") || t.Contains("lubang") ? 2 : 0);
        var hd = Math.Min(z * 0.3, Math.Max(3, x / (n + 2) * 0.4));
        for (var i = 0; i < n; i++)
        {
            var px = n == 1 ? 0 : -x / 2 + x * (i + 1) / (n + 1);
            part.Features.Add(new() { Name = $"Hole {i + 1}", Type = "hole", Diameter = Math.Round(hd, 1), Height = y + 2, Position = [px, y / 2, 0] });
        }

        return part;
    }

    public static CadPart Flange(double diameter, int boltHoles)
    {
        var thickness = Math.Round(diameter * 0.15, 1);
        var part = new CadPart { Name = $"Flange Ø{diameter:0} with {boltHoles} bolt holes" };
        part.Features.Add(new() { Name = "Disc", Type = "cylinder", Diameter = diameter, Height = thickness, Position = [0, thickness / 2, 0] });
        part.Features.Add(new() { Name = "Hub", Type = "cylinder", Diameter = diameter * 0.5, Height = thickness * 1.8, Position = [0, thickness * 0.9, 0] });
        part.Features.Add(new() { Name = "Bore", Type = "hole", Diameter = Math.Round(diameter * 0.3, 1), Height = thickness * 2 + 2, Position = [0, thickness, 0] });
        var pcd = diameter * 0.76;
        for (var i = 0; i < boltHoles; i++)
        {
            var a = i / (double)boltHoles * Math.PI * 2;
            part.Features.Add(new() { Name = $"Bolt hole {i + 1}", Type = "hole", Diameter = Math.Round(diameter * 0.09, 1), Height = thickness + 2, Position = [Math.Round(Math.Cos(a) * pcd / 2, 2), thickness / 2, Math.Round(Math.Sin(a) * pcd / 2, 2)] });
        }

        return part;
    }

    /// <summary>Reads a model's JSON feature list; returns null when unusable.</summary>
    public static CadPart? FromJson(JsonNode? json)
    {
        if (json?["features"] is not JsonArray arr)
        {
            return null;
        }

        var part = new CadPart { Name = json["name"]?.ToString() is { Length: > 1 } n ? n : "AI part", Source = "ai" };
        foreach (var f in arr)
        {
            var type = f?["type"]?.ToString()?.ToLowerInvariant() ?? "box";
            if (f?["op"]?.ToString() == "cut" || type == "hole") type = "hole";
            else if (type != "cylinder") type = "box";
            double[] Vec(string key, double[] fallback) => f?[key] is JsonArray a && a.Count == 3 && a.All(x => x is JsonValue) ? a.Select(x => x!.GetValue<double>()).ToArray() : fallback;
            var size = Vec("size", [10, 10, 10]);
            if (size.Any(s => s <= 0 || s > 5000)) continue;
            part.Features.Add(new CadFeature
            {
                Name = f?["name"]?.ToString() ?? type,
                Type = type,
                Size = size,
                Diameter = Math.Clamp(f?["diameter"]?.GetValue<double>() ?? 10, 0.5, 5000),
                Height = Math.Clamp(f?["height"]?.GetValue<double>() ?? 10, 0.5, 5000),
                Position = Vec("position", [0, 0, 0]),
            });
        }

        return part.Features.Any(f => !f.IsCut) ? part : null;
    }

    private static int? HoleCount(string t)
    {
        var m = HoleRegex().Match(t);
        if (!m.Success) return null;
        var w = m.Groups[1].Value;
        return int.TryParse(w, out var n) ? n : w switch
        {
            "one" or "a" or "satu" => 1, "two" or "dua" => 2, "three" or "tiga" => 3, "four" or "empat" => 4,
            "five" or "lima" => 5, "six" or "enam" => 6, "eight" or "delapan" => 8, _ => null,
        };
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)(?:\s*[x×]\s*(\d+(?:\.\d+)?))?")]
    private static partial Regex Dims();

    [GeneratedRegex(@"(\d+|one|two|three|four|five|six|eight|satu|dua|tiga|empat|lima|enam|delapan)\s+(?:bolt\s+)?(?:holes?|lubang)")]
    private static partial Regex HoleRegex();
}
