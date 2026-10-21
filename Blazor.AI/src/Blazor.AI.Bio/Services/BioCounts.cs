using System.Globalization;
using Biotech.Net.Core;

namespace Blazor.AI.Bio;

/// <summary>Reads a genes × samples count table (CSV or TSV) and labels samples by condition.</summary>
public static class BioCounts
{
    /// <summary>
    /// Parses counts; samples whose name contains "ctrl", "control", "wt" or "untreated" become <paramref name="reference"/>,
    /// the rest <paramref name="treatment"/>. The result is annotated with a "condition" column.
    /// </summary>
    public static Dataset Parse(string text, string reference = "control", string treatment = "treated")
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            throw new FormatException("The count table needs a header row and at least one gene.");
        }

        var separator = lines[0].Contains('\t') ? '\t' : lines[0].Contains(';') ? ';' : ',';
        var header = lines[0].Split(separator).Select(h => h.Trim('"', ' ')).ToArray();
        var samples = header[1..];
        if (samples.Length < 2)
        {
            throw new FormatException("Expected a gene id column followed by at least two sample columns.");
        }

        var genes = new List<string>(lines.Length - 1);
        var values = new List<double>((lines.Length - 1) * samples.Length);
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(separator);
            if (cells.Length != header.Length)
            {
                continue;
            }

            genes.Add(cells[0].Trim('"', ' '));
            foreach (var cell in cells.Skip(1))
            {
                values.Add(double.TryParse(cell.Trim('"', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, Math.Round(v)) : 0);
            }
        }

        string[] controlHints = ["ctrl", "control", "wt", "untreated", "mock"];
        var condition = samples.Select(s => controlHints.Any(h => s.Contains(h, StringComparison.OrdinalIgnoreCase)) ? reference : treatment).ToArray();
        if (condition.Distinct().Count() < 2)
        {
            throw new FormatException("Could not find both groups: name control samples with ctrl/control/wt/untreated.");
        }

        return new Dataset("counts", genes, samples, [.. values]).Annotate("condition", condition);
    }
}
