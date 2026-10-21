using System.Globalization;
using System.Text;

namespace Blazor.AI.ThreeD;

/// <summary>An atom from a PDB ATOM/HETATM record.</summary>
public sealed record PdbAtom(int Serial, string Name, string Residue, char Chain, int ResidueNumber, double X, double Y, double Z, string Element, bool Hetero);

/// <summary>A residue's Cα position with its assigned secondary structure (H helix, E strand, C coil).</summary>
public sealed record PdbResidue(char Chain, int Number, string Name, double[] CA, char SecondaryStructure);

/// <summary>A parsed PDB structure (first model only), with Cα-geometry secondary-structure assignment.</summary>
public sealed class PdbStructure
{
    private static readonly Dictionary<string, char> OneLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALA"] = 'A', ["ARG"] = 'R', ["ASN"] = 'N', ["ASP"] = 'D', ["CYS"] = 'C', ["GLN"] = 'Q', ["GLU"] = 'E', ["GLY"] = 'G', ["HIS"] = 'H', ["ILE"] = 'I',
        ["LEU"] = 'L', ["LYS"] = 'K', ["MET"] = 'M', ["PHE"] = 'F', ["PRO"] = 'P', ["SER"] = 'S', ["THR"] = 'T', ["TRP"] = 'W', ["TYR"] = 'Y', ["VAL"] = 'V',
    };

    public string Title { get; private init; } = "Structure";

    public IReadOnlyList<PdbAtom> Atoms { get; private init; } = [];

    public IReadOnlyList<PdbResidue> Residues { get; private init; } = [];

    public IReadOnlyList<PdbAtom> Ligands => Atoms.Where(a => a.Hetero && a.Residue is not "HOH" and not "WAT").ToList();

    public IReadOnlyList<char> Chains => Residues.Select(r => r.Chain).Distinct().ToList();

    public string Sequence(char chain) => string.Concat(Residues.Where(r => r.Chain == chain).Select(r => OneLetter.GetValueOrDefault(r.Name, 'X')));

    public static PdbStructure Parse(string text)
    {
        var atoms = new List<PdbAtom>();
        var title = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("ENDMDL", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("TITLE", StringComparison.Ordinal) || line.StartsWith("HEADER", StringComparison.Ordinal) && title.Length == 0)
            {
                title.Append(line.Length > 10 ? line[10..].Trim() : string.Empty).Append(' ');
                continue;
            }

            var hetero = line.StartsWith("HETATM", StringComparison.Ordinal);
            if (!hetero && !line.StartsWith("ATOM", StringComparison.Ordinal) || line.Length < 54)
            {
                continue;
            }

            var altLoc = line.Length > 16 ? line[16] : ' ';
            if (altLoc is not ' ' and not 'A')
            {
                continue;
            }

            var element = line.Length >= 78 ? line[76..78].Trim() : string.Empty;
            var name = line[12..16].Trim();
            atoms.Add(new PdbAtom(
                Int(line[6..11]), name, line[17..20].Trim(), line.Length > 21 ? line[21] : 'A', Int(line[22..26]),
                Dbl(line[30..38]), Dbl(line[38..46]), Dbl(line[46..54]),
                element.Length > 0 ? element : new string(name.Where(char.IsLetter).Take(1).ToArray()), hetero));
        }

        if (atoms.Count == 0)
        {
            throw new FormatException("No ATOM or HETATM records found.");
        }

        var cas = atoms.Where(a => !a.Hetero && a.Name == "CA").ToList();
        var ss = AssignSecondaryStructure(cas.Select(a => new[] { a.X, a.Y, a.Z }).ToList(), cas.Select(a => a.Chain).ToList());
        var residues = cas.Select((a, i) => new PdbResidue(a.Chain, a.ResidueNumber, a.Residue, [a.X, a.Y, a.Z], ss[i])).ToList();
        return new PdbStructure { Title = title.Length > 0 ? title.ToString().Trim() : "Structure", Atoms = atoms, Residues = residues };
    }

    /// <summary>
    /// Cα-only assignment: helix where i→i+3 ≈ 5.0–5.6 Å and i→i+4 ≈ 5.8–6.6 Å; strand where i→i+2 ≥ 6.2 Å (extended).
    /// Runs shorter than 5 (helix) or 3 (strand) residues become coil.
    /// </summary>
    public static char[] AssignSecondaryStructure(IReadOnlyList<double[]> ca, IReadOnlyList<char> chain)
    {
        var n = ca.Count;
        var ss = Enumerable.Repeat('C', n).ToArray();
        double D(int i, int j) => i >= 0 && j < n && chain[i] == chain[j] ? Layout3D.Distance(ca[i], ca[j]) : double.NaN;
        for (var i = 0; i < n; i++)
        {
            var d3 = D(i, i + 3);
            var d4 = D(i, i + 4);
            if (d3 is >= 4.6 and <= 5.8 && d4 is >= 5.6 and <= 6.9)
            {
                for (var k = i; k <= Math.Min(n - 1, i + 3); k++)
                {
                    ss[k] = 'H';
                }
            }
        }

        for (var i = 1; i < n - 1; i++)
        {
            if (ss[i] == 'C' && D(i - 1, i + 1) >= 6.2)
            {
                ss[i] = 'E';
            }
        }

        // Remove short fragments.
        Smooth(ss, 'H', 5);
        Smooth(ss, 'E', 3);
        return ss;
    }

    private static void Smooth(char[] ss, char kind, int min)
    {
        var i = 0;
        while (i < ss.Length)
        {
            if (ss[i] != kind)
            {
                i++;
                continue;
            }

            var j = i;
            while (j < ss.Length && ss[j] == kind)
            {
                j++;
            }

            if (j - i < min)
            {
                for (var k = i; k < j; k++)
                {
                    ss[k] = 'C';
                }
            }

            i = j;
        }
    }

    /// <summary>
    /// An idealised 46-residue mini-protein (β-hairpin + two α-helices), generated from standard geometry
    /// (helix: 1.5 Å rise, 100°/residue, radius 2.3 Å; strand: 3.3 Å per residue). Synthetic, for offline demos.
    /// </summary>
    public static string SamplePdb()
    {
        const string seq = "TTCCPSIVARSNFNVCRLPGTPEAICATYTGCIIIPGATCPGDYAN"; // crambin-like composition, 46 residues
        var ca = new List<double[]>();
        // Strand 1 (1-6), turn, strand 2 (9-14): antiparallel hairpin along X.
        for (var i = 0; i < 6; i++)
        {
            ca.Add([i * 3.3, (i % 2) * 0.9, 0]);
        }

        ca.Add([5 * 3.3 + 2.4, 1.6, 2.4]);
        ca.Add([5 * 3.3 + 1.0, 0.4, 4.6]);
        for (var i = 0; i < 6; i++)
        {
            ca.Add([(5 - i) * 3.3, ((i + 1) % 2) * 0.9, 4.8]);
        }

        // Loop to helix 1.
        ca.Add([-2.8, 1.6, 6.2]);
        ca.Add([-4.4, 4.2, 6.8]);
        AddHelix(ca, [-4.0, 7.0, 7.0], [1, 0.12, 0.1], 13);
        ca.Add([ca[^1][0] + 2.6, ca[^1][1] + 2.4, ca[^1][2] - 1.2]);
        ca.Add([ca[^1][0] + 1.2, ca[^1][1] + 3.2, ca[^1][2] - 2.0]);
        ca.Add([ca[^1][0] - 1.4, ca[^1][1] + 2.4, ca[^1][2] - 2.6]);
        AddHelix(ca, [ca[^1][0] - 2.0, ca[^1][1] + 0.6, ca[^1][2] - 1.0], [-1, 0.05, -0.25], 12);
        while (ca.Count < seq.Length)
        {
            var last = ca[^1];
            ca.Add([last[0] - 2.5, last[1] - 2.4, last[2] - 0.6]);
        }

        var b = new StringBuilder("TITLE     IDEALISED MINI-PROTEIN (SYNTHETIC SAMPLE: BETA HAIRPIN + TWO HELICES)\n");
        for (var i = 0; i < seq.Length; i++)
        {
            var three = OneLetter.First(kv => kv.Value == seq[i]).Key;
            b.Append(CultureInfo.InvariantCulture, $"ATOM  {i + 1,5}  CA  {three} A{i + 1,4}    {ca[i][0],8:0.000}{ca[i][1],8:0.000}{ca[i][2],8:0.000}  1.00 20.00           C\n");
        }

        b.Append(CultureInfo.InvariantCulture, $"HETATM{seq.Length + 1,5} ZN    ZN A 101    {ca[30][0] + 3,8:0.000}{ca[30][1] + 2,8:0.000}{ca[30][2] + 3,8:0.000}  1.00 20.00          ZN\n");
        return b.Append("END\n").ToString();
    }

    private static void AddHelix(List<double[]> ca, double[] start, double[] axis, int count)
    {
        var len = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
        var u = new[] { axis[0] / len, axis[1] / len, axis[2] / len };
        var p = Stick3D.Perpendicular(u);
        var q = new[] { u[1] * p[2] - u[2] * p[1], u[2] * p[0] - u[0] * p[2], u[0] * p[1] - u[1] * p[0] };
        for (var i = 0; i < count; i++)
        {
            var t = i * 100 * Math.PI / 180;
            var r = 2.3;
            ca.Add([
                start[0] + u[0] * 1.5 * i + (p[0] * Math.Cos(t) + q[0] * Math.Sin(t)) * r,
                start[1] + u[1] * 1.5 * i + (p[1] * Math.Cos(t) + q[1] * Math.Sin(t)) * r,
                start[2] + u[2] * 1.5 * i + (p[2] * Math.Cos(t) + q[2] * Math.Sin(t)) * r,
            ]);
        }
    }

    private static int Int(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Dbl(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
