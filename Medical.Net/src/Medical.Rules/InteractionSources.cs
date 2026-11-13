using System.Globalization;
using System.Xml;
using Medical.Core;

namespace Medical.Rules;

/// <summary>One drug-drug interaction reported by a source.</summary>
public sealed record InteractionFinding(string DrugA, string DrugB, InteractionSeverity Severity, Localized Effect, string Source, string? Reference = null)
{
    public override string ToString() => $"{DrugA} + {DrugB}: {Severity} — {Effect.En} [{Source}]";
}

/// <summary>
/// A drug-interaction knowledge source. Implement this to plug in a licensed database (First Databank, Medi-Span,
/// Lexicomp, DrugBank, a national formulary service…); combine several with <see cref="CompositeInteractionSource"/>.
/// </summary>
public interface IDrugInteractionSource
{
    string Name { get; }

    /// <summary>Interactions between two drugs (generic names), most severe first; empty when none are known.</summary>
    ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string drugA, string drugB, CancellationToken ct = default);
}

/// <summary>Helpers for any source.</summary>
public static class DrugInteractionSourceExtensions
{
    /// <summary>Checks every pair of a medication list.</summary>
    public static async Task<IReadOnlyList<InteractionFinding>> CheckAsync(this IDrugInteractionSource source, IEnumerable<string> drugs, CancellationToken ct = default)
    {
        var list = drugs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = new List<InteractionFinding>();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                all.AddRange(await source.FindAsync(list[i], list[j], ct).ConfigureAwait(false));
        return all.OrderByDescending(f => f.Severity).ToList();
    }
}

/// <summary>The curated class-based rules of <see cref="DrugKnowledge"/> (illustrative).</summary>
public sealed class BuiltInInteractionSource(DrugKnowledge? knowledge = null) : IDrugInteractionSource
{
    private readonly DrugKnowledge _k = knowledge ?? DrugKnowledge.Default;
    public string Name => "Medical.Net built-in";

    public ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string drugA, string drugB, CancellationToken ct = default)
    {
        var i = _k.Interaction(drugA, drugB);
        IReadOnlyList<InteractionFinding> r = i is null ? [] : [new(drugA, drugB, i.Severity, i.Effect, Name)];
        return ValueTask.FromResult(r);
    }
}

/// <summary>
/// A pair table keyed on drug names or groups: each side is a set of generic names. Used by
/// <see cref="LabelContraindications"/> and by <see cref="CsvInteractionSource"/>.
/// </summary>
public class TableInteractionSource : IDrugInteractionSource
{
    private readonly List<(HashSet<string> A, HashSet<string> B, InteractionSeverity Severity, Localized Effect, string? Reference)> _rows = [];

    public TableInteractionSource(string name) => Name = name;
    public string Name { get; }
    public int Count => _rows.Count;

    public TableInteractionSource Add(IEnumerable<string> a, IEnumerable<string> b, InteractionSeverity severity, Localized effect, string? reference = null)
    {
        _rows.Add((new(a.Select(Norm), StringComparer.OrdinalIgnoreCase), new(b.Select(Norm), StringComparer.OrdinalIgnoreCase), severity, effect, reference));
        return this;
    }

    public ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string drugA, string drugB, CancellationToken ct = default)
    {
        string a = Norm(drugA), b = Norm(drugB);
        IReadOnlyList<InteractionFinding> r = _rows
            .Where(x => (x.A.Contains(a) && x.B.Contains(b)) || (x.A.Contains(b) && x.B.Contains(a)))
            .OrderByDescending(x => x.Severity)
            .Select(x => new InteractionFinding(drugA, drugB, x.Severity, x.Effect, Name, x.Reference)).ToList();
        return ValueTask.FromResult(r);
    }

    internal static string Norm(string s) => s.Trim().ToLowerInvariant();
}

/// <summary>
/// Combinations that US prescribing information lists as contraindicated or to be avoided — the interactions a
/// prescriber must never miss. Each row cites its label section.
/// </summary>
public static class LabelContraindications
{
    private static readonly string[] StrongCyp3A4Inhibitors = ["clarithromycin", "itraconazole", "ketoconazole", "posaconazole", "voriconazole", "ritonavir", "cobicistat", "nefazodone", "telithromycin"];
    private static readonly string[] Maois = ["phenelzine", "tranylcypromine", "isocarboxazid", "selegiline", "rasagiline", "linezolid", "methylene blue"];
    private static readonly string[] Serotonergic = ["fluoxetine", "sertraline", "paroxetine", "citalopram", "escitalopram", "fluvoxamine", "venlafaxine", "duloxetine", "tramadol", "meperidine", "pethidine", "methadone", "fentanyl", "dextromethorphan", "bupropion"];
    private static readonly string[] Nitrates = ["nitroglycerin", "glyceryl trinitrate", "isosorbide dinitrate", "isosorbide mononitrate", "riociguat"];

