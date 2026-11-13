using System.Globalization;
using System.Text;
using Medical.Core;
using Medical.Terminology;

namespace Medical.AI.Nlp;

/// <summary>Kinds of clinical entity.</summary>
public enum ClinicalEntityType { Problem, Symptom, Drug, Test, Procedure, Anatomy, Other }

/// <summary>An entity found in clinical text, with its codes and context (negated / family history).</summary>
public sealed record ClinicalEntity(ClinicalEntityType Type, string Text, int Start, int End, double Score, IReadOnlyList<ClinicalCode> Codes, string Source)
{
    /// <summary>Asserted absent ("no fever", "tidak demam").</summary>
    public bool Negated { get; init; }
    /// <summary>Refers to a relative, not the patient ("family history of diabetes", "riwayat keluarga").</summary>
    public bool FamilyHistory { get; init; }
    /// <summary>The model's raw label, for model-based recognizers.</summary>
    public string? Label { get; init; }

    public override string ToString() => $"{Type} '{Text}' [{Start}..{End}){(Negated ? " negated" : "")}{(FamilyHistory ? " family" : "")}{(Codes.Count > 0 ? " " + string.Join(", ", Codes.Select(c => $"{CodeSystems.ShortName(c.System)} {c.Code}")) : "")}";
}

/// <summary>Finds clinical entities in text.</summary>
public interface IEntityRecognizer
{
    string Name { get; }
    IReadOnlyList<ClinicalEntity> Recognize(string text);
}

/// <summary>
/// Dictionary-based recognizer over the terminology (ICD-10, SNOMED CT, RxNorm, LOINC displays in English and Bahasa
/// Indonesia) plus colloquial EN / ID synonyms ("sesak napas", "darah tinggi", "kencing manis"). Longest match wins;
/// negation and family history are detected with NegEx-style trigger phrases in both languages. Deterministic and
/// offline; combine with a transformer recognizer (Medical.HuggingFace) for recall on free text.
/// </summary>
public sealed class TerminologyEntityRecognizer : IEntityRecognizer
{
    private sealed class Node
    {
        public Dictionary<string, Node> Next { get; } = [];
        public List<(ClinicalEntityType Type, ClinicalCode Code)> Hits { get; } = [];
    }

    private readonly Node _root = new();
    private readonly TerminologyService _terms;

    public string Name => "terminology";

    public TerminologyEntityRecognizer(TerminologyService? terms = null)
    {
        _terms = terms ?? TerminologyService.Default;
        foreach (var (uri, type) in new[] { (CodeSystems.Icd10, (ClinicalEntityType?)null), (CodeSystems.Snomed, null), (CodeSystems.RxNorm, ClinicalEntityType.Drug), (CodeSystems.Loinc, ClinicalEntityType.Test) })
        {
            if (_terms.System(uri) is not { } cs) continue;
            foreach (var c in cs.Concepts)
            {
                var t = type ?? TypeOf(c);
                if (t is null) continue;
                foreach (var phrase in Variants(c.Display).Concat(c.DisplayId is null ? [] : Variants(c.DisplayId)))
                    Add(phrase, t.Value, new ClinicalCode(c.System, c.Code, c.Display));
            }
        }
        foreach (var (phrase, system, code) in Synonyms)
            if (_terms.Lookup(system, code) is { } c) Add(phrase, system == CodeSystems.RxNorm ? ClinicalEntityType.Drug : TypeOf(c) ?? ClinicalEntityType.Problem, new ClinicalCode(c.System, c.Code, c.Display));
    }

    /// <summary>Adds a phrase (e.g. a local synonym or brand name) for a code.</summary>
    public void Add(string phrase, ClinicalEntityType type, ClinicalCode code)
    {
        var toks = Tokenize(phrase).Select(t => t.Norm).ToList();
        if (toks.Count == 0 || (toks.Count == 1 && toks[0].Length < 3)) return;
        var n = _root;
        foreach (var t in toks)
        {
            if (!n.Next.TryGetValue(t, out var nx)) n.Next[t] = nx = new Node();
            n = nx;
        }
        if (!n.Hits.Any(h => h.Code.System == code.System && h.Code.Code == code.Code)) n.Hits.Add((type, code));
    }

