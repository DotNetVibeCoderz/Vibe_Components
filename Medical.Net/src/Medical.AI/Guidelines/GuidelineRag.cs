using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Medical.AI.Assistant;
using Medical.Core;

namespace Medical.AI.Guidelines;

/// <summary>A retrievable passage of a guideline.</summary>
public sealed record GuidelineChunk(string Id, string Source, string Title, string Section, string Text, string Language, string? Url = null)
{
    /// <summary>Citation label, e.g. "WHO TB guidelines 2022 — Treatment".</summary>
    public string Citation => string.IsNullOrEmpty(Section) ? Title : $"{Title} — {Section}";
}

/// <summary>A passage returned for a query, with its fused rank and the component ranks.</summary>
public sealed record RetrievedPassage(GuidelineChunk Chunk, double Score, int? LexicalRank, int? DenseRank);

/// <summary>Turns text into a vector for dense retrieval (e.g. a Hugging Face sentence-embedding model via Medical.HuggingFace).</summary>
public interface ITextEmbedder
{
    string Name { get; }
    float[] Embed(string text);
}

/// <summary>A numbered source used in an answer.</summary>
public sealed record Citation(int Number, GuidelineChunk Chunk);

/// <summary>An answer grounded in guideline passages; every [n] in <see cref="Text"/> refers to <see cref="Citations"/>.</summary>
public sealed record GuidelineAnswer(string Text, IReadOnlyList<Citation> Citations, string Mode, bool Grounded, IReadOnlyList<string> Warnings)
{
    public override string ToString() => Text + Environment.NewLine + string.Join(Environment.NewLine, Citations.Select(c => $"[{c.Number}] {c.Chunk.Citation} ({c.Chunk.Source})"));
}

/// <summary>
/// A guideline corpus split into passages. Load your licensed / official texts (Kemenkes PNPK, WHO guidelines,
/// hospital protocols) with <see cref="AddDocument"/> or <see cref="LoadDirectory"/>; <see cref="Sample"/> holds short
/// summaries written for Medical.Net that point to the original documents.
/// </summary>
public sealed class GuidelineCorpus
{
    private readonly List<GuidelineChunk> _chunks = [];
    public IReadOnlyList<GuidelineChunk> Chunks => _chunks;

