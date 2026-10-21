using Biotech.Net.Core;

namespace Blazor.AI.Bio;

/// <summary>Curated example inputs so every biology component has something meaningful to analyse.</summary>
public static class BioSamples
{
    /// <summary>Enhanced green fluorescent protein coding sequence (720 bp).</summary>
    public const string Egfp =
        "ATGGTGAGCAAGGGCGAGGAGCTGTTCACCGGGGTGGTGCCCATCCTGGTCGAGCTGGACGGCGACGTAAACGGCCACAAGTTCAGCGTGTCCGGCGAGGGCGAGGGCGATGCCACCTACGGCAAGCTGACCCTGAAGTTCATCTGCACCACCGGCAAGCTGCCCGTGCCCTGGCCCACCCTCGTGACCACCCTGACCTACGGCGTGCAGTGCTTCAGCCGCTACCCCGACCACATGAAGCAGCACGACTTCTTCAAGTCCGCCATGCCCGAAGGCTACGTCCAGGAGCGCACCATCTTCTTCAAGGACGACGGCAACTACAAGACCCGCGCCGAGGTGAAGTTCGAGGGCGACACCCTGGTGAACCGCATCGAGCTGAAGGGCATCGACTTCAAGGAGGACGGCAACATCCTGGGGCACAAGCTGGAGTACAACTACAACAGCCACAACGTCTATATCATGGCCGACAAGCAGAAGAACGGCATCAAGGTGAACTTCAAGATCCGCCACAACATCGAGGACGGCAGCGTGCAGCTCGCCGACCACTACCAGCAGAACACCCCCATCGGCGACGGCCCCGTGCTGCTGCCCGACAACCACTACCTGAGCACCCAGTCCGCCCTGAGCAAAGACCCCAACGAGAAGCGCGATCACATGGTCCTGCTGGAGTTCGTGACCGCCGCCGGGATCACTCTCGGCATGGACGAGCTGTACAAGTAA";

    /// <summary>Human insulin precursor (preproinsulin), 110 aa.</summary>
    public const string Insulin = "MALWMRLLPLLALLALWGPDPAAAFVNQHLCGSHLVEALYLVCGERGFFYTPKTRREAEDLQVGQVELGGGPGAGSLQPLALEGSLQKRGIVEQCCTSICSLYQLENYCN";

    /// <summary>Human hemoglobin beta chain, 147 aa.</summary>
    public const string HemoglobinBeta = "MVHLTPEEKSAVTALWGKVNVDEVGGEALGRLLVVYPWTQRFFESFGDLSTPDAVMGNPKVKAHGKKVLGAFSDGLAHLDNLKGTFATLSELHCDKLHVDPENFRLLGNVLVCVLAHHFGKEFTPPVQAAYQKVVAGVANALAHKYH";

    /// <summary>Cytochrome c from several species, for multiple alignment and phylogeny.</summary>
    public const string CytochromeC = """
        >Human
        MGDVEKGKKIFIMKCSQCHTVEKGGKHKTGPNLHGLFGRKTGQAPGYSYTAANKNKGIIWGEDTLMEYLENPKKYIPGTKMIFVGIKKKEERADLIAYLKKATNE
        >Chimpanzee
        MGDVEKGKKIFIMKCSQCHTVEKGGKHKTGPNLHGLFGRKTGQAPGYSYTAANKNKGIIWGEDTLMEYLENPKKYIPGTKMIFVGIKKKEERADLIAYLKKATNE
        >Horse
        MGDVEKGKKIFVQKCAQCHTVEKGGKHKTGPNLHGLFGRKTGQAPGFTYTDANKNKGITWKEETLMEYLENPKKYIPGTKMIFAGIKKKTEREDLIAYLKKATNE
        >Chicken
        MGDIEKGKKIFVQKCSQCHTVEKGGKHKTGPNLHGLFGRKTGQAEGFSYTDANKNKGITWGEDTLMEYLENPKKYIPGTKMIFAGIKKKSERVDLIAYLKDATSK
        >Tuna
        MGDVAKGKKTFVQKCAQCHTVENGGKHKVGPNLWGLFGRKTGQAEGYSYTDANKSKGIVWNNDTLMEYLENPKKYIPGTKMIFAGIKKKGERQDLVAYLKSATS
        >Yeast
        MTEFKAGSAKKGATLFKTRCLQCHTVEKGGPHKVGPNLHGIFGRHSGQAEGYSYTDANIKKNVLWDENNMSEYLTNPKKYIPGTKMAFGGLKKEKDRNDLITYLKKACE
        """;

