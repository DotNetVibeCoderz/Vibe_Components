using System.Text;
using Medical.Core;

namespace Medical.Terminology;

/// <summary>Loads the curated subsets embedded in the assembly.</summary>
internal static class EmbeddedTerminology
{
    public static TerminologyService Create()
    {
        var t = new TerminologyService();
        t.Register(Load(CodeSystems.Icd10, "ICD-10", "2010", "icd10", r => new Concept
        {
            System = CodeSystems.Icd10, Code = r[0], Display = r[1], DisplayId = Opt(r, 2), Category = r[0][..1],
        }));
        t.Register(Load(CodeSystems.Loinc, "LOINC", "2.80", "loinc", r => new Concept
        {
            System = CodeSystems.Loinc, Code = r[0], Display = r[1], DisplayId = Opt(r, 2), Category = Opt(r, 3), Unit = Opt(r, 4),
        }));
        t.Register(Load(CodeSystems.Snomed, "SNOMED CT", "INT sample", "snomed", r => new Concept
        {
            System = CodeSystems.Snomed, Code = r[0], Display = r[1], Category = Opt(r, 2), DisplayId = Opt(r, 3),
            Parents = Opt(r, 4)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], MapTarget = Opt(r, 5),
        }));
        t.Register(Load(CodeSystems.RxNorm, "RxNorm", "ingredients sample", "rxnorm", r => new Concept
        {
            System = CodeSystems.RxNorm, Code = r[0], Display = r[1], Category = Opt(r, 2),
            Tags = Opt(r, 3)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [], DisplayId = Opt(r, 4),
        }));
        return t;
    }

    private static string? Opt(string[] r, int i) => i < r.Length && r[i].Length > 0 ? r[i] : null;

    private static CodeSystem Load(string uri, string name, string version, string resource, Func<string[], Concept> map)
    {
        var cs = new CodeSystem(uri, name, version);
        using var s = typeof(EmbeddedTerminology).Assembly.GetManifestResourceStream($"Medical.Terminology.{resource}.tsv")
                      ?? throw new InvalidOperationException($"Embedded terminology '{resource}' missing.");
        using var reader = new StreamReader(s, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            cs.Add(map(line.Split('\t')));
        }
        return cs;
    }
}

/// <summary>
/// Loaders for official full releases (not redistributed with Medical.Net because of their licences):
/// SNOMED CT RF2 snapshot, LOINC <c>Loinc.csv</c>, CMS ICD-10-CM order file and RxNorm <c>RXNCONSO.RRF</c>.
/// </summary>
public static class ReleaseLoaders
{
    private const string FsnType = "900000000000003001";
    private const string IsA = "116680003";

    /// <summary>
    /// SNOMED CT RF2 snapshot: <c>sct2_Description_Snapshot-en_*.txt</c> (+ optional <c>sct2_Relationship_Snapshot_*.txt</c> for is-a).
    /// Uses the FSN when no synonym is preferred; the semantic tag is taken from the FSN's trailing parentheses.
    /// </summary>
    public static CodeSystem LoadSnomedRf2(string descriptionFile, string? relationshipFile = null, string version = "RF2")
    {
        var fsn = new Dictionary<string, string>();
        var syn = new Dictionary<string, string>();
        foreach (var f in ReadTsv(descriptionFile, skipHeader: true))
        {
            // id effectiveTime active moduleId conceptId languageCode typeId term caseSignificanceId
            if (f.Length < 9 || f[2] != "1") continue;
            if (f[6] == FsnType) fsn[f[4]] = f[7];
            else syn.TryAdd(f[4], f[7]);
        }
        var parents = new Dictionary<string, List<string>>();
        if (relationshipFile is not null)
            foreach (var f in ReadTsv(relationshipFile, skipHeader: true))
            {
                // id effectiveTime active moduleId sourceId destinationId relationshipGroup typeId characteristicTypeId modifierId
                if (f.Length < 8 || f[2] != "1" || f[7] != IsA) continue;
                if (!parents.TryGetValue(f[4], out var l)) parents[f[4]] = l = [];
                l.Add(f[5]);
            }
        var cs = new CodeSystem(CodeSystems.Snomed, "SNOMED CT", version);
        foreach (var (id, full) in fsn)
        {
            int open = full.LastIndexOf(" (", StringComparison.Ordinal);
            string? tag = open > 0 && full.EndsWith(')') ? full[(open + 2)..^1] : null;
            string term = syn.GetValueOrDefault(id) ?? (open > 0 ? full[..open] : full);
            cs.Add(new Concept { System = CodeSystems.Snomed, Code = id, Display = term, Category = tag, Parents = parents.TryGetValue(id, out var p) ? p : [] });
        }
        return cs;
    }

