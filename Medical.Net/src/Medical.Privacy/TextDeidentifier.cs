using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Medical.Core;

namespace Medical.Privacy;

public enum PhiType { Name, Nik, Mrn, Phone, Email, Date, Address, Url, IpAddress, Age, Id }

/// <summary>A detected identifier span in the original text.</summary>
public sealed record PhiSpan(PhiType Type, int Start, int Length, string Value, string Replacement);

public sealed record DeidentifiedText(string Text, IReadOnlyList<PhiSpan> Findings)
{
    public IReadOnlyDictionary<PhiType, int> Counts => Findings.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.Count());
}

public enum ReplacementStyle
{
    /// <summary>[NAME], [DATE]… (irreversible redaction).</summary>
    Tags,
    /// <summary>Stable HMAC pseudonyms and per-subject date shifting (linkable).</summary>
    Pseudonyms,
}

/// <summary>
/// Rule-based PHI detector/redactor for clinical notes in English and Bahasa Indonesia. Detects the HIPAA Safe Harbor
/// identifier classes that appear in free text (names after honorifics or from a known-name list, NIK, MRN, phone, email,
/// dates, street addresses, URLs, IPs, ages &gt; 89). It is a safety net, not a guarantee — review before release.
/// </summary>
public sealed partial class TextDeidentifier
{
    private readonly Pseudonymizer? _pseudo;
    private readonly HashSet<string> _knownNames;

    public TextDeidentifier(string? secret = null, IEnumerable<string>? knownNames = null)
    {
        _pseudo = secret is null ? null : new Pseudonymizer(secret);
        _knownNames = new HashSet<string>(knownNames ?? [], StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b\d{16}\b")] private static partial Regex NikRx();
    [GeneratedRegex(@"\b(?:MRN|RM|No\.?\s?RM|No\.?\s?Rekam\s?Medis)[:\s#-]*([A-Z]{0,4}\d{4,10})\b", RegexOptions.IgnoreCase)] private static partial Regex MrnRx();
    [GeneratedRegex(@"(?:\+62|\b0)8\d{1,3}[\s-]?\d{3,4}[\s-]?\d{3,5}\b|\(\d{3}\)\s?\d{3}-\d{4}\b|\b\d{3}-\d{3}-\d{4}\b")] private static partial Regex PhoneRx();
    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b")] private static partial Regex EmailRx();
    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4})\b")] private static partial Regex NumericDateRx();
    [GeneratedRegex(@"\b\d{1,2}\s+(?:Jan(?:uari|uary)?|Feb(?:ruari|ruary)?|Mar(?:et|ch)?|Apr(?:il)?|Mei|May|Jun(?:i|e)?|Jul(?:i|y)?|Agu(?:stus)?|Aug(?:ust)?|Sep(?:tember)?|Okt(?:ober)?|Oct(?:ober)?|Nov(?:ember)?|Des(?:ember)?|Dec(?:ember)?)\s+\d{4}\b", RegexOptions.IgnoreCase)] private static partial Regex TextDateRx();
    [GeneratedRegex(@"\b(?:Jl\.|Jln\.|Jalan|Gg\.|Gang)\s+[A-Z][\w.]*(?:\s+[A-Z0-9][\w./]*)*(?:\s+No\.?\s*\d+[A-Za-z]?)?(?:\s+RT\s*\d+/?\s*RW\s*\d+)?|\b\d{1,5}\s+[A-Z][a-z]+\s+(?:Street|St\.|Avenue|Ave\.|Road|Rd\.|Lane|Blvd\.?)\b")] private static partial Regex AddressRx();
    [GeneratedRegex(@"\bhttps?://[^\s)]+")] private static partial Regex UrlRx();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")] private static partial Regex IpRx();
    [GeneratedRegex(@"\b(?:9\d|1[0-4]\d)\s?(?:tahun|thn|th|years?|yo|y/o)\b", RegexOptions.IgnoreCase)] private static partial Regex AgeRx();
    [GeneratedRegex(@"\b(?:Tn\.|Ny\.|Nn\.|Sdr\.|Sdri\.|An\.|Bpk\.|Bapak|Ibu|Mr\.|Mrs\.|Ms\.|Mx\.|Pasien|Patient)\s+((?:[A-Z][a-z]+)(?:\s+[A-Z][a-z]+){0,3})")] private static partial Regex HonorificNameRx();