    /// <summary>The built-in label table.</summary>
    public static TableInteractionSource Source { get; } = Create();

    private static TableInteractionSource Create() => new TableInteractionSource("FDA label contraindications")
        .Add(["simvastatin", "lovastatin"], StrongCyp3A4Inhibitors, InteractionSeverity.Contraindicated,
            new("Strong CYP3A4 inhibitor raises statin exposure many-fold — myopathy / rhabdomyolysis.", "Penghambat kuat CYP3A4 menaikkan kadar statin berlipat — miopati / rabdomiolisis."), "Zocor / Mevacor label §4")
        .Add(["ergotamine", "dihydroergotamine"], StrongCyp3A4Inhibitors, InteractionSeverity.Contraindicated,
            new("Ergot toxicity (vasospasm, ischaemia) with strong CYP3A4 inhibitors.", "Toksisitas ergot (vasospasme, iskemia) dengan penghambat kuat CYP3A4."), "Ergotamine label boxed warning")
        .Add(["pimozide"], ["clarithromycin", "erythromycin", "itraconazole", "ketoconazole", "ritonavir", "fluoxetine", "sertraline", "paroxetine", "citalopram", "escitalopram"], InteractionSeverity.Contraindicated,
            new("QT prolongation and torsades de pointes (raised pimozide levels or additive QT).", "Pemanjangan QT dan torsades de pointes."), "Orap (pimozide) label §4")
        .Add(["tizanidine"], ["fluvoxamine", "ciprofloxacin"], InteractionSeverity.Contraindicated,
            new("CYP1A2 inhibition raises tizanidine ~10–30-fold — severe hypotension and sedation.", "Penghambatan CYP1A2 menaikkan tizanidin ~10–30 kali — hipotensi berat dan sedasi."), "Zanaflex label §4")
        .Add(["ramelteon"], ["fluvoxamine"], InteractionSeverity.Contraindicated,
            new("Fluvoxamine raises ramelteon exposure ~190-fold.", "Fluvoksamin menaikkan kadar ramelteon ~190 kali."), "Rozerem label §7")
        .Add(Maois, Serotonergic, InteractionSeverity.Contraindicated,
            new("MAO inhibitor with a serotonergic drug — serotonin syndrome; observe the washout period.", "Penghambat MAO dengan obat serotonergik — sindrom serotonin; patuhi masa washout."), "SSRI / SNRI labels §4")
        .Add(["sildenafil", "tadalafil", "vardenafil", "avanafil"], Nitrates, InteractionSeverity.Contraindicated,
            new("PDE5 inhibitor with a nitrate / sGC stimulator — profound hypotension.", "Penghambat PDE5 dengan nitrat — hipotensi berat."), "Viagra / Cialis label §4")
        .Add(["dofetilide"], ["verapamil", "cimetidine", "trimethoprim", "ketoconazole", "hydrochlorothiazide", "dolutegravir", "megestrol", "prochlorperazine"], InteractionSeverity.Contraindicated,
            new("Raised dofetilide levels — QT prolongation and torsades.", "Kadar dofetilid naik — pemanjangan QT dan torsades."), "Tikosyn label §4")
        .Add(["colchicine"], StrongCyp3A4Inhibitors, InteractionSeverity.Major,
            new("Colchicine toxicity; contraindicated with renal or hepatic impairment, otherwise reduce the dose.", "Toksisitas kolkisin; kontraindikasi bila ada gangguan ginjal / hati, selain itu turunkan dosis."), "Colcrys label §4, §7")
        .Add(["azathioprine", "mercaptopurine"], ["allopurinol", "febuxostat"], InteractionSeverity.Major,
            new("Xanthine-oxidase inhibition — myelosuppression; reduce the thiopurine to one-third or one-quarter (febuxostat: contraindicated).", "Penghambatan xantin oksidase — mielosupresi; turunkan tiopurin menjadi 1/3–1/4 dosis."), "Imuran label §7; Uloric label §4")
        .Add(["methotrexate"], ["trimethoprim", "sulfamethoxazole", "co-trimoxazole", "cotrimoxazole"], InteractionSeverity.Major,
            new("Additive antifolate effect — bone-marrow suppression.", "Efek antifolat aditif — supresi sumsum tulang."), "Methotrexate label §7")
        .Add(["atazanavir"], ["omeprazole", "esomeprazole", "lansoprazole", "pantoprazole", "rabeprazole"], InteractionSeverity.Major,
            new("PPIs markedly reduce atazanavir absorption — loss of virological response.", "PPI menurunkan absorpsi atazanavir — kegagalan virologis."), "Reyataz label §7")
        .Add(["clopidogrel"], ["omeprazole", "esomeprazole"], InteractionSeverity.Moderate,
            new("CYP2C19 inhibition reduces clopidogrel activation — avoid; pantoprazole is an alternative.", "Penghambatan CYP2C19 menurunkan aktivasi klopidogrel — hindari; pantoprazol sebagai alternatif."), "Plavix label §7");
}