    /// <summary>LOINC table <c>Loinc.csv</c> (quoted CSV with a header containing LOINC_NUM, LONG_COMMON_NAME, CLASS, EXAMPLE_UCUM_UNITS).</summary>
    public static CodeSystem LoadLoincCsv(string path, string version = "LOINC")
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = ParseCsvLine(reader.ReadLine() ?? throw new InvalidDataException("Empty LOINC file."));
        int iNum = Array.IndexOf(header, "LOINC_NUM"), iName = Array.IndexOf(header, "LONG_COMMON_NAME"), iClass = Array.IndexOf(header, "CLASS"),
            iUnit = Array.IndexOf(header, "EXAMPLE_UCUM_UNITS"), iStatus = Array.IndexOf(header, "STATUS");
        if (iNum < 0 || iName < 0) throw new InvalidDataException("Loinc.csv must contain LOINC_NUM and LONG_COMMON_NAME columns.");
        var cs = new CodeSystem(CodeSystems.Loinc, "LOINC", version);
        while (reader.ReadLine() is { } line)
        {
            var f = ParseCsvLine(line);
            if (f.Length <= Math.Max(iNum, iName) || (iStatus >= 0 && f[iStatus] is "DEPRECATED")) continue;
            cs.Add(new Concept
            {
                System = CodeSystems.Loinc, Code = f[iNum], Display = f[iName],
                Category = iClass >= 0 ? f[iClass] : null, Unit = iUnit >= 0 && f[iUnit].Length > 0 ? f[iUnit].Split(';')[0] : null,
            });
        }
        return cs;
    }

    /// <summary>CMS ICD-10-CM order file (<c>icd10cm_order_YYYY.txt</c>, fixed width). Only billable codes are loaded unless <paramref name="includeHeaders"/>.</summary>
    public static CodeSystem LoadIcd10CmOrderFile(string path, bool includeHeaders = false, string version = "ICD-10-CM")
    {
        var cs = new CodeSystem(CodeSystems.Icd10Cm, "ICD-10-CM", version);
        foreach (var line in File.ReadLines(path))
        {
            // order(5) sp code(7) sp header(1) sp short(60) sp long
            if (line.Length < 77) continue;
            string raw = line.Substring(6, 7).Trim();
            bool billable = line[14] == '1';
            if (!billable && !includeHeaders) continue;
            string code = raw.Length > 3 ? $"{raw[..3]}.{raw[3..]}" : raw;
            cs.Add(new Concept { System = CodeSystems.Icd10Cm, Code = code, Display = line[77..].Trim(), Category = raw[..1] });
        }
        return cs;
    }

    /// <summary>RxNorm <c>RXNCONSO.RRF</c> — loads SAB=RXNORM atoms of the given term types (default ingredients and clinical drugs).</summary>
    public static CodeSystem LoadRxNormRrf(string path, string[]? termTypes = null, string version = "RxNorm")
    {
        var tty = new HashSet<string>(termTypes ?? ["IN", "SCD", "SBD"]);
        var cs = new CodeSystem(CodeSystems.RxNorm, "RxNorm", version);
        foreach (var line in File.ReadLines(path))
        {
            // RXCUI|LAT|TS|LUI|STT|SUI|ISPREF|RXAUI|SAUI|SCUI|SDUI|SAB|TTY|CODE|STR|SRL|SUPPRESS|CVF
            var f = line.Split('|');
            if (f.Length < 15 || f[11] != "RXNORM" || !tty.Contains(f[12]) || f[16] is "O" or "Y") continue;
            if (!cs.Contains(f[0])) cs.Add(new Concept { System = CodeSystems.RxNorm, Code = f[0], Display = f[14], Category = f[12] });
        }
        return cs;
    }

    private static IEnumerable<string[]> ReadTsv(string path, bool skipHeader)
    {
        bool first = true;
        foreach (var line in File.ReadLines(path))
        {
            if (first && skipHeader) { first = false; continue; }
            first = false;
            yield return line.Split('\t');
        }
    }

    /// <summary>RFC 4180 CSV line parser (quotes, escaped quotes).</summary>
    public static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return [.. result];
    }
}
