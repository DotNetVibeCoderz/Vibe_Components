using Gravicode.HFNet.GraviTransformers;
using Medical.AI.Guidelines;
using Medical.AI.Nlp;
using Medical.Core;
using Medical.Terminology;

namespace Medical.HuggingFace;

/// <summary>
/// Clinical named-entity recognition with a Hugging Face token-classification model run in-process by HF.Net, e.g.
/// <c>d4data/biomedical-ner-all</c> (107 biomedical entity types, Apache-2.0). Entities are typed, coded through the
/// terminology (ICD-10 / SNOMED / RxNorm / LOINC search) and given negation / family-history context.
/// </summary>
public sealed class HuggingFaceEntityRecognizer : IEntityRecognizer, IDisposable
{
    /// <summary>Default biomedical NER model.</summary>
    public const string DefaultModel = "d4data/biomedical-ner-all";

    private readonly TransformerModel _model;
    private readonly TerminologyService _terms;
    private readonly bool _own;

    public HuggingFaceEntityRecognizer(TransformerModel model, TerminologyService? terms = null, bool ownsModel = false)
    {
        if (!model.HasTokenClassificationHead) throw new ArgumentException($"{model.RepoId} has no token-classification head.", nameof(model));
        _model = model;
        _terms = terms ?? TerminologyService.Default;
        _own = ownsModel;
    }

    /// <summary>Downloads (or reuses the cached) model from the Hub and loads it.</summary>
    public static HuggingFaceEntityRecognizer Load(string repoId = DefaultModel, TerminologyService? terms = null) =>
        new(TransformerModel.Load(repoId), terms, ownsModel: true);

    public string Name => $"hf:{_model.RepoId}";

    /// <summary>Minimum mean token confidence for an entity to be kept.</summary>
    public double MinScore { get; init; } = 0.5;

    public IReadOnlyList<ClinicalEntity> Recognize(string text)
    {
        var res = new List<ClinicalEntity>();
        foreach (var (label, start, end, score) in WordSpans(text, _model.FindEntities(text).Select(e => (e.Label, e.Start, e.End, e.Score))))
        {
            if (score < MinScore) continue;
            var type = MapLabel(label);
            if (type is null) continue;
            string span = text[start..end];
            res.Add(Context.Apply(text, new ClinicalEntity(type.Value, span, start, end, Math.Round(score, 3), Code(span, type.Value), Name) { Label = label }));
        }
        return res;
    }

    /// <summary>
    /// Word-level aggregation (like the "max" strategy of the transformers pipeline): sub-word pieces are widened to
    /// whole words and overlapping pieces of the same label merged, so "am" + "oxici" becomes "amoxicillin".
    /// </summary>
    public static IReadOnlyList<(string Label, int Start, int End, double Score)> WordSpans(string text, IEnumerable<(string Label, int Start, int End, double Score)> pieces)
    {
        static bool Word(char c) => char.IsLetterOrDigit(c) || c is '-' or '\'';
        var merged = new List<(string Label, int Start, int End, double Score, int N)>();
        foreach (var (label, s0, e0, score) in pieces.OrderBy(p => p.Start))
        {
            int s = Math.Clamp(s0, 0, text.Length), e = Math.Clamp(e0, s, text.Length);
            while (s > 0 && Word(text[s - 1])) s--;
            while (e < text.Length && Word(text[e])) e++;
            if (merged.Count > 0 && merged[^1].Label == label && s <= merged[^1].End)
            {
                var m = merged[^1];
                merged[^1] = (label, m.Start, Math.Max(m.End, e), m.Score + score, m.N + 1);
            }
            else if (merged.Count > 0 && s < merged[^1].End)
            {
                // Same word claimed by two labels: keep the more confident one.
                var m = merged[^1];
                if (score > m.Score / m.N) merged[^1] = (label, Math.Min(s, m.Start), Math.Max(e, m.End), score, 1);
            }
            else merged.Add((label, s, e, score, 1));
        }
        return merged.Select(m => (m.Label, m.Start, m.End, m.Score / m.N)).ToList();
    }

    /// <summary>Maps model labels (biomedical-ner-all, i2b2-style and BC5CDR-style) to entity types; null = ignore.</summary>
    public static ClinicalEntityType? MapLabel(string label) => label.ToLowerInvariant().Replace('-', '_') switch
    {
        "disease_disorder" or "disease" or "problem" or "diagnosis" => ClinicalEntityType.Problem,
        "sign_symptom" or "symptom" => ClinicalEntityType.Symptom,
        "medication" or "drug" or "chemical" or "treatment" => ClinicalEntityType.Drug,
        "diagnostic_procedure" or "lab_value" or "test" => ClinicalEntityType.Test,
        "therapeutic_procedure" or "procedure" => ClinicalEntityType.Procedure,
        "biological_structure" or "anatomy" => ClinicalEntityType.Anatomy,
        _ => null,
    };

    private IReadOnlyList<ClinicalCode> Code(string text, ClinicalEntityType type)
    {
        string? system = type switch
        {
            ClinicalEntityType.Problem or ClinicalEntityType.Symptom => CodeSystems.Icd10,
            ClinicalEntityType.Drug => CodeSystems.RxNorm,
            ClinicalEntityType.Test => CodeSystems.Loinc,
            _ => null,
        };
        if (system is null) return [];
        var hit = _terms.Search(text, system, 1).FirstOrDefault();
        return hit.Concept is null ? [] : [new ClinicalCode(hit.Concept.System, hit.Concept.Code, hit.Concept.Display)];
    }

    public void Dispose() { if (_own) _model.Dispose(); }
}

/// <summary>
/// Sentence embeddings from a Hugging Face encoder (mean-pooled), for dense guideline retrieval. A multilingual
/// sentence-transformer such as <c>sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2</c> lets Indonesian
/// questions match English passages.
/// </summary>
public sealed class HuggingFaceEmbedder : ITextEmbedder, IDisposable
{
    public const string DefaultModel = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";

    private readonly TransformerModel _model;
    private readonly bool _own;
    private readonly int _maxLength;

    public HuggingFaceEmbedder(TransformerModel model, int maxLength = 256, bool ownsModel = false)
    {
        _model = model;
        _maxLength = maxLength;
        _own = ownsModel;
    }

    public static HuggingFaceEmbedder Load(string repoId = DefaultModel, int maxLength = 256) => new(TransformerModel.Load(repoId), maxLength, ownsModel: true);

    public string Name => $"hf:{_model.RepoId}";

    /// <summary>Mean-pooled sentence vector (HF.Net returns an <c>NdArray</c>; copied row-major to floats).</summary>
    public float[] Embed(string text) => Array.ConvertAll(_model.Embed(text, _maxLength).ToArray(), x => (float)x);

    public void Dispose() { if (_own) _model.Dispose(); }
}
