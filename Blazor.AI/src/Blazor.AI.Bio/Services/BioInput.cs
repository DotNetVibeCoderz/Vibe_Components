using Biotech.Net.Core;
using Biotech.Net.Sequences.IO;

namespace Blazor.AI.Bio;

/// <summary>Turns user input (raw residues or FASTA, with spaces, digits and line breaks) into Biotech.Net sequences.</summary>
public static class BioInput
{
    /// <summary>All sequences in the input. Raw text without a FASTA header becomes one sequence.</summary>
    public static IReadOnlyList<Sequence> ParseMany(string? text, SequenceType type, string defaultId = "query")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.TrimStart().StartsWith('>'))
        {
            return FastaReader.Parse(text).Select(s => Normalize(s.ToString(), type, s.Id)).ToList();
        }

        return [Normalize(text, type, defaultId)];
    }

    public static Sequence? ParseOne(string? text, SequenceType type, string defaultId = "query") => ParseMany(text, type, defaultId).FirstOrDefault();

    /// <summary>Keeps only letters (and '*' for proteins), upper-cases, and builds a typed sequence.</summary>
    public static Sequence Normalize(string raw, SequenceType type, string id)
    {
        var letters = new string(raw.Where(c => char.IsLetter(c) || (type == SequenceType.Protein && c == '*')).Select(char.ToUpperInvariant).ToArray());
        if (letters.Length == 0)
        {
            throw new FormatException("The input contains no residues.");
        }

        return type switch
        {
            SequenceType.Protein => Sequence.Protein(letters.TrimEnd('*'), id),
            SequenceType.Rna => Sequence.Rna(letters.Replace('T', 'U'), id),
            _ => Sequence.Dna(letters.Replace('U', 'T'), id),
        };
    }

    /// <summary>Residues as a plain string.</summary>
    public static string Residues(Sequence sequence) => sequence.ToString();

    /// <summary>Wraps residues in fixed-width lines with coordinates (for display).</summary>
    public static string Wrap(string residues, int width = 60, int group = 10)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < residues.Length; i += width)
        {
            builder.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)).Append("  ");
            var line = residues.Substring(i, Math.Min(width, residues.Length - i));
            for (var j = 0; j < line.Length; j += group)
            {
                builder.Append(line.AsSpan(j, Math.Min(group, line.Length - j))).Append(' ');
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
