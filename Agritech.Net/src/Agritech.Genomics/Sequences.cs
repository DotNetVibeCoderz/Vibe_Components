using System.Collections.Frozen;
using System.Text;

namespace Agritech.Genomics;

public enum SequenceKind { Dna, Rna, Protein }

/// <summary>Immutable nucleotide sequence (DNA or RNA, IUPAC N allowed).</summary>
public sealed class NucleotideSequence
{
    public NucleotideSequence(string residues, SequenceKind kind = SequenceKind.Dna, string id = "seq", string? description = null)
    {
        if (kind == SequenceKind.Protein) throw new ArgumentException("Use a protein string for amino-acid sequences.");
        Residues = residues.ToUpperInvariant().Replace("\n", "").Replace("\r", "").Replace(" ", "");
        string valid = kind == SequenceKind.Dna ? "ACGTN" : "ACGUN";
        int bad = Residues.AsSpan().IndexOfAnyExcept(valid);
        if (bad >= 0) throw new FormatException($"Invalid {kind} residue '{Residues[bad]}' at position {bad + 1}.");
        Kind = kind; Id = id; Description = description;
    }

    public string Id { get; }
    public string? Description { get; }
    public string Residues { get; }
    public SequenceKind Kind { get; }
    public int Length => Residues.Length;
    public override string ToString() => Residues;

    public double GcContent
    {
        get
        {
            int gc = 0, acgt = 0;
            foreach (char c in Residues) { if (c is 'G' or 'C') gc++; if (c != 'N') acgt++; }
            return acgt == 0 ? 0 : gc * 100.0 / acgt;
        }
    }

    public NucleotideSequence ReverseComplement()
    {
        var sb = new StringBuilder(Length);
        for (int i = Length - 1; i >= 0; i--)
            sb.Append(Residues[i] switch { 'A' => Kind == SequenceKind.Dna ? 'T' : 'U', 'T' or 'U' => 'A', 'G' => 'C', 'C' => 'G', _ => 'N' });
        return new NucleotideSequence(sb.ToString(), Kind, Id + "_rc");
    }

    public NucleotideSequence Transcribe() =>
        Kind == SequenceKind.Rna ? this : new NucleotideSequence(Residues.Replace('T', 'U'), SequenceKind.Rna, Id);

    /// <summary>Translates using the standard genetic code (frame 0..2). Stops at the first stop codon when <paramref name="toStop"/>.</summary>
    public string Translate(int frame = 0, bool toStop = false)
    {
        var rna = Kind == SequenceKind.Dna ? Residues.Replace('T', 'U') : Residues;
        var sb = new StringBuilder(rna.Length / 3);
        for (int i = frame; i + 3 <= rna.Length; i += 3)
        {
            char aa = GeneticCode.Codon(rna.AsSpan(i, 3));
            if (aa == '*' && toStop) break;
            sb.Append(aa);
        }
        return sb.ToString();
    }

    /// <summary>Open reading frames (ATG…stop) on both strands with minimum protein length.</summary>
    public IReadOnlyList<OpenReadingFrame> FindOrfs(int minProteinLength = 30)
    {
        var res = new List<OpenReadingFrame>();
        foreach (var (seq, strand) in new[] { (this, '+'), (ReverseComplement(), '-') })
            for (int frame = 0; frame < 3; frame++)
            {
                string prot = seq.Translate(frame);
                int start = -1;
                for (int i = 0; i < prot.Length; i++)
                {
                    if (start < 0 && prot[i] == 'M') start = i;
                    else if (start >= 0 && prot[i] == '*')
                    {
                        if (i - start >= minProteinLength)
                        {
                            int nt0 = frame + start * 3, nt1 = frame + i * 3 + 3;
                            res.Add(strand == '+'
                                ? new OpenReadingFrame(strand, frame, nt0 + 1, nt1, prot[start..i])
                                : new OpenReadingFrame(strand, frame, Length - nt1 + 1, Length - nt0, prot[start..i]));
                        }
                        start = -1;
                    }
                }
            }
        return res.OrderByDescending(o => o.Protein.Length).ToList();
    }

    public Dictionary<string, int> KmerCounts(int k)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i + k <= Length; i++)
        {
            var kmer = Residues.Substring(i, k);
            if (kmer.Contains('N')) continue;
            d[kmer] = d.GetValueOrDefault(kmer) + 1;
        }
        return d;
    }

    /// <summary>Primer melting temperature: Wallace rule (&lt;14 nt) or GC-based formula.</summary>
    public double MeltingTemperature()
    {
        int a = Residues.Count(c => c is 'A' or 'T' or 'U'), g = Residues.Count(c => c is 'G' or 'C');
        return Length < 14 ? 2 * a + 4 * g : 64.9 + 41.0 * (g - 16.4) / (a + g);
    }

    public int HammingDistance(NucleotideSequence other)
    {
        if (other.Length != Length) throw new ArgumentException("Sequences must have equal length.");
        int d = 0;
        for (int i = 0; i < Length; i++) if (Residues[i] != other.Residues[i]) d++;
        return d;
    }

    /// <summary>All 0-based positions of a motif (overlapping).</summary>
    public IReadOnlyList<int> FindMotif(string motif)
    {
        var res = new List<int>();
        motif = motif.ToUpperInvariant();
        for (int i = Residues.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = Residues.IndexOf(motif, i + 1, StringComparison.Ordinal)) res.Add(i);
        return res;
    }
}