    public IReadOnlyList<ClinicalEntity> Recognize(string text)
    {
        var toks = Tokenize(text);
        var res = new List<ClinicalEntity>();
        int i = 0;
        while (i < toks.Count)
        {
            Node n = _root;
            int bestEnd = -1;
            List<(ClinicalEntityType Type, ClinicalCode Code)>? best = null;
            for (int j = i; j < toks.Count && n.Next.TryGetValue(toks[j].Norm, out var nx); j++)
            {
                n = nx;
                if (n.Hits.Count > 0) { bestEnd = j; best = n.Hits; }
            }
            if (best is null) { i++; continue; }
            int s = toks[i].Start, e = toks[bestEnd].End;
            var type = best.Select(h => h.Type).OrderBy(t => t == ClinicalEntityType.Problem ? 0 : 1).First();
            var codes = best.Select(h => h.Code).OrderBy(c => c.System == CodeSystems.Icd10 ? 0 : 1).ToList();
            res.Add(Context.Apply(text, new ClinicalEntity(type, text[s..e], s, e, 1.0, codes, Name)));
            i = bestEnd + 1;
        }
        return res;
    }

    private static ClinicalEntityType? TypeOf(Concept c)
    {
        if (c.System == CodeSystems.Icd10)
            return c.Code.StartsWith('R') ? ClinicalEntityType.Symptom : c.Code[0] is 'Z' or 'V' or 'W' or 'X' or 'Y' ? null : ClinicalEntityType.Problem;
        return c.Category switch
        {
            "disorder" => ClinicalEntityType.Problem,
            "finding" => ClinicalEntityType.Symptom,
            "procedure" => ClinicalEntityType.Procedure,
            "body structure" => ClinicalEntityType.Anatomy,
            "substance" or "product" => ClinicalEntityType.Drug,
            _ => null,
        };
    }

