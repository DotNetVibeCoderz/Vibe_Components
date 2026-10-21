using System.Text;
using System.Text.RegularExpressions;
using Blazor.AI.Abstractions.Security;

namespace Blazor.AI.Core.Security;

/// <summary>
/// Detects common PII with compiled, source-generated regexes: emails, phone numbers, payment cards (Luhn-validated),
/// Indonesian NIK (16 digits), IBANs and IPv4 addresses.
/// </summary>
public sealed partial class RegexPiiDetector : IPiiDetector
{
    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:\d[ \-]?){13,19}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\b\d{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NikPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){2,7}(?:\s?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpPattern();

    [GeneratedRegex(@"(?<!\w)(?:\+\d{1,3}[\s\-.]?)?(?:\(\d{2,4}\)[\s\-.]?|\d{2,4}[\s\-.])?\d{3,4}[\s\-.]\d{3,5}(?!\w)|(?<!\w)(?:\+62|08)\d{8,12}(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    public IReadOnlyList<PiiMatch> Detect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var matches = new List<PiiMatch>();
        Add(matches, EmailPattern(), text, PiiKind.Email);
        foreach (Match m in CardPattern().Matches(text))
        {
            var digits = Digits(m.Value);
            if (digits.Length is >= 13 and <= 19 && Luhn(digits))
            {
                matches.Add(new PiiMatch(PiiKind.CreditCard, m.Index, m.Length, m.Value));
            }
        }

        Add(matches, NikPattern(), text, PiiKind.NationalId);
        Add(matches, IbanPattern(), text, PiiKind.Iban);
        Add(matches, IpPattern(), text, PiiKind.IpAddress);
        foreach (Match m in PhonePattern().Matches(text))
        {
            if (Digits(m.Value).Length is >= 9 and <= 15)
            {
                matches.Add(new PiiMatch(PiiKind.Phone, m.Index, m.Length, m.Value));
            }
        }

        // Keep the earliest, longest, non-overlapping matches (a card number must not also count as a phone).
        matches.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var result = new List<PiiMatch>(matches.Count);
        var end = -1;
        foreach (var match in matches)
        {
            if (match.Start >= end)
            {
                result.Add(match);
                end = match.Start + match.Length;
            }
        }

        return result;
    }

    public string Redact(string text)
    {
        var matches = Detect(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text, position, match.Start - position).Append(Placeholder(match.Kind));
            position = match.Start + match.Length;
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }

    public static string Placeholder(PiiKind kind) => kind switch
    {
        PiiKind.Email => "[EMAIL]",
        PiiKind.Phone => "[PHONE]",
        PiiKind.CreditCard => "[CARD]",
        PiiKind.NationalId => "[NATIONAL_ID]",
        PiiKind.Iban => "[IBAN]",
        PiiKind.IpAddress => "[IP]",
        _ => "[PII]",
    };

    private static void Add(List<PiiMatch> matches, Regex regex, string text, PiiKind kind)
    {
        foreach (Match m in regex.Matches(text))
        {
            matches.Add(new PiiMatch(kind, m.Index, m.Length, m.Value));
        }
    }

    private static string Digits(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                buffer[n++] = c;
            }
        }

        return new string(buffer[..n]);
    }

    internal static bool Luhn(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }
}