public sealed record OpenReadingFrame(char Strand, int Frame, int Start, int End, string Protein);

/// <summary>Standard genetic code (NCBI table 1).</summary>
public static class GeneticCode
{
    private static readonly FrozenDictionary<string, char> Table = Build();

    private static FrozenDictionary<string, char> Build()
    {
        const string bases = "UCAG";
        const string aas = "FFLLSSSSYY**CC*WLLLLPPPPHHQQRRRRIIIMTTTTNNKKSSRRVVVVAAAADDEEGGGG";
        var d = new Dictionary<string, char>();
        int n = 0;
        foreach (var a in bases) foreach (var b in bases) foreach (var c in bases) d[$"{a}{b}{c}"] = aas[n++];
        return d.ToFrozenDictionary();
    }

    public static char Codon(ReadOnlySpan<char> codon)
    {
        Span<char> c = stackalloc char[3];
        for (int i = 0; i < 3; i++) c[i] = codon[i] == 'T' ? 'U' : char.ToUpperInvariant(codon[i]);
        return Table.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(c, out var aa) ? aa : 'X';
    }
}

/// <summary>FASTA reader/writer.</summary>
public static class Fasta
{
    public static IEnumerable<NucleotideSequence> Read(TextReader reader, SequenceKind kind = SequenceKind.Dna)
    {
        string? header = null; var sb = new StringBuilder();
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith('>'))
            {
                if (header is not null) yield return Make(header, sb.ToString(), kind);
                header = line[1..].Trim(); sb.Clear();
            }
            else if (!line.StartsWith(';')) sb.Append(line.Trim());
        }
        if (header is not null) yield return Make(header, sb.ToString(), kind);
    }

    public static IReadOnlyList<NucleotideSequence> Parse(string text, SequenceKind kind = SequenceKind.Dna) => Read(new StringReader(text), kind).ToList();

    private static NucleotideSequence Make(string header, string seq, SequenceKind kind)
    {
        int sp = header.IndexOf(' ');
        return new NucleotideSequence(seq, kind, sp < 0 ? header : header[..sp], sp < 0 ? null : header[(sp + 1)..]);
    }

    public static string Write(IEnumerable<NucleotideSequence> seqs, int lineWidth = 70)
    {
        var sb = new StringBuilder();
        foreach (var s in seqs)
        {
            sb.Append('>').Append(s.Id);
            if (s.Description is not null) sb.Append(' ').Append(s.Description);
            sb.Append('\n');
            for (int i = 0; i < s.Length; i += lineWidth) sb.Append(s.Residues.AsSpan(i, Math.Min(lineWidth, s.Length - i))).Append('\n');
        }
        return sb.ToString();
    }
}

public sealed record AlignmentResult(int Score, string AlignedA, string AlignedB, double IdentityPct);

/// <summary>Pairwise alignment with linear gap penalty (Needleman–Wunsch global, Smith–Waterman local).</summary>
public static class Alignment
{
    public static AlignmentResult Global(string a, string b, int match = 1, int mismatch = -1, int gap = -2) => Align(a, b, match, mismatch, gap, false);
    public static AlignmentResult Local(string a, string b, int match = 2, int mismatch = -1, int gap = -2) => Align(a, b, match, mismatch, gap, true);

    private static AlignmentResult Align(string a, string b, int match, int mismatch, int gap, bool local)
    {
        int n = a.Length, m = b.Length;
        var h = new int[n + 1, m + 1];
        if (!local) { for (int i = 1; i <= n; i++) h[i, 0] = i * gap; for (int j = 1; j <= m; j++) h[0, j] = j * gap; }
        int best = 0, bi = n, bj = m;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                int diag = h[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? match : mismatch);
                int v = Math.Max(diag, Math.Max(h[i - 1, j] + gap, h[i, j - 1] + gap));
                if (local) v = Math.Max(0, v);
                h[i, j] = v;
                if (local && v > best) { best = v; bi = i; bj = j; }
            }
        int score = local ? best : h[n, m];
        var sa = new StringBuilder(); var sb = new StringBuilder();
        int x = local ? bi : n, y = local ? bj : m;
        while (x > 0 || y > 0)
        {
            if (local && h[x, y] == 0) break;
            if (x > 0 && y > 0 && h[x, y] == h[x - 1, y - 1] + (a[x - 1] == b[y - 1] ? match : mismatch)) { sa.Insert(0, a[x - 1]); sb.Insert(0, b[y - 1]); x--; y--; }
            else if (x > 0 && h[x, y] == h[x - 1, y] + gap) { sa.Insert(0, a[x - 1]); sb.Insert(0, '-'); x--; }
            else { sa.Insert(0, '-'); sb.Insert(0, b[y - 1]); y--; }
        }
        int same = 0;
        for (int i = 0; i < sa.Length; i++) if (sa[i] == sb[i] && sa[i] != '-') same++;
        return new AlignmentResult(score, sa.ToString(), sb.ToString(), sa.Length == 0 ? 0 : Math.Round(same * 100.0 / sa.Length, 2));
    }
}
