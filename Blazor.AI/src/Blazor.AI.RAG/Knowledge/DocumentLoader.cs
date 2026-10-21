using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Gravicode.Science.GraviFrame;
using Gravicode.Science.GraviFrame.Io;
using PdfNet.Document;

namespace Blazor.AI.RAG;

/// <summary>Plain text extracted from a file, split into pages when the format has them.</summary>
public sealed record LoadedDocument(string Name, string Kind, IReadOnlyList<string> Pages, long Bytes)
{
    public string Text => string.Join("\n\n", Pages);
}

/// <summary>
/// Turns uploaded files into text: PDF (PdfNet), Excel and CSV (GraviFrame), Markdown, HTML, JSON and plain text.
/// </summary>
public static partial class DocumentLoader
{
    private const string PageBreak = "\n<<<BAI-PAGE>>>\n";

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".txt", ".md", ".markdown", ".csv", ".tsv", ".xlsx", ".json", ".html", ".htm", ".xml", ".log" };

    public static bool IsSupported(string fileName) => SupportedExtensions.Contains(Path.GetExtension(fileName));

    public static async Task<LoadedDocument> LoadAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var kind = extension.TrimStart('.');

        IReadOnlyList<string> pages = extension switch
        {
            ".pdf" => LoadPdf(bytes),
            ".xlsx" => [TableToText(ExcelReader.Read(new MemoryStream(bytes)))],
            ".csv" or ".tsv" => [TableToText(ParseCsv(Encoding.UTF8.GetString(bytes)))],
            ".html" or ".htm" or ".xml" => [StripMarkup(Encoding.UTF8.GetString(bytes))],
            _ => [Encoding.UTF8.GetString(bytes)],
        };

        return new LoadedDocument(fileName, kind, pages.Select(Normalize).Where(p => p.Length > 0).ToArray(), bytes.LongLength);
    }

    public static LoadedDocument FromText(string name, string text) =>
        new(name, "txt", [Normalize(text)], Encoding.UTF8.GetByteCount(text));

    /// <summary>Reads a CSV or Excel file into a GraviFrame <see cref="DataFrame"/> (used by spreadsheet chat).</summary>
    public static async Task<DataFrame> LoadTableAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? ExcelReader.Read(buffer)
            : ParseCsv(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public static DataFrame ParseCsv(string content) => DataFrame.ParseCsv(content);

    private static string[] LoadPdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var text = pdf.ExtractText(PageBreak);
        return text.Split(PageBreak);
    }

    /// <summary>Rows as "column: value" lines, which embed and retrieve far better than raw CSV.</summary>
    public static string TableToText(DataFrame frame, int maxRows = 5_000)
    {
        var builder = new StringBuilder();
        builder.Append("Columns: ").AppendJoin(", ", frame.ColumnNames).AppendLine().AppendLine();
        var rows = Math.Min(frame.RowCount, maxRows);
        for (var r = 0; r < rows; r++)
        {
            builder.Append("Row ").Append(r + 1).Append(": ");
            for (var c = 0; c < frame.ColumnCount; c++)
            {
                var column = frame.Columns[c];
                builder.Append(column.Name).Append(" = ").Append(column.IsMissing(r) ? "(empty)" : Convert.ToString(column.GetValue(r), System.Globalization.CultureInfo.InvariantCulture));
                builder.Append(c < frame.ColumnCount - 1 ? "; " : "\n");
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    private static string StripMarkup(string html) => WebUtility.HtmlDecode(Tag().Replace(ScriptOrStyle().Replace(html, " "), " "));

    private static string Normalize(string text) => BlankLines().Replace(Spaces().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), " "), "\n\n").Trim();
}