    /// <summary>Display variants: brackets removed, "unspecified" / "without complication" qualifiers stripped, British → US spelling.</summary>
    internal static IEnumerable<string> Variants(string display)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Put(string s)
        {
            s = s.Trim(' ', ',', ':', '-');
            if (s.Length < 3) return;
            set.Add(s);
            string us = s.Replace("oe", "e").Replace("ae", "e").Replace("aemia", "emia");
            set.Add(us);
        }
        Put(display);
        string core = System.Text.RegularExpressions.Regex.Replace(display, @"\[[^\]]*\]|\([^)]*\)", " ");
        foreach (var q in new[] { ", unspecified", " unspecified", ", not elsewhere classified", " without complications", " without complication", ", site not specified", " tidak spesifik", ", tidak spesifik" })
            core = core.Replace(q, "", StringComparison.OrdinalIgnoreCase);
        Put(core);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(display, @"\[([^\]]+)\]")) Put(m.Groups[1].Value);
        if (display.StartsWith("Malignant neoplasm:", StringComparison.OrdinalIgnoreCase))
            Put("cancer of " + display["Malignant neoplasm:".Length..].Split(',')[0]);
        return set;
    }

    /// <summary>Colloquial / abbreviated EN and ID phrases → code.</summary>
    public static IReadOnlyList<(string Phrase, string System, string Code)> Synonyms { get; } =
    [
        ("high blood pressure", CodeSystems.Icd10, "I10"), ("hypertension", CodeSystems.Icd10, "I10"), ("htn", CodeSystems.Icd10, "I10"),
        ("darah tinggi", CodeSystems.Icd10, "I10"), ("hipertensi", CodeSystems.Icd10, "I10"),
        ("type 2 diabetes", CodeSystems.Icd10, "E11.9"), ("t2dm", CodeSystems.Icd10, "E11.9"), ("diabetes mellitus", CodeSystems.Icd10, "E11.9"), ("diabetes", CodeSystems.Icd10, "E11.9"),
        ("kencing manis", CodeSystems.Icd10, "E11.9"), ("dm tipe 2", CodeSystems.Icd10, "E11.9"), ("diabetes melitus", CodeSystems.Icd10, "E11.9"),
        ("type 1 diabetes", CodeSystems.Icd10, "E10.9"),
        ("shortness of breath", CodeSystems.Icd10, "R06.0"), ("breathlessness", CodeSystems.Icd10, "R06.0"), ("sesak napas", CodeSystems.Icd10, "R06.0"), ("sesak nafas", CodeSystems.Icd10, "R06.0"), ("sesak", CodeSystems.Icd10, "R06.0"),
        ("fever", CodeSystems.Icd10, "R50.9"), ("demam", CodeSystems.Icd10, "R50.9"), ("panas", CodeSystems.Icd10, "R50.9"),
        ("cough", CodeSystems.Icd10, "R05"), ("batuk", CodeSystems.Icd10, "R05"),
        ("headache", CodeSystems.Icd10, "R51"), ("sakit kepala", CodeSystems.Icd10, "R51"), ("nyeri kepala", CodeSystems.Icd10, "R51"), ("pusing", CodeSystems.Icd10, "R42"),
        ("chest pain", CodeSystems.Icd10, "R07.4"), ("nyeri dada", CodeSystems.Icd10, "R07.4"),
        ("abdominal pain", CodeSystems.Icd10, "R10.4"), ("nyeri perut", CodeSystems.Icd10, "R10.4"), ("sakit perut", CodeSystems.Icd10, "R10.4"),
        ("vomiting", CodeSystems.Icd10, "R11"), ("nausea", CodeSystems.Icd10, "R11"), ("mual", CodeSystems.Icd10, "R11"), ("muntah", CodeSystems.Icd10, "R11"),
        ("fainting", CodeSystems.Icd10, "R55"), ("pingsan", CodeSystems.Icd10, "R55"), ("kejang demam", CodeSystems.Icd10, "R56.0"),
        ("pneumonia", CodeSystems.Icd10, "J18.9"), ("radang paru", CodeSystems.Icd10, "J18.9"), ("paru-paru basah", CodeSystems.Icd10, "J18.9"),
        ("copd", CodeSystems.Icd10, "J44.9"), ("ppok", CodeSystems.Icd10, "J44.9"), ("asthma", CodeSystems.Icd10, "J45.9"), ("asma", CodeSystems.Icd10, "J45.9"),
        ("tb paru", CodeSystems.Icd10, "A16.9"), ("pulmonary tuberculosis", CodeSystems.Icd10, "A16.9"), ("tuberculosis", CodeSystems.Icd10, "A16.9"), ("tbc", CodeSystems.Icd10, "A16.9"),
        ("dengue", CodeSystems.Icd10, "A90"), ("dbd", CodeSystems.Icd10, "A91"), ("demam berdarah", CodeSystems.Icd10, "A91"), ("typhoid", CodeSystems.Icd10, "A01.0"), ("tifoid", CodeSystems.Icd10, "A01.0"), ("tipes", CodeSystems.Icd10, "A01.0"),
        ("malaria", CodeSystems.Icd10, "B54"), ("sepsis", CodeSystems.Icd10, "A41.9"),
        ("heart attack", CodeSystems.Icd10, "I21.9"), ("myocardial infarction", CodeSystems.Icd10, "I21.9"), ("ami", CodeSystems.Icd10, "I21.9"), ("stemi", CodeSystems.Icd10, "I21.9"), ("serangan jantung", CodeSystems.Icd10, "I21.9"),
        ("heart failure", CodeSystems.Icd10, "I50.9"), ("gagal jantung", CodeSystems.Icd10, "I50.9"), ("chf", CodeSystems.Icd10, "I50.0"),
        ("atrial fibrillation", CodeSystems.Icd10, "I48"), ("af", CodeSystems.Icd10, "I48"), ("fibrilasi atrium", CodeSystems.Icd10, "I48"),
        ("stroke", CodeSystems.Icd10, "I64"), ("ischaemic stroke", CodeSystems.Icd10, "I63.9"), ("ischemic stroke", CodeSystems.Icd10, "I63.9"), ("stroke iskemik", CodeSystems.Icd10, "I63.9"),
        ("pulmonary embolism", CodeSystems.Icd10, "I26.9"), ("emboli paru", CodeSystems.Icd10, "I26.9"), ("dvt", CodeSystems.Icd10, "I80.2"), ("deep vein thrombosis", CodeSystems.Icd10, "I80.2"),
        ("ckd", CodeSystems.Icd10, "N18.9"), ("chronic kidney disease", CodeSystems.Icd10, "N18.9"), ("penyakit ginjal kronik", CodeSystems.Icd10, "N18.9"), ("gagal ginjal kronik", CodeSystems.Icd10, "N18.9"),
        ("aki", CodeSystems.Icd10, "N17.9"), ("acute kidney injury", CodeSystems.Icd10, "N17.9"), ("gagal ginjal akut", CodeSystems.Icd10, "N17.9"),
        ("uti", CodeSystems.Icd10, "N39.0"), ("urinary tract infection", CodeSystems.Icd10, "N39.0"), ("isk", CodeSystems.Icd10, "N39.0"), ("infeksi saluran kemih", CodeSystems.Icd10, "N39.0"),
        ("gerd", CodeSystems.Icd10, "K21.9"), ("gastritis", CodeSystems.Icd10, "K29.7"), ("maag", CodeSystems.Icd10, "K29.7"), ("appendicitis", CodeSystems.Icd10, "K35.9"), ("usus buntu", CodeSystems.Icd10, "K35.9"),
        ("anaemia", CodeSystems.Icd10, "D64.9"), ("anemia", CodeSystems.Icd10, "D64.9"), ("kurang darah", CodeSystems.Icd10, "D64.9"),
        ("obesity", CodeSystems.Icd10, "E66.9"), ("obesitas", CodeSystems.Icd10, "E66.9"), ("hyperlipidaemia", CodeSystems.Icd10, "E78.5"), ("kolesterol tinggi", CodeSystems.Icd10, "E78.0"),
        ("hypokalaemia", CodeSystems.Icd10, "E87.6"), ("hyperkalaemia", CodeSystems.Icd10, "E87.5"), ("dehydration", CodeSystems.Icd10, "E86"), ("dehidrasi", CodeSystems.Icd10, "E86"),
        ("preeclampsia", CodeSystems.Icd10, "O14.9"), ("pre-eclampsia", CodeSystems.Icd10, "O14.9"), ("preeklamsia", CodeSystems.Icd10, "O14.9"),
        ("depression", CodeSystems.Icd10, "F32.9"), ("depresi", CodeSystems.Icd10, "F32.9"), ("epilepsy", CodeSystems.Icd10, "G40.9"), ("epilepsi", CodeSystems.Icd10, "G40.9"), ("ayan", CodeSystems.Icd10, "G40.9"),
        ("paracetamol", CodeSystems.RxNorm, "161"), ("parasetamol", CodeSystems.RxNorm, "161"),
    ];

    internal readonly record struct Token(string Norm, int Start, int End);

    internal static List<Token> Tokenize(string text)
    {
        var res = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int s = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || (text[i] is '-' or '\'' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1])))) i++;
            res.Add(new Token(Fold(text[s..i]), s, i));
        }
        return res;
    }

    internal static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString().Replace("-", "");
    }
}

