using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blazor.AI.Core.Simulated;

/// <summary>Regex and keyword extraction of form values, used by the simulated SmartPaste.</summary>
internal static partial class FieldHeuristics
{
    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:\+?\d{1,3}[\s\-.]?)?(?:\(?\d{2,4}\)?[\s\-.]?)?\d{3,4}[\s\-.]?\d{3,5}", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b(\d{4})-(\d{2})-(\d{2})\b|\b(\d{1,2})[/\.](\d{1,2})[/\.](\d{4})\b|\b(\d{1,2})\s+(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec|Mei|Agu|Okt|Des)[a-z]*\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Date();

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b\d{5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCode();

    public static JsonObject Extract(string text, JsonArray fields)
    {
        var result = new JsonObject();
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var field in fields.OfType<JsonObject>())
        {
            var name = field["name"]?.ToString() ?? string.Empty;
            var type = field["type"]?.ToString() ?? "string";
            var description = field["description"]?.ToString() ?? string.Empty;
            var allowed = (field["allowed"] as JsonArray)?.Select(a => a?.ToString() ?? string.Empty).Where(a => a.Length > 0).ToArray();
            var hint = (name + " " + description).ToLowerInvariant();

            var value = ExtractOne(text, lines, hint, type, allowed);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static string? ExtractOne(string text, string[] lines, string hint, string type, string[]? allowed)
    {
        if (allowed is { Length: > 0 })
        {
            return allowed.FirstOrDefault(a => text.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        // A labelled line ("Phone: 0812...") beats pattern matching.
        var labelled = Labelled(lines, hint);

        if (type == "email" || hint.Contains("email") || hint.Contains("e-mail"))
        {
            return Email().Match(labelled ?? text) is { Success: true } m ? m.Value : Email().Match(text) is { Success: true } m2 ? m2.Value : null;
        }

        if (type == "phone" || hint.Contains("phone") || hint.Contains("telp") || hint.Contains("mobile") || hint.Contains("hp") || hint.Contains("whatsapp"))
        {
            var source = labelled ?? text;
            return Phone().Matches(source).Select(m => m.Value.Trim()).FirstOrDefault(v => v.Count(char.IsDigit) >= 8);
        }

        if (type == "date" || hint.Contains("date") || hint.Contains("tanggal") || hint.Contains("birth") || hint.Contains("lahir"))
        {
            return Date().Match(labelled ?? text) is { Success: true } m ? NormalizeDate(m) : null;
        }

        if (hint.Contains("url") || hint.Contains("website") || hint.Contains("link"))
        {
            return Url().Match(text) is { Success: true } m ? m.Value.TrimEnd('.', ',') : null;
        }

        if (hint.Contains("postal") || hint.Contains("zip") || hint.Contains("kode pos"))
        {
            return PostalCode().Match(labelled ?? text) is { Success: true } m ? m.Value : null;
        }

        if (type == "boolean")
        {
            if (labelled is null)
            {
                return null;
            }

            var lower = labelled.ToLowerInvariant();
            return lower.Contains("yes") || lower.Contains("ya") || lower.Contains("true") ? "true" : lower.Contains("no") || lower.Contains("tidak") ? "false" : null;
        }

        if (type == "number" || hint.Contains("amount") || hint.Contains("price") || hint.Contains("qty") || hint.Contains("quantity") || hint.Contains("jumlah") || hint.Contains("age"))
        {
            var source = labelled ?? LineMentioning(lines, hint);
            return source is not null && Number().Match(source) is { Success: true } m ? m.Value.Replace(',', '.') : null;
        }

        if (labelled is not null)
        {
            return labelled;
        }

        if (hint.Contains("name") || hint.Contains("nama"))
        {
            return GuessName(text, hint);
        }

        if (hint.Contains("address") || hint.Contains("alamat") || hint.Contains("street"))
        {
            return lines.FirstOrDefault(l => AddressCues.Any(c => l.Contains(c, StringComparison.OrdinalIgnoreCase)));
        }

        if (hint.Contains("city") || hint.Contains("kota"))
        {
            return KnownCities.FirstOrDefault(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
        }

        if (hint.Contains("company") || hint.Contains("organization") || hint.Contains("perusahaan"))
        {
            return lines.Select(l => CompanyPattern().Match(l)).FirstOrDefault(m => m.Success)?.Value;
        }

        if (hint.Contains("subject") || hint.Contains("title") || hint.Contains("judul"))
        {
            return lines.FirstOrDefault();
        }

        if (hint.Contains("message") || hint.Contains("description") || hint.Contains("notes") || hint.Contains("issue") || hint.Contains("keterangan"))
        {
            return TextHeuristics.Summarize(text, 2);
        }

        return null;
    }

    [GeneratedRegex(@"\b(?:PT\.?|CV\.?)\s+[A-Z][\w&]*(?:\s+[A-Z][\w&]*)*|\b[A-Z][\w&]*(?:\s+[A-Z][\w&]*)*\s+(?:Inc|Ltd|LLC|Corp|GmbH|Tbk)\.?", RegexOptions.CultureInvariant)]
    private static partial Regex CompanyPattern();

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProperName();

    private static readonly string[] AddressCues = ["jl.", "jalan", "street", "st.", "road", "rd.", "avenue", "ave", "blok", "no.", "rt ", "rw "];

    private static readonly string[] KnownCities =
    [
        "Jakarta", "Bandung", "Surabaya", "Yogyakarta", "Semarang", "Medan", "Makassar", "Denpasar", "Bogor", "Depok", "Bekasi",
        "Tangerang", "Malang", "Palembang", "Singapore", "Kuala Lumpur", "London", "New York", "Seattle", "San Francisco",
        "Tokyo", "Sydney", "Berlin", "Paris", "Amsterdam", "Toronto", "Chicago", "Boston",
    ];

    private static readonly string[] NameStopWords = ["Dear", "Hello", "Hi", "Regards", "Thanks", "Best", "Street", "Road", "Jalan"];

    private static string? GuessName(string text, string hint)
    {
        var candidates = ProperName().Matches(text).Select(m => m.Value)
            .Where(v => !NameStopWords.Any(s => v.StartsWith(s, StringComparison.Ordinal)) && !KnownCities.Contains(v))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var full = candidates[0];
        var parts = full.Split(' ');
        if (hint.Contains("first"))
        {
            return parts[0];
        }

        if (hint.Contains("last") || hint.Contains("surname"))
        {
            return parts[^1];
        }

        return full;
    }

    private static string? Labelled(string[] lines, string hint)
    {
        var keys = hint.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Where(k => k.Length > 2).ToArray();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || colon > 40)
            {
                continue;
            }

            var label = line[..colon].ToLowerInvariant();
            if (keys.Any(k => label.Contains(k, StringComparison.Ordinal)))
            {
                var value = line[(colon + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? LineMentioning(string[] lines, string hint)
    {
        var keys = hint.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Where(k => k.Length > 2).ToArray();
        return lines.FirstOrDefault(l => keys.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeDate(Match m)
    {
        if (m.Groups[1].Success)
        {
            return m.Value;
        }

        if (m.Groups[4].Success)
        {
            return $"{m.Groups[6].Value}-{int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture):00}-{int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture):00}";
        }

        var month = Array.FindIndex(Months, mo => m.Groups[8].Value.StartsWith(mo, StringComparison.OrdinalIgnoreCase)) % 12 + 1;
        return $"{m.Groups[9].Value}-{month:00}-{int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture):00}";
    }

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "Mei", "Jun", "Jul", "Agu", "Sep", "Okt", "Nov", "Des"];
}