    public DeidentifiedText Deidentify(string text, ReplacementStyle style = ReplacementStyle.Tags, string subjectId = "subject")
    {
        var spans = new List<(PhiType Type, int Start, int Length, string Value)>();
        void Add(Regex rx, PhiType type, int group = 0)
        {
            foreach (Match m in rx.Matches(text))
            {
                var g = m.Groups[group];
                if (g.Success) spans.Add((type, g.Index, g.Length, g.Value));
            }
        }
        Add(UrlRx(), PhiType.Url);
        Add(EmailRx(), PhiType.Email);
        Add(NikRx(), PhiType.Nik);
        Add(MrnRx(), PhiType.Mrn, 1);
        Add(PhoneRx(), PhiType.Phone);
        Add(IpRx(), PhiType.IpAddress);
        Add(TextDateRx(), PhiType.Date);
        Add(NumericDateRx(), PhiType.Date);
        Add(AddressRx(), PhiType.Address);
        Add(AgeRx(), PhiType.Age);
        Add(HonorificNameRx(), PhiType.Name, 1);
        foreach (var name in _knownNames)
            foreach (Match m in Regex.Matches(text, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                spans.Add((PhiType.Name, m.Index, m.Length, m.Value));
        // Names found once are redacted everywhere (e.g. "Tn. Budi … Budi mengeluh …").
        foreach (var token in spans.Where(s => s.Type == PhiType.Name).SelectMany(s => s.Value.Split(' ')).Where(t => t.Length > 2).Distinct().ToArray())
            foreach (Match m in Regex.Matches(text, $@"\b{Regex.Escape(token)}\b"))
                spans.Add((PhiType.Name, m.Index, m.Length, m.Value));

        // Resolve overlaps: earliest start, then longest span wins.
        var chosen = new List<(PhiType Type, int Start, int Length, string Value)>();
        int cursor = -1;
        foreach (var s in spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length))
        {
            if (s.Start < cursor) continue;
            chosen.Add(s);
            cursor = s.Start + s.Length;
        }

        var sb = new StringBuilder(text.Length);
        var findings = new List<PhiSpan>(chosen.Count);
        int pos = 0;
        foreach (var s in chosen)
        {
            sb.Append(text, pos, s.Start - pos);
            string rep = Replacement(s.Type, s.Value, style, subjectId);
            sb.Append(rep);
            findings.Add(new PhiSpan(s.Type, s.Start, s.Length, s.Value, rep));
            pos = s.Start + s.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        return new DeidentifiedText(sb.ToString(), findings);
    }

    private string Replacement(PhiType type, string value, ReplacementStyle style, string subject)
    {
        if (style == ReplacementStyle.Tags || _pseudo is null) return $"[{type.ToString().ToUpperInvariant()}]";
        return type switch
        {
            PhiType.Name => _pseudo.Pseudonym(value.ToLowerInvariant(), "NAMA-", 6),
            PhiType.Date => ShiftDate(value, _pseudo.DateShiftDays(subject)) ?? "[DATE]",
            PhiType.Age => "90+",
            _ => _pseudo.Pseudonym(value, $"{type.ToString().ToUpperInvariant()}-", 8),
        };
    }

    private static string? ShiftDate(string value, int days)
    {
        string[] formats = ["yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yy"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("id-ID"), CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(value, ["d MMMM yyyy", "d MMM yyyy"], culture, DateTimeStyles.None, out d))
                return d.AddDays(days).ToString("d MMMM yyyy", culture);
        return null;
    }
}

/// <summary>Display masking for identifiers shown on screens and printouts.</summary>
public static class Masking
{
    /// <summary>3201********0001 — keeps region and serial, hides the birth date.</summary>
    public static string Nik(string nik) => nik.Length == 16 ? $"{nik[..4]}********{nik[12..]}" : new string('*', nik.Length);

    public static string Phone(string phone) => phone.Length <= 4 ? "****" : new string('*', phone.Length - 4) + phone[^4..];

    public static string Email(string email)
    {
        int at = email.IndexOf('@');
        return at <= 1 ? "***" + email[Math.Max(0, at)..] : $"{email[0]}***{email[(at - 1)..]}";
    }

    public static string Name(HumanName name) => $"{name.Given[0]}. {name.Family[0]}{new string('*', Math.Max(0, name.Family.Length - 1))}";
}