/// <summary>
/// NegEx-style context (Chapman et al., J Biomed Inform 2001) in English and Bahasa Indonesia: a trigger before an
/// entity within the same clause negates it (or marks it as family history); conjunctions such as "but" / "tetapi"
/// end the scope.
/// </summary>
public static class Context
{
    private static readonly string[] PreNegation =
    [
        "no", "not", "denies", "denied", "without", "negative for", "no evidence of", "no signs of", "free of", "absence of", "ruled out", "rules out", "never had",
        "tidak", "tidak ada", "tanpa", "bukan", "menyangkal", "negatif", "tak", "belum pernah", "tidak didapatkan", "tidak ditemukan", "disangkal",
    ];
    private static readonly string[] PostNegation = ["ruled out", "was ruled out", "is ruled out", "unlikely", "absent", "disangkal", "tidak ada", "negatif", "disingkirkan"];
    private static readonly string[] Family = ["family history", "family history of", "mother", "father", "brother", "sister", "riwayat keluarga", "ibu", "ayah", "kakak", "adik", "saudara"];
    private static readonly string[] Terminators = ["but", "however", "although", "except", "aside from", "tetapi", "namun", "tapi", "kecuali", "walaupun", "meskipun"];
    private const int Window = 6;

    /// <summary>Sets <see cref="ClinicalEntity.Negated"/> and <see cref="ClinicalEntity.FamilyHistory"/> from the surrounding clause.</summary>
    public static ClinicalEntity Apply(string text, ClinicalEntity e)
    {
        var before = Clause(text, e.Start, backwards: true);
        var after = Clause(text, e.End, backwards: false);
        var bt = TerminologyEntityRecognizer.Tokenize(before).Select(t => t.Norm).ToList();
        var at = TerminologyEntityRecognizer.Tokenize(after).Select(t => t.Norm).ToList();
        // Scope: from the last terminator before the entity.
        int cut = -1;
        for (int i = 0; i < bt.Count; i++)
            foreach (var term in Terminators)
                if (EndsWithPhrase(bt, i, term)) cut = i;
        var scope = bt.Skip(cut + 1).TakeLast(Window).ToList();
        bool neg = PreNegation.Any(p => ContainsPhrase(scope, p)) || PostNegation.Any(p => StartsWithPhrase(at.Take(4).ToList(), p));
        bool fam = Family.Any(p => ContainsPhrase(bt.Skip(cut + 1).ToList(), p));
        return e with { Negated = neg, FamilyHistory = fam };
    }

