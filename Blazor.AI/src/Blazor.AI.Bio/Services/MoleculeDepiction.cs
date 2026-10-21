using System.Globalization;
using Biotech.Net.Chemistry;

namespace Blazor.AI.Bio;

/// <summary>A ready-to-render 2-D skeletal drawing of a molecule (carbons implicit, heteroatoms labelled).</summary>
public sealed class MoleculeDepiction
{
    public required string ViewBox { get; init; }

    public required IReadOnlyList<DepictedBond> Bonds { get; init; }

    public required IReadOnlyList<DepictedAtom> Labels { get; init; }

    public static MoleculeDepiction Create(Molecule mol, double scale = 40)
    {
        var coords = Depiction.Compute2DCoordinates(mol);
        var points = coords.Select(p => (X: p.X * scale, Y: -p.Y * scale)).ToArray();
        var bonds = new List<DepictedBond>();
        var labels = new List<DepictedAtom>();
        var shrink = new bool[mol.AtomCount];
        for (var i = 0; i < mol.AtomCount; i++)
        {
            var atom = mol.AtomAt(i);
            if (atom.AtomicNumber == 6 && mol.HeavyDegree(i) > 0)
            {
                continue;
            }

            shrink[i] = true;
            var h = atom.HydrogenCount;
            var text = atom.Symbol + (h switch { 0 => "", 1 => "H", _ => "H" + Subscript(h) }) + (atom.FormalCharge switch { 0 => "", > 0 => "+", _ => "−" });
            labels.Add(new DepictedAtom(points[i].X, points[i].Y, text, AtomColor(atom.AtomicNumber)));
        }

        for (var b = 0; b < mol.BondCount; b++)
        {
            var bond = mol.BondAt(b);
            var (x1, y1) = points[bond.Begin];
            var (x2, y2) = points[bond.End];
            Trim(ref x1, ref y1, x2, y2, shrink[bond.Begin]);
            Trim(ref x2, ref y2, x1, y1, shrink[bond.End]);
            bonds.Add(new DepictedBond(x1, y1, x2, y2, false));
            var order = bond.Order switch { BondOrder.Double => 2, BondOrder.Triple => 3, BondOrder.Aromatic => 1, _ => 0 };
            if (order == 0)
            {
                continue;
            }

            // Parallel line(s), offset towards the ring centre where possible.
            var (dx, dy) = (x2 - x1, y2 - y1);
            var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
            var (nx, ny) = (-dy / len * 6, dx / len * 6);
            var side = RingSide(mol, points, bond.Begin, bond.End, nx, ny);
            for (var k = 1; k <= Math.Min(order, 2); k++)
            {
                var s = order == 3 ? (k == 1 ? 1 : -1) : side;
                var inset = 0.15;
                bonds.Add(new DepictedBond(
                    x1 + dx * inset + nx * s, y1 + dy * inset + ny * s,
                    x2 - dx * inset + nx * s, y2 - dy * inset + ny * s,
                    bond.Order == BondOrder.Aromatic));
            }
        }

        var all = points.Length == 0 ? [(X: 0d, Y: 0d)] : points;
        double minX = all.Min(p => p.X) - 24, minY = all.Min(p => p.Y) - 24, maxX = all.Max(p => p.X) + 24, maxY = all.Max(p => p.Y) + 24;
        return new MoleculeDepiction
        {
            ViewBox = string.Create(CultureInfo.InvariantCulture, $"{minX:0.#} {minY:0.#} {maxX - minX:0.#} {maxY - minY:0.#}"),
            Bonds = bonds,
            Labels = labels,
        };
    }

    private static void Trim(ref double x, ref double y, double ox, double oy, bool labelled)
    {
        if (!labelled)
        {
            return;
        }

        var (dx, dy) = (ox - x, oy - y);
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 1e-6)
        {
            x += dx / len * 9;
            y += dy / len * 9;
        }
    }

    private static int RingSide(Molecule mol, (double X, double Y)[] points, int a, int b, double nx, double ny)
    {
        // Sum the positions of neighbours on each side of the bond; draw the second line on the busier side.
        var (mx, my) = ((points[a].X + points[b].X) / 2, (points[a].Y + points[b].Y) / 2);
        double score = 0;
        foreach (var atom in new[] { a, b })
        {
            foreach (var n in mol.Neighbors(atom))
            {
                if (n != a && n != b)
                {
                    score += (points[n].X - mx) * nx + (points[n].Y - my) * ny;
                }
            }
        }

        return score >= 0 ? 1 : -1;
    }

    private static string Subscript(int n) => string.Concat(n.ToString(CultureInfo.InvariantCulture).Select(c => (char)('₀' + (c - '0'))));

    private static string AtomColor(int z) => z switch
    {
        7 => "#3b6fd8",
        8 => "#d8443b",
        9 or 17 => "#2a9d5c",
        15 => "#d9822b",
        16 => "#b8a100",
        35 => "#a5432a",
        53 => "#7a3fa6",
        _ => "var(--bai-ink)",
    };
}

public readonly record struct DepictedBond(double X1, double Y1, double X2, double Y2, bool Dashed);

public readonly record struct DepictedAtom(double X, double Y, string Text, string Color);