    public static readonly IReadOnlyList<(string Name, string Smiles)> Molecules =
    [
        ("Aspirin", "CC(=O)Oc1ccccc1C(=O)O"),
        ("Caffeine", "Cn1cnc2c1c(=O)n(C)c(=O)n2C"),
        ("Ibuprofen", "CC(C)Cc1ccc(cc1)C(C)C(=O)O"),
        ("Paracetamol", "CC(=O)Nc1ccc(O)cc1"),
        ("Curcumin", "COc1cc(/C=C/C(=O)CC(=O)/C=C/c2ccc(O)c(OC)c2)ccc1O"),
        ("Metformin", "CN(C)C(=N)N=C(N)N"),
        ("Imatinib", "Cc1ccc(NC(=O)c2ccc(CN3CCN(C)CC3)cc2)cc1Nc1nccc(-c2cccnc2)n1"),
        ("Artemisinin", "CC1CCC2C(C(=O)OC3CC4(C)OOC23C1CCO4)C"),
    ];

    /// <summary>A synthetic RNA-seq count matrix (genes × 6 samples) with known up- and down-regulated genes.</summary>
    public static Dataset CountMatrix(int genes = 400, int seed = 1)
    {
        var rng = new Random(seed);
        var names = Enumerable.Range(0, genes).Select(i => i < GeneNames.Length ? GeneNames[i] : $"GENE{i:0000}").ToArray();
        string[] samples = ["ctrl_1", "ctrl_2", "ctrl_3", "treat_1", "treat_2", "treat_3"];
        var values = new double[genes * samples.Length];
        for (var g = 0; g < genes; g++)
        {
            var mean = 30 + Math.Exp(rng.NextDouble() * 7.5);
            var foldChange = g < 25 ? 2 + rng.NextDouble() * 6 : g < 45 ? 1 / (2 + rng.NextDouble() * 5) : 1;
            for (var s = 0; s < samples.Length; s++)
            {
                var mu = mean * (s >= 3 ? foldChange : 1);
                // Negative-binomial-like noise (dispersion ~0.05).
                var noisy = mu * Math.Exp(0.22 * Gaussian(rng));
                values[g * samples.Length + s] = Math.Max(0, Math.Round(noisy));
            }
        }

        return new Dataset("rnaseq", names, samples, values).Annotate("condition", ["control", "control", "control", "treated", "treated", "treated"]);
    }

    private static readonly string[] GeneNames =
    [
        "HSPA1A", "HMOX1", "MT2A", "DDIT3", "ATF3", "GADD45A", "CDKN1A", "FOS", "EGR1", "JUN", "IL6", "CXCL8", "SERPINE1", "TXNRD1", "NQO1", "GCLM",
        "SLC7A11", "SQSTM1", "HSPB1", "DNAJB1", "BAG3", "HSPH1", "CRYAB", "TRIB3", "ASNS", "MKI67", "TOP2A", "CCNB1", "CDK1", "PLK1", "AURKA", "BUB1",
        "E2F1", "MCM2", "PCNA", "RRM2", "TYMS", "CENPF", "KIF11", "BIRC5", "CCNA2", "CDC20", "FOXM1", "MYBL2", "TK1",
        "GAPDH", "ACTB", "TUBB", "RPLP0", "EEF1A1", "B2M", "PPIA", "HPRT1", "TBP", "YWHAZ", "SDHA", "UBC", "GUSB", "HMBS", "PGK1",
    ];

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