/// <summary>
/// Interaction table from a CSV file — the usual export format of licensed databases and national formularies.
/// Header (case-insensitive): <c>drug_a, drug_b, severity, effect_en</c> and optionally <c>effect_id, reference</c>.
/// Severity: contraindicated / major / moderate (also "minor", which maps to moderate). A drug cell may hold several
/// names separated by '|'.
/// </summary>
public sealed class CsvInteractionSource : TableInteractionSource
{
    private CsvInteractionSource(string name) : base(name) { }

    public static CsvInteractionSource Load(string path, string? name = null)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, name ?? Path.GetFileNameWithoutExtension(path));
    }

    public static CsvInteractionSource Parse(TextReader reader, string name)
    {
        var src = new CsvInteractionSource(name);
        var header = SplitCsv(reader.ReadLine() ?? throw new InvalidDataException("Empty interaction CSV.")).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n, bool required = true)
        {
            int i = header.IndexOf(n);
            if (i < 0 && required) throw new InvalidDataException($"Interaction CSV is missing the '{n}' column.");
            return i;
        }
        int a = Col("drug_a"), b = Col("drug_b"), sev = Col("severity"), en = Col("effect_en"), id = Col("effect_id", false), rf = Col("reference", false);
        string? line;
        int lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            var severity = Get(sev).ToLowerInvariant() switch
            {
                "contraindicated" or "contraindication" or "x" => InteractionSeverity.Contraindicated,
                "major" or "severe" or "high" or "d" => InteractionSeverity.Major,
                "moderate" or "minor" or "low" or "c" or "b" => InteractionSeverity.Moderate,
                var s => throw new InvalidDataException($"Line {lineNo}: unknown severity '{s}'."),
            };
            string effectEn = Get(en);
            src.Add(Get(a).Split('|'), Get(b).Split('|'), severity, new(effectEn, id >= 0 && Get(id).Length > 0 ? Get(id) : effectEn), rf >= 0 && Get(rf).Length > 0 ? Get(rf) : null);
        }
        return src;
    }

    /// <summary>RFC 4180 field splitting (quoted fields, doubled quotes).</summary>
    internal static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        res.Add(sb.ToString());
        return res;
    }
}

/// <summary>
/// Reads the <c>drug-interactions</c> of a DrugBank full-database XML file (licensed separately from DrugBank; the
/// file is not distributed with Medical.Net). The XML has no severity grade, so findings are reported as
/// <see cref="InteractionSeverity.Moderate"/> unless the description names a contraindication or a serious outcome.
/// Streams the file, so the full database loads without building a DOM; pass <c>only</c> to keep
/// just the drugs of a formulary.
/// </summary>
public sealed class DrugBankInteractionSource : IDrugInteractionSource
{
    private readonly Dictionary<(string, string), string> _pairs = [];
    public string Name => "DrugBank";
    public int Count => _pairs.Count;

    private DrugBankInteractionSource() { }

    public static DrugBankInteractionSource Load(string path, IEnumerable<string>? only = null)
    {
        using var s = File.OpenRead(path);
        return Load(s, only);
    }

