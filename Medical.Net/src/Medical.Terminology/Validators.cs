using System.Text.RegularExpressions;

namespace Medical.Terminology;

/// <summary>Structural validation of codes and identifiers (format + check digits).</summary>
public static partial class CodeValidators
{
    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Icd10Regex();

    [GeneratedRegex(@"^[0-9]{1,7}-[0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex LoincRegex();

    /// <summary>ICD-10 / ICD-10-CM code shape: letter, two digits, optional dot and up to 4 characters (e.g. E11.9, I10, S72.001A).</summary>
    public static bool IsIcd10Format(string code) => Icd10Regex().IsMatch(code.Trim().ToUpperInvariant());

    /// <summary>LOINC code with valid mod-10 check digit (e.g. 2345-7).</summary>
    public static bool IsValidLoinc(string code)
    {
        code = code.Trim();
        if (!LoincRegex().IsMatch(code)) return false;
        int dash = code.IndexOf('-');
        return LoincCheckDigit(code.AsSpan(0, dash)) == code[dash + 1] - '0';
    }

    /// <summary>Computes the LOINC mod-10 (Luhn) check digit of the numeric part.</summary>
    public static int LoincCheckDigit(ReadOnlySpan<char> digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return (10 - sum % 10) % 10;
    }

    private static readonly int[,] VerhoeffD =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 }, { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 },
        { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 }, { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 },
        { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 }, { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] VerhoeffP =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 }, { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 }, { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    /// <summary>
    /// SNOMED CT identifier (SCTID): 6–18 digits, Verhoeff check digit, and a valid partition identifier
    /// (00 concept, 01 description, 02 relationship; 10/11/12 for extension namespaces).
    /// </summary>
    public static bool IsValidSctid(string id)
    {
        id = id.Trim();
        if (id.Length is < 6 or > 18 || id[0] == '0' || !id.All(char.IsAsciiDigit)) return false;
        string partition = id.Substring(id.Length - 3, 2);
        if (partition is not ("00" or "01" or "02" or "10" or "11" or "12")) return false;
        int c = 0;
        for (int i = 0; i < id.Length; i++) c = VerhoeffD[c, VerhoeffP[i % 8, id[id.Length - 1 - i] - '0']];
        return c == 0;
    }

    /// <summary>True when the SCTID identifies a concept (partition 00 or 10).</summary>
    public static bool IsConceptSctid(string id) => IsValidSctid(id) && id[^3..^1] is "00" or "10";

    /// <summary>
    /// Indonesian NIK (Nomor Induk Kependudukan): 16 digits = province(2) regency(2) district(2) DDMMYY (DD + 40 for women) serial(4).
    /// Returns the encoded birth date and sex when structurally valid.
    /// </summary>
    public static bool TryParseNik(string nik, out DateOnly birthDate, out bool female)
    {
        birthDate = default; female = false;
        nik = nik.Trim();
        if (nik.Length != 16 || !nik.All(char.IsAsciiDigit)) return false;
        int day = int.Parse(nik.AsSpan(6, 2)), month = int.Parse(nik.AsSpan(8, 2)), yy = int.Parse(nik.AsSpan(10, 2));
        if (day > 40) { female = true; day -= 40; }
        int year = yy + (yy > DateTime.UtcNow.Year % 100 ? 1900 : 2000);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        if (nik.AsSpan(12, 4).SequenceEqual("0000")) return false;
        birthDate = new DateOnly(year, month, day);
        return true;
    }
}
