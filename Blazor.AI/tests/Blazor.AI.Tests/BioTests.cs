using Biotech.Net.Core;
using Blazor.AI.Bio;

namespace Blazor.AI.Tests;

public class BioTests
{
    [Fact]
    public void Input_accepts_raw_and_fasta()
    {
        var raw = BioInput.ParseOne("atg gcc\n12 att", SequenceType.Dna);
        Assert.Equal("ATGGCCATT", raw!.ToString());

        var many = BioInput.ParseMany(BioSamples.CytochromeC, SequenceType.Protein);
        Assert.Equal(6, many.Count);
        Assert.Equal("Human", many[0].Id);
    }

    [Fact]
    public void Input_rejects_empty_residues() =>
        Assert.Throws<FormatException>(() => BioInput.Normalize("123 ---", SequenceType.Dna, "x"));

    [Fact]
    public void Tools_compute_known_values()
    {
        Assert.Equal("CGAT", BioTools.ReverseComplement("ATCG"));
        Assert.Contains("GC 50.0", BioTools.DnaStats("ATGCATGC").Replace(" %", ""));
        Assert.Contains("C9H8O4", BioTools.MoleculeProperties("CC(=O)Oc1ccccc1C(=O)O"));
        Assert.Contains("pI", BioTools.ProteinProperties(BioSamples.Insulin));
    }

    [Fact]
    public void Count_table_parses_conditions()
    {
        const string csv = "gene,ctrl_1,ctrl_2,treat_1,treat_2\nA,10,12,40,44\nB,100,90,95,105\n";
        var data = BioCounts.Parse(csv);
        Assert.Equal(2, data.Rows);
        Assert.Equal(["control", "control", "treated", "treated"], data.ColumnAnnotations["condition"]);
    }

    [Fact]
    public void Depiction_draws_every_bond()
    {
        var mol = Biotech.Net.Chemistry.Molecule.FromSmiles("CC(=O)O");
        var drawing = MoleculeDepiction.Create(mol);
        Assert.True(drawing.Bonds.Count >= mol.BondCount);
        Assert.Contains(drawing.Labels, l => l.Text.StartsWith('O'));
    }
}