    /// <summary>
    /// Adds a document. Markdown headings (#, ##, ###) become sections; long sections are split into passages of about
    /// <paramref name="maxChars"/> characters on sentence boundaries.
    /// </summary>
    public GuidelineCorpus AddDocument(string source, string title, string text, string language = "en", string? url = null, int maxChars = 900)
    {
        string section = "";
        var buf = new StringBuilder();
        int n = 0;
        void Flush()
        {
            var body = buf.ToString().Trim();
            buf.Clear();
            if (body.Length == 0) return;
            foreach (var piece in Split(body, maxChars))
                _chunks.Add(new GuidelineChunk($"{source}#{++n}", source, title, section, piece, language, url));
        }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var h = Regex.Match(line, @"^#{1,3}\s+(.*)$");
            if (h.Success) { Flush(); section = h.Groups[1].Value.Trim(); continue; }
            buf.AppendLine(line);
        }
        Flush();
        return this;
    }

    /// <summary>
    /// Loads every .md / .txt file in a folder. Optional header lines at the top of a file: <c>title:</c>,
    /// <c>source:</c>, <c>language:</c>, <c>url:</c>, then a blank line.
    /// </summary>
    public static GuidelineCorpus LoadDirectory(string directory)
    {
        var c = new GuidelineCorpus();
        foreach (var file in Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(file).ToList();
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            for (; i < lines.Count; i++)
            {
                var m = Regex.Match(lines[i], @"^(title|source|language|url):\s*(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) break;
                meta[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            string name = Path.GetFileNameWithoutExtension(file);
            c.AddDocument(meta.GetValueOrDefault("source", name), meta.GetValueOrDefault("title", name), string.Join('\n', lines.Skip(i)),
                meta.GetValueOrDefault("language", "en"), meta.GetValueOrDefault("url"));
        }
        return c;
    }

    private static IEnumerable<string> Split(string body, int maxChars)
    {
        if (body.Length <= maxChars) { yield return body; yield break; }
        var sentences = Regex.Split(body, @"(?<=[.!?])\s+");
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            if (sb.Length > 0 && sb.Length + s.Length > maxChars) { yield return sb.ToString().Trim(); sb.Clear(); }
            sb.Append(s).Append(' ');
        }
        if (sb.Length > 0) yield return sb.ToString().Trim();
    }

    /// <summary>
    /// Short summaries written for Medical.Net (not the guideline texts) of a few WHO and Kemenkes recommendations, for
    /// demos and tests. Each passage names its source document; load the official documents for real use.
    /// </summary>
    public static GuidelineCorpus Sample()
    {
        var c = new GuidelineCorpus();
        c.AddDocument("WHO-TB-2022", "WHO consolidated guidelines on tuberculosis, Module 4: treatment (2022)", """
            ## Drug-susceptible pulmonary TB
            Adults and children with drug-susceptible pulmonary tuberculosis receive 6 months of treatment: an intensive phase of 2 months of isoniazid, rifampicin, pyrazinamide and ethambutol (2HRZE) followed by a continuation phase of 4 months of isoniazid and rifampicin (4HR).
            A 4-month regimen of rifapentine, isoniazid, pyrazinamide and moxifloxacin (2HPZM/2HPM) may be used in people aged 12 years or older.
            Children aged 3 months to 16 years with non-severe TB may be treated for 4 months (2HRZ(E)/2HR).
            ## Monitoring
            Sputum smear microscopy is repeated at the end of the intensive phase; daily dosing is preferred over intermittent dosing.
            """, "en", "https://www.who.int/publications/i/item/9789240048126");
        c.AddDocument("WHO-DENGUE-2009", "WHO Dengue: guidelines for diagnosis, treatment, prevention and control (2009)", """
            ## Warning signs
            Warning signs of severe dengue are abdominal pain or tenderness, persistent vomiting, clinical fluid accumulation, mucosal bleeding, lethargy or restlessness, liver enlargement over 2 cm, and a rise in haematocrit with a rapid fall in platelet count. Patients with warning signs are admitted for intravenous fluids.
            ## Fluid management
            For dengue with warning signs, start isotonic crystalloid at 5–7 mL/kg/h for 1–2 hours, then reduce to 3–5 mL/kg/h for 2–4 hours and 2–3 mL/kg/h, adjusting to the clinical response and haematocrit. Avoid aspirin and NSAIDs; paracetamol is used for fever.
            """, "en", "https://www.who.int/publications/i/item/9789241547871");
        c.AddDocument("WHO-SEPSIS-SSC-2021", "Surviving Sepsis Campaign: international guidelines 2021", """
            ## Initial resuscitation
            For possible septic shock or a high likelihood of sepsis, give antimicrobials immediately, ideally within 1 hour of recognition. For sepsis-induced hypoperfusion or septic shock, give at least 30 mL/kg of intravenous crystalloid within the first 3 hours. Use norepinephrine as the first-line vasopressor, targeting a mean arterial pressure of 65 mmHg. Measure lactate and repeat it when raised.
            """, "en", "https://www.sccm.org/survivingsepsiscampaign/guidelines-and-resources/surviving-sepsis-campaign-adult-guidelines-2021");
        c.AddDocument("WHO-GROWTH-2006", "WHO Child Growth Standards (2006) — interpretation", """
            ## Interpretation of z-scores
            Length/height-for-age below −2 SD is stunting and below −3 SD severe stunting. Weight-for-length/height below −2 SD is wasting and below −3 SD severe wasting; above +2 SD is overweight and above +3 SD obesity. Weight-for-length/height or BMI-for-age above +1 SD indicates a possible risk of overweight.
            """, "en", "https://www.who.int/tools/child-growth-standards");
        c.AddDocument("KMK-PNPK-HT-2021", "PNPK Tata Laksana Hipertensi Dewasa (Kemenkes RI, 2021)", """
            ## Diagnosis
            Hipertensi ditegakkan bila tekanan darah sistolik ≥ 140 mmHg dan/atau diastolik ≥ 90 mmHg pada pengukuran berulang di fasilitas kesehatan, sebaiknya dikonfirmasi dengan pengukuran di rumah atau ambulatori.
            ## Terapi
            Terapi awal pada sebagian besar pasien adalah kombinasi dua obat dosis rendah, misalnya penghambat ACE atau ARB dengan penghambat kanal kalsium atau diuretik tiazid, sebaiknya dalam satu pil. Target tekanan darah umumnya < 140/90 mmHg dan < 130/80 mmHg bila ditoleransi. Modifikasi gaya hidup: kurangi garam, aktivitas fisik, berhenti merokok.
            """, "id", "https://yankes.kemkes.go.id/");
        c.AddDocument("KMK-PNPK-MALARIA-2020", "PNPK Tata Laksana Malaria (Kemenkes RI, 2020)", """
            ## Malaria tanpa komplikasi
            Malaria falsiparum dan vivaks tanpa komplikasi diobati dengan ACT dihidroartemisinin-piperakuin (DHP) selama 3 hari ditambah primakuin: dosis tunggal 0,25 mg/kgBB pada falsiparum, dan 0,25 mg/kgBB selama 14 hari pada vivaks untuk mencegah relaps. Primakuin tidak diberikan pada ibu hamil dan bayi di bawah 6 bulan.
            ## Malaria berat
            Malaria berat diobati dengan artesunat intravena, lalu dilanjutkan DHP oral setelah pasien dapat minum obat.
            """, "id", "https://yankes.kemkes.go.id/");
        c.AddDocument("KMK-PNPK-DBD", "PNPK Tata Laksana Infeksi Dengue Anak dan Remaja (Kemenkes RI)", """
            ## Tanda bahaya
            Tanda bahaya dengue meliputi nyeri perut, muntah terus-menerus, perdarahan mukosa, letargi atau gelisah, pembesaran hati, akumulasi cairan, serta peningkatan hematokrit disertai penurunan trombosit cepat. Pasien dengan tanda bahaya dirawat inap untuk terapi cairan intravena kristaloid.
            ## Fase kritis
            Fase kritis terjadi saat penurunan demam (hari ke-3 sampai ke-7); pantau tanda vital, hematokrit dan diuresis secara ketat.
            """, "id", "https://yankes.kemkes.go.id/");
        c.AddDocument("KMK-PNPK-DM", "PNPK Tata Laksana Diabetes Melitus Tipe 2 Dewasa (Kemenkes RI)", """
            ## Diagnosis
            Diabetes melitus ditegakkan bila glukosa plasma puasa ≥ 126 mg/dL, glukosa 2 jam TTGO ≥ 200 mg/dL, HbA1c ≥ 6,5 % dengan metode terstandar, atau glukosa sewaktu ≥ 200 mg/dL dengan gejala klasik.
            ## Terapi farmakologis
            Metformin adalah obat lini pertama bila tidak ada kontraindikasi; hentikan bila eGFR < 30 mL/menit/1,73 m². Target HbA1c umumnya < 7 %.
            """, "id", "https://yankes.kemkes.go.id/");
        return c;
    }
}

/// <summary>
/// Hybrid retriever: BM25 over EN / ID tokens (with a small bilingual glossary so an Indonesian question reaches
/// English passages and vice versa) and, when an <see cref="ITextEmbedder"/> is supplied, dense cosine similarity;
/// the two rankings are combined with reciprocal rank fusion (Cormack et al., SIGIR 2009).
/// </summary>
public sealed class GuidelineRetriever
{
    private const double K1 = 1.2, B = 0.75, RrfK = 60;
    private readonly GuidelineCorpus _corpus;
    private readonly ITextEmbedder? _embedder;
    private readonly List<Dictionary<string, int>> _tf = [];
    private readonly Dictionary<string, int> _df = [];
    private readonly double _avgLen;
    private readonly float[][]? _vectors;

    public GuidelineRetriever(GuidelineCorpus corpus, ITextEmbedder? embedder = null)
    {
        _corpus = corpus;
        _embedder = embedder;
        foreach (var ch in corpus.Chunks)
        {
            var tf = new Dictionary<string, int>();
            foreach (var t in Terms($"{ch.Title} {ch.Section} {ch.Text}")) tf[t] = tf.GetValueOrDefault(t) + 1;
            _tf.Add(tf);
            foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        }
        _avgLen = _tf.Count == 0 ? 1 : _tf.Average(d => d.Values.Sum());
        if (embedder is not null) _vectors = corpus.Chunks.Select(c => Normalize(embedder.Embed($"{c.Citation}. {c.Text}"))).ToArray();
    }

    public bool HasDense => _vectors is not null;

    public IReadOnlyList<RetrievedPassage> Search(string query, int k = 5)
    {
        // Query terms weigh 1; glossary translations 0.5, so a passage in the question's language ranks first.
        var q = Expand(Terms(query)).GroupBy(t => t.Term).Select(g => (Term: g.Key, Weight: g.Max(t => t.Weight))).ToList();
        int n = _corpus.Chunks.Count;
        var lexical = new List<(int I, double S)>();
        for (int i = 0; i < n; i++)
        {
            double s = 0, len = _tf[i].Values.Sum();
            foreach (var (t, wt) in q)
            {
                if (!_tf[i].TryGetValue(t, out var f)) continue;
                double idf = Math.Log(1 + (n - _df[t] + 0.5) / (_df[t] + 0.5));
                s += wt * idf * f * (K1 + 1) / (f + K1 * (1 - B + B * len / _avgLen));
            }
            if (s > 0) lexical.Add((i, s));
        }
        var lexRank = lexical.OrderByDescending(x => x.S).Select((x, r) => (x.I, r)).ToDictionary(x => x.I, x => x.r + 1);
        Dictionary<int, int> denseRank = [];
        if (_vectors is not null)
        {
            var qv = Normalize(_embedder!.Embed(query));
            denseRank = Enumerable.Range(0, n).Select(i => (i, s: Dot(qv, _vectors[i]))).OrderByDescending(x => x.s).Take(Math.Max(k * 4, 20))
                .Select((x, r) => (x.i, r)).ToDictionary(x => x.i, x => x.r + 1);
        }
        return lexRank.Keys.Union(denseRank.Keys)
            .Select(i => (i, s: (lexRank.TryGetValue(i, out var a) ? 1 / (RrfK + a) : 0) + (denseRank.TryGetValue(i, out var d) ? 1 / (RrfK + d) : 0)))
            .OrderByDescending(x => x.s).Take(k)
            .Select(x => new RetrievedPassage(_corpus.Chunks[x.i], Math.Round(x.s, 5), lexRank.TryGetValue(x.i, out var a) ? a : null, denseRank.TryGetValue(x.i, out var d) ? d : null))
            .ToList();
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "or", "for", "to", "in", "on", "with", "is", "are", "be", "what", "which", "how", "when", "should", "does", "do", "at", "by", "as", "it", "this", "that", "from", "into", "than", "patient", "patients",
        "yang", "dan", "atau", "untuk", "pada", "dengan", "di", "ke", "dari", "adalah", "apa", "bagaimana", "kapan", "berapa", "ini", "itu", "bila", "jika", "pasien", "dalam", "sebagai", "oleh",
    };

    private static readonly Dictionary<string, string[]> Glossary = new(StringComparer.Ordinal)
    {
        ["tuberkulosis"] = ["tuberculosis", "tb"], ["tbc"] = ["tuberculosis", "tb"], ["tb"] = ["tuberculosis", "tuberkulosis"], ["tuberculosis"] = ["tuberkulosis", "tb"],
        ["demam"] = ["fever"], ["fever"] = ["demam"], ["berdarah"] = ["dengue"], ["dengue"] = ["dbd", "berdarah"], ["dbd"] = ["dengue"],
        ["hipertensi"] = ["hypertension"], ["hypertension"] = ["hipertensi"], ["tekanan"] = ["pressure"], ["darah"] = ["blood"],
        ["diabetes"] = ["melitus"], ["gula"] = ["glucose", "glukosa"], ["glukosa"] = ["glucose"], ["glucose"] = ["glukosa"],
        ["obat"] = ["treatment", "drug"], ["pengobatan"] = ["treatment", "terapi"], ["terapi"] = ["treatment", "therapy"], ["treatment"] = ["terapi", "pengobatan"],
        ["anak"] = ["children", "child"], ["children"] = ["anak"], ["dewasa"] = ["adults"], ["adults"] = ["dewasa"], ["hamil"] = ["pregnant", "pregnancy"],
        ["cairan"] = ["fluid", "fluids"], ["fluid"] = ["cairan"], ["fluids"] = ["cairan"], ["bulan"] = ["months"], ["months"] = ["bulan"],
        ["tanda"] = ["signs"], ["bahaya"] = ["warning"], ["warning"] = ["bahaya", "tanda"], ["syok"] = ["shock"], ["shock"] = ["syok"], ["sepsis"] = ["septic"],
        ["antibiotik"] = ["antimicrobials", "antibiotics"], ["antibiotics"] = ["antimicrobials", "antibiotik"], ["stunting"] = ["stunted", "pendek"], ["pendek"] = ["stunting"],
        ["gizi"] = ["nutrition", "wasting"], ["paru"] = ["pulmonary", "lung"], ["pulmonary"] = ["paru"], ["lama"] = ["duration", "months"], ["durasi"] = ["duration", "months"],
        ["lini"] = ["line"], ["pertama"] = ["first"], ["first"] = ["pertama"], ["dosis"] = ["dose"], ["dose"] = ["dosis"], ["sensitif"] = ["susceptible"], ["malaria"] = ["falsiparum", "vivaks"], ["diagnosis"] = ["diagnosa"],
    };

    private static readonly Dictionary<string, string[]> StemmedGlossary = Glossary
        .GroupBy(kv => Stem(kv.Key)).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).Distinct().ToArray());

    /// <summary>Query terms with glossary translations (weight 1 for the query's own words, 0.5 for translations).</summary>
    public static IReadOnlyDictionary<string, double> QueryTerms(string query) =>
        Expand(Terms(query)).GroupBy(t => t.Term).ToDictionary(g => g.Key, g => g.Max(t => t.Weight));

    private static IEnumerable<(string Term, double Weight)> Expand(IEnumerable<string> terms)
    {
        foreach (var t in terms)
        {
            yield return (t, 1);
            if (StemmedGlossary.TryGetValue(t, out var more)) foreach (var m in more) yield return (Stem(m), 0.5);
        }
    }

    internal static IEnumerable<string> Terms(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        foreach (var w in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!Stop.Contains(w) && (w.Length > 1 || char.IsDigit(w[0]))) yield return Stem(w);
    }

    /// <summary>Very light stemming: English plural / -ing / -ed; nothing for Indonesian (affixes carry meaning).</summary>
    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("ed") && !w.EndsWith("eed")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) return w[..^1];
        return w;
    }

    private static float[] Normalize(float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        return n < 1e-12 ? v : v.Select(x => (float)(x / n)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// Answers clinical questions from guideline passages with numbered citations. With an LLM the answer is generated
/// only from the retrieved passages and every citation is validated (unknown numbers are removed; an answer without a
/// valid citation is rejected); without one — or when validation fails — an extractive answer quotes the best-matching
/// sentences with their citations.
/// </summary>
public sealed class GuidelineAnswerer(GuidelineRetriever retriever, IClinicalLlm? llm = null)
{
    public async Task<GuidelineAnswer> AskAsync(string question, string language = "en", int k = 4, CancellationToken ct = default)
    {
        var passages = retriever.Search(question, k);
        if (passages.Count == 0)
            return new GuidelineAnswer(language == "id" ? "Tidak ditemukan rekomendasi yang relevan dalam korpus pedoman." : "No relevant recommendation was found in the guideline corpus.", [], "none", false, []);
        var cites = passages.Select((p, i) => new Citation(i + 1, p.Chunk)).ToList();
        var warnings = new List<string>();
        if (llm is not null and not OfflineClinicalLlm)
        {
            try
            {
                var sources = string.Join("\n\n", cites.Select(c => $"[{c.Number}] {c.Chunk.Citation} ({c.Chunk.Source})\n{c.Chunk.Text}"));
                string system = (language == "id"
                    ? "Anda menjawab pertanyaan klinis HANYA dari sumber pedoman bernomor yang diberikan. Setiap kalimat faktual harus diakhiri sitasi seperti [1]. Jika sumber tidak memuat jawabannya, katakan demikian. Jawab dalam Bahasa Indonesia, ringkas."
                    : "Answer the clinical question ONLY from the numbered guideline sources provided. End every factual sentence with a citation such as [1]. If the sources do not contain the answer, say so. Be concise.")
                    + " Do not call tools.";
                var reply = await llm.RunAsync(system, [new ChatMessage(ChatRole.User, $"Sources:\n{sources}\n\nQuestion: {question}")], new ClinicalTools(), null, ct).ConfigureAwait(false);
                var (text, used, invalid) = Validate(reply.Text, cites.Count);
                if (invalid > 0) warnings.Add($"Removed {invalid} citation(s) to sources that were not provided.");
                if (used.Count > 0 && !reply.Refused)
                    return new GuidelineAnswer(text, cites.Where(c => used.Contains(c.Number)).ToList(), $"llm:{llm.Provider}/{llm.Model}", true, warnings);
                warnings.Add("The model's answer had no valid citation; showing an extractive answer instead.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                warnings.Add($"LLM unavailable ({e.GetType().Name}); showing an extractive answer.");
            }
        }
        return Extractive(question, cites, warnings);
    }

    /// <summary>Removes citations outside 1..<paramref name="count"/> and reports which were used.</summary>
    public static (string Text, HashSet<int> Used, int Invalid) Validate(string text, int count)
    {
        var used = new HashSet<int>();
        int invalid = 0;
        string cleaned = Regex.Replace(text, @"\[(\d+(?:\s*[,–-]\s*\d+)*)\]", m =>
        {
            var nums = Regex.Matches(m.Groups[1].Value, @"\d+").Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
            var ok = nums.Where(n => n >= 1 && n <= count).Distinct().ToList();
            invalid += nums.Count - nums.Count(n => n >= 1 && n <= count);
            foreach (var n in ok) used.Add(n);
            return ok.Count == 0 ? "" : "[" + string.Join(", ", ok) + "]";
        });
        return (Regex.Replace(cleaned, @"\s+([.,;])", "$1").Trim(), used, invalid);
    }

    private static GuidelineAnswer Extractive(string question, List<Citation> cites, List<string> warnings)
    {
        var q = GuidelineRetriever.QueryTerms(question);
        var sentences = cites
            .SelectMany(c => Regex.Split(c.Chunk.Text, @"(?<=[.!?])\s+").Where(s => s.Length > 20).Select((s, i) => (c.Number, Sentence: s.Trim(), Order: i)))
            .Select(x => (x.Number, x.Sentence, x.Order, Score: GuidelineRetriever.Terms(x.Sentence).Distinct().Sum(t => q.GetValueOrDefault(t)) - 0.05 * x.Order - 0.2 * (x.Number - 1)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ToList();
        double best = sentences.Count > 0 ? sentences[0].Score : 0;
        sentences = sentences.Where(x => x.Score >= Math.Max(1, 0.5 * best)).Take(3)
            .OrderBy(x => x.Number).ThenBy(x => x.Order).ToList();
        if (sentences.Count == 0) sentences = [(1, Regex.Split(cites[0].Chunk.Text, @"(?<=[.!?])\s+")[0].Trim(), 0, 0)];
        var text = string.Join(" ", sentences.Select(s => $"{s.Sentence.TrimEnd('.')}. [{s.Number}]"));
        var used = sentences.Select(s => s.Number).ToHashSet();
        return new GuidelineAnswer(text, cites.Where(c => used.Contains(c.Number)).ToList(), "extractive", true, warnings);
    }
}