    private static string Clause(string text, int at, bool backwards)
    {
        const string stops = ".;\n!?:";
        if (backwards)
        {
            int s = at - 1;
            while (s >= 0 && stops.IndexOf(text[s]) < 0) s--;
            return text[(s + 1)..at];
        }
        int e = at;
        while (e < text.Length && stops.IndexOf(text[e]) < 0 && text[e] != ',') e++;
        return text[at..e];
    }

    private static string[] P(string phrase) => phrase.Split(' ').Select(TerminologyEntityRecognizer.Fold).ToArray();

    private static bool ContainsPhrase(List<string> toks, string phrase)
    {
        var p = P(phrase);
        for (int i = 0; i + p.Length <= toks.Count; i++)
            if (p.Select((w, k) => toks[i + k] == w).All(x => x)) return true;
        return false;
    }

    private static bool StartsWithPhrase(List<string> toks, string phrase)
    {
        var p = P(phrase);
        return p.Length <= toks.Count && p.Select((w, k) => toks[k] == w).All(x => x);
    }

    private static bool EndsWithPhrase(List<string> toks, int end, string phrase)
    {
        var p = P(phrase);
        int s = end - p.Length + 1;
        return s >= 0 && p.Select((w, k) => toks[s + k] == w).All(x => x);
    }
}

/// <summary>Runs several recognizers and merges overlapping spans, preferring coded entities and longer spans.</summary>
public sealed class CompositeEntityRecognizer(params IEntityRecognizer[] recognizers) : IEntityRecognizer
{
    public string Name => string.Join("+", recognizers.Select(r => r.Name));

    public IReadOnlyList<ClinicalEntity> Recognize(string text)
    {
        var all = recognizers.SelectMany(r => r.Recognize(text))
            .OrderByDescending(e => e.Codes.Count > 0).ThenByDescending(e => e.End - e.Start).ThenByDescending(e => e.Score).ToList();
        var kept = new List<ClinicalEntity>();
        foreach (var e in all)
        {
            var overlap = kept.FindIndex(k => e.Start < k.End && k.Start < e.End);
            if (overlap < 0) { kept.Add(e); continue; }
            // Same span from a model with a label: keep the coded entity but remember the label.
            var k = kept[overlap];
            if (k.Label is null && e.Label is not null) kept[overlap] = k with { Label = e.Label };
        }
        return kept.OrderBy(e => e.Start).ToList();
    }
}