    public static DrugBankInteractionSource Load(Stream xml, IEnumerable<string>? only = null)
    {
        var keep = only?.Select(TableInteractionSource.Norm).ToHashSet();
        var src = new DrugBankInteractionSource();
        using var r = XmlReader.Create(xml, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit });
        int depth = -1;
        string? current = null;
        bool advance = true;
        while (!advance || r.Read())
        {
            advance = true;
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.LocalName == "drug" && (depth < 0 || r.Depth == depth))
            {
                depth = r.Depth;
                current = null;
                continue;
            }
            if (r.Depth == depth + 1 && r.LocalName == "name" && current is null)
            {
                current = TableInteractionSource.Norm(r.ReadElementContentAsString());
                advance = false;   // the reader already stands on the next node
                continue;
            }
            if (r.LocalName == "drug-interaction" && current is not null)
            {
                string? other = null, desc = null;
                using (var sub = r.ReadSubtree())
                {
                    bool subAdvance = true;
                    while (!subAdvance || sub.Read())
                    {
                        subAdvance = true;
                        if (sub.NodeType != XmlNodeType.Element) continue;
                        if (sub.LocalName == "name") { other = TableInteractionSource.Norm(sub.ReadElementContentAsString()); subAdvance = false; }
                        else if (sub.LocalName == "description") { desc = sub.ReadElementContentAsString(); subAdvance = false; }
                    }
                }
                if (other is null || desc is null) continue;
                if (keep is not null && (!keep.Contains(current) || !keep.Contains(other))) continue;
                src._pairs.TryAdd(Key(current, other), desc);
            }
        }
        return src;
    }

    private static (string, string) Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    public ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string drugA, string drugB, CancellationToken ct = default)
    {
        IReadOnlyList<InteractionFinding> r = _pairs.TryGetValue(Key(TableInteractionSource.Norm(drugA), TableInteractionSource.Norm(drugB)), out var d)
            ? [new(drugA, drugB, Grade(d), new(d, d), Name)]
            : [];
        return ValueTask.FromResult(r);
    }

    private static InteractionSeverity Grade(string d)
    {
        string s = d.ToLowerInvariant();
        if (s.Contains("contraindicated")) return InteractionSeverity.Contraindicated;
        string[] serious = ["qtc", "torsade", "serotonin syndrome", "rhabdomyolysis", "bleeding and hemorrhage", "hemorrhage", "respiratory depression", "myelosuppression", "hyperkalemia"];
        return serious.Any(s.Contains) ? InteractionSeverity.Major : InteractionSeverity.Moderate;
    }
}

/// <summary>
/// Queries several sources and merges the answers: one finding per source, ordered by severity, so a licensed
/// database's grade is shown next to the built-in rules. A failing source is reported in <see cref="Errors"/> rather
/// than failing the whole check.
/// </summary>
public sealed class CompositeInteractionSource(params IDrugInteractionSource[] sources) : IDrugInteractionSource
{
    private readonly List<string> _errors = [];
    public string Name => string.Join(" + ", sources.Select(s => s.Name));
    public IReadOnlyList<IDrugInteractionSource> Sources => sources;

    /// <summary>Errors from the most recent queries (source name: message).</summary>
    public IReadOnlyList<string> Errors { get { lock (_errors) return _errors.ToList(); } }

    /// <summary>Built-in rules plus the label contraindications.</summary>
    public static CompositeInteractionSource Default() => new(new BuiltInInteractionSource(), LabelContraindications.Source);

    public async ValueTask<IReadOnlyList<InteractionFinding>> FindAsync(string drugA, string drugB, CancellationToken ct = default)
    {
        var all = new List<InteractionFinding>();
        foreach (var s in sources)
        {
            try { all.AddRange(await s.FindAsync(drugA, drugB, ct).ConfigureAwait(false)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lock (_errors) _errors.Add($"{s.Name}: {e.Message}");
            }
        }
        return all.GroupBy(f => f.Source).Select(g => g.OrderByDescending(f => f.Severity).First())
            .OrderByDescending(f => f.Severity).ToList();
    }

    /// <summary>The most severe grade any source gives a pair (null when none reports it).</summary>
    public async ValueTask<InteractionSeverity?> WorstAsync(string drugA, string drugB, CancellationToken ct = default)
    {
        var f = await FindAsync(drugA, drugB, ct).ConfigureAwait(false);
        return f.Count == 0 ? null : f.Max(x => x.Severity);
    }
}
