using System.ComponentModel;
using System.Globalization;
using System.Text;
using Blazor.AI.Agents;
using Biotech.Net.Chemistry;
using Biotech.Net.Core;
using Biotech.Net.DrugDiscovery;
using Biotech.Net.Proteomics;
using Biotech.Net.Sequences;
using Biotech.Net.SyntheticBiology.Crispr;
using Biotech.Net.SyntheticBiology.Primers;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Bio;

/// <summary>Biotech.Net functions exposed as agent tools, so a copilot computes instead of guessing.</summary>
public static class BioTools
{
    public static IReadOnlyList<AIFunction> All { get; } =
    [
        AIFunctionFactory.Create(DnaStats, "dna_statistics", "Length, GC content, molecular weight, melting temperature, ORF count and protein translation of a DNA sequence."),
        AIFunctionFactory.Create(ReverseComplement, "reverse_complement", "Reverse complement of a DNA sequence."),
        AIFunctionFactory.Create(ProteinProperties, "protein_properties", "ProtParam-style properties of a protein sequence: MW, pI, instability, GRAVY, aliphatic index, half-life."),
        AIFunctionFactory.Create(MoleculeProperties, "molecule_properties", "Descriptors, drug-likeness rules, QED and ADMET flags for a small molecule given as SMILES."),
        AIFunctionFactory.Create(CrisprGuides, "crispr_guides", "Top SpCas9 guide RNAs for a DNA target with on-target scores."),
        AIFunctionFactory.Create(PrimerPair, "primer_pair", "Best PCR primer pair for a DNA template with Tm and product size."),
        AIFunctionFactory.Create(RestrictionSites, "restriction_sites", "Enzymes that cut a DNA sequence exactly once."),
    ];

    /// <summary>Registers the tools in an agent tool registry under the "Biology" category.</summary>
    public static void RegisterAll(ToolRegistry registry)
    {
        foreach (var tool in All)
        {
            registry.Register(tool, "Biology", "biotech.net");
        }
    }

    public static string DnaStats([Description("DNA sequence (A, C, G, T)")] string sequence)
    {
        var dna = BioInput.Normalize(sequence, SequenceType.Dna, "query");
        var orfs = OrfFinder.Find(dna, new OrfOptions { MinLength = 75 });
        var protein = dna.Translate(toStop: true).ToString();
        return Invariant($"length {dna.Length} bp; GC {dna.GcContent():P1}; dsDNA MW {NucleicAcidMass.MolecularWeight(dna, true):N0} Da; Tm (nearest-neighbour, first 30 nt) {MeltingTemperature.NearestNeighbor(dna.Subsequence(0, Math.Min(30, dna.Length))):0.0} °C; ORFs ≥75 nt: {orfs.Count}; frame-1 translation: {(protein.Length > 60 ? protein[..60] + "…" : protein)}");
    }

    public static string ReverseComplement([Description("DNA sequence")] string sequence) =>
        BioInput.Normalize(sequence, SequenceType.Dna, "query").ReverseComplement().ToString();

    public static string ProteinProperties([Description("Protein sequence (one-letter codes)")] string sequence)
    {
        var p = new ProteinAnalysis(BioInput.Normalize(sequence, SequenceType.Protein, "query"));
        return Invariant($"length {p.Length} aa; MW {p.MolecularWeight():N0} Da; pI {p.IsoelectricPoint():0.00}; instability index {p.InstabilityIndex():0.0} ({(p.IsStable ? "stable" : "unstable")}); GRAVY {p.Gravy():0.000}; aliphatic index {p.AliphaticIndex():0.0}; half-life (mammalian) {p.HalfLife().Text}");
    }

    public static string MoleculeProperties([Description("SMILES string")] string smiles)
    {
        var mol = Molecule.FromSmiles(smiles);
        var profile = Admet.Profile(mol);
        var d = profile.Descriptors;
        return Invariant($"{d.Formula}; MW {d.MolecularWeight:0.00}; cLogP {d.LogP:0.00}; TPSA {d.Tpsa:0.0}; HBD {d.HBondDonors}; HBA {d.HBondAcceptors}; QED {profile.Qed:0.00}; rules passed: {string.Join(", ", profile.PassedRules)}; failed: {string.Join(", ", profile.FailedRules.Select(r => r.Rule))}; solubility {profile.SolubilityClass}; GI absorption {(profile.GiAbsorption ? "high" : "low")}; BBB {(profile.BloodBrainBarrier ? "yes" : "no")}; hERG risk {profile.HergRisk:0.00}");
    }

    public static string CrisprGuides([Description("DNA target region")] string sequence, [Description("How many guides to return")] int count = 3)
    {
        var guides = GuideDesigner.Design(BioInput.Normalize(sequence, SequenceType.Dna, "target"));
        var builder = new StringBuilder();
        foreach (var g in guides.Take(Math.Clamp(count, 1, 10)))
        {
            builder.Append(Invariant($"{g.Guide.Spacer} PAM {g.Guide.Pam} {g.Guide.Strand} at {g.Guide.Start}, on-target {g.OnTargetScore:0.00}, composite {g.CompositeScore:0.00}; "));
        }

        return guides.Count == 0 ? "No guides found (needs NGG PAM sites)." : builder.ToString();
    }

    public static string PrimerPair([Description("DNA template")] string sequence)
    {
        var pairs = Biotech.Net.SyntheticBiology.Primers.PrimerDesigner.DesignPairs(BioInput.Normalize(sequence, SequenceType.Dna, "template").ToString());
        if (pairs.Count == 0)
        {
            return "No primer pair satisfies the default constraints.";
        }

        var p = pairs[0];
        return Invariant($"forward {p.Forward.Sequence} (Tm {p.Forward.Tm:0.0} °C, GC {p.Forward.GcFraction:P0}); reverse {p.Reverse.Sequence} (Tm {p.Reverse.Tm:0.0} °C); product {p.ProductSize} bp");
    }

    public static string RestrictionSites([Description("DNA sequence")] string sequence)
    {
        var dna = BioInput.Normalize(sequence, SequenceType.Dna, "query");
        var single = RestrictionEnzymes.SingleCutters(dna);
        return single.Count == 0 ? "No single-cutter enzymes." : "Single cutters: " + string.Join(", ", single.Select(e => $"{e.Name} ({e.Site}) at {RestrictionDigest.FindCuts(dna, e)[0].Position}"));
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