/// <summary>An ICD-10 coding suggestion with the text that supports it.</summary>
public sealed record CodingSuggestion(string Code, string Display, double Score, IReadOnlyList<string> Evidence, Localized Note)
{
    /// <summary>Bahasa Indonesia display from the terminology, when available.</summary>
    public string? DisplayId { get; init; }

    /// <summary>The display in <paramref name="lang"/> ("id" falls back to English when no translation exists).</summary>
    public string DisplayFor(string lang) => lang == "id" && DisplayId is not null ? DisplayId : Display;

    public override string ToString() => $"{Code} {Display} ({Score:0.00}) ← {string.Join("; ", Evidence)}";
}

/// <summary>
/// Suggests ICD-10 codes from a clinical note: affirmed problems become candidates (SNOMED concepts through their
/// ICD-10 map, uncoded model entities through terminology search); negated and family-history mentions are excluded;
/// symptom codes (chapter R) are ranked below definitive diagnoses, as ICD-10 coding rules require. Suggestions are
/// for a coder to confirm, never for automatic claims.
/// </summary>
public sealed class Icd10Coder(IEntityRecognizer? recognizer = null, TerminologyService? terms = null)
{
    private readonly IEntityRecognizer _rec = recognizer ?? new TerminologyEntityRecognizer(terms);
    private readonly TerminologyService _terms = terms ?? TerminologyService.Default;

    public IReadOnlyList<ClinicalEntity> LastEntities { get; private set; } = [];

    public IReadOnlyList<CodingSuggestion> Suggest(string note, int max = 10)
    {
        var entities = _rec.Recognize(note);
        LastEntities = entities;
        var cands = new Dictionary<string, (string Display, double Score, List<string> Evidence)>();
        foreach (var e in entities.Where(e => e.Type is ClinicalEntityType.Problem or ClinicalEntityType.Symptom && !e.Negated && !e.FamilyHistory))
        {
            foreach (var (code, display, conf) in Icd10For(e))
            {
                if (!cands.TryGetValue(code, out var c)) cands[code] = c = (display, 0, []);
                c.Score = Math.Max(c.Score, conf * e.Score);
                if (!c.Evidence.Contains(e.Text, StringComparer.OrdinalIgnoreCase)) c.Evidence.Add(e.Text);
                cands[code] = c;
            }
        }
        bool hasDiagnosis = cands.Keys.Any(k => !k.StartsWith('R'));
        return cands
            .Select(kv =>
            {
                bool symptom = kv.Key.StartsWith('R');
                double score = Math.Round(kv.Value.Score * (symptom && hasDiagnosis ? 0.5 : 1) * (1 + 0.05 * (kv.Value.Evidence.Count - 1)), 3);
                var note = symptom && hasDiagnosis
                    ? new Localized("Symptom code — use only if no confirmed diagnosis explains it.", "Kode gejala — gunakan hanya bila tidak dijelaskan oleh diagnosis pasti.")
                    : new Localized("Affirmed in the note.", "Disebutkan sebagai temuan positif dalam catatan.");
                return new CodingSuggestion(kv.Key, kv.Value.Display, Math.Min(1, score), kv.Value.Evidence, note)
                {
                    DisplayId = _terms.Lookup(CodeSystems.Icd10, kv.Key)?.DisplayId,
                };
            })
            .OrderByDescending(s => s.Score).ThenBy(s => s.Code).Take(max).ToList();
    }

    private IEnumerable<(string Code, string Display, double Confidence)> Icd10For(ClinicalEntity e)
    {
        bool any = false;
        foreach (var c in e.Codes)
        {
            if (c.System is CodeSystems.Icd10 or CodeSystems.Icd10Cm) { any = true; yield return (c.Code, c.Display ?? c.Code, 1.0); }
            else if (c.System == CodeSystems.Snomed && _terms.SnomedToIcd10(c.Code) is { } m) { any = true; yield return (m.Target.Code, m.Target.Display, 0.9); }
        }
        if (any) yield break;
        var hit = _terms.Search(e.Text, CodeSystems.Icd10, 1).FirstOrDefault();
        if (hit.Concept is not null && hit.Score > 0) yield return (hit.Concept.Code, hit.Concept.Display, Math.Min(0.8, hit.Score));
    }
}
