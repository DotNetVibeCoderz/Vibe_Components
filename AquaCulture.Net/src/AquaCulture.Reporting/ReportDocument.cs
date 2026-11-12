using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AquaCulture.Core;

namespace AquaCulture.Reporting;

/// <summary>Building blocks of a report.</summary>
public abstract record ReportBlock
{
    public sealed record Heading(string Text, int Level = 1) : ReportBlock;
    public sealed record Paragraph(string Text) : ReportBlock;
    public sealed record Bullets(IReadOnlyList<string> Items) : ReportBlock;
    public sealed record KeyValues(IReadOnlyList<(string Key, string Value)> Items) : ReportBlock;
    public sealed record Table(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows) : ReportBlock;
    public sealed record Image(byte[] Png, string Caption, double WidthCm = 15) : ReportBlock;
}

/// <summary>Format-independent report; render it with any <see cref="ReportWriters"/> writer.</summary>
public sealed class ReportDocument
{
    public string Title { get; set; } = "AquaCulture.Net report";
    public string? Subtitle { get; set; }
    public string Author { get; set; } = "AquaCulture.Net";
    public DateTime Created { get; set; } = DateTime.Now;
    public string Language { get; set; } = "en";
    public List<ReportBlock> Blocks { get; } = [];

    public ReportDocument H(string text, int level = 1) { Blocks.Add(new ReportBlock.Heading(text, level)); return this; }
    public ReportDocument P(string text) { Blocks.Add(new ReportBlock.Paragraph(text)); return this; }
    public ReportDocument List(params string[] items) { Blocks.Add(new ReportBlock.Bullets(items)); return this; }
    public ReportDocument Kv(params (string Key, string Value)[] items) { Blocks.Add(new ReportBlock.KeyValues(items)); return this; }
    public ReportDocument Table(string name, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows) { Blocks.Add(new ReportBlock.Table(name, headers, rows.ToList())); return this; }
    public ReportDocument Image(byte[] png, string caption, double widthCm = 15) { Blocks.Add(new ReportBlock.Image(png, caption, widthCm)); return this; }

    public string Footer => $"{AquaInfo.Name} · {AquaInfo.Credit(Language)}";
    public IEnumerable<ReportBlock.Table> Tables => Blocks.OfType<ReportBlock.Table>();

    internal static string Cell(object? v) => v switch
    {
        null => "",
        double d when double.IsNaN(d) => "",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        DateOnly dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

/// <summary>Plain-text writers: CSV, JSON, Markdown and HTML.</summary>
public static partial class ReportWriters
{
    /// <summary>RFC 4180 CSV of one table (or the first table).</summary>
    public static string Csv(ReportDocument doc, string? tableName = null)
    {
        var t = (tableName is null ? doc.Tables.FirstOrDefault() : doc.Tables.FirstOrDefault(x => x.Name == tableName)) ?? throw new InvalidOperationException("No table in report.");
        return Csv(t.Headers, t.Rows);
    }

    public static string Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        static string Q(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Q))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(",", r.Select(c => Q(ReportDocument.Cell(c))))).Append("\r\n");
        return sb.ToString();
    }

    public static string Json(ReportDocument doc)
    {
        var blocks = new JsonArray();
        foreach (var b in doc.Blocks)
            blocks.Add((JsonNode)(b switch
            {
                ReportBlock.Heading h => new JsonObject { ["type"] = "heading", ["level"] = h.Level, ["text"] = h.Text },
                ReportBlock.Paragraph p => new JsonObject { ["type"] = "paragraph", ["text"] = p.Text },
                ReportBlock.Bullets l => new JsonObject { ["type"] = "bullets", ["items"] = new JsonArray(l.Items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) },
                ReportBlock.KeyValues kv => new JsonObject { ["type"] = "keyValues", ["items"] = new JsonObject(kv.Items.Select(i => KeyValuePair.Create(i.Key, (JsonNode?)JsonValue.Create(i.Value)))) },
                ReportBlock.Table t => new JsonObject
                {
                    ["type"] = "table", ["name"] = t.Name,
                    ["rows"] = new JsonArray(t.Rows.Select(r => (JsonNode)new JsonObject(t.Headers.Select((h, i) => KeyValuePair.Create(h, i < r.Count ? ToNode(r[i]) : null)))).ToArray()),
                },
                ReportBlock.Image img => new JsonObject { ["type"] = "image", ["caption"] = img.Caption, ["png"] = Convert.ToBase64String(img.Png) },
                _ => new JsonObject(),
            }));
        return new JsonObject
        {
            ["title"] = doc.Title, ["subtitle"] = doc.Subtitle, ["author"] = doc.Author, ["created"] = doc.Created.ToString("O", CultureInfo.InvariantCulture),
            ["generator"] = doc.Footer, ["blocks"] = blocks,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        double d when double.IsNaN(d) => null,
        double d => d, int i => i, long l => l, float f => f, bool b => b, decimal m => m,
        _ => JsonValue.Create(ReportDocument.Cell(v)),
    };

    public static string Markdown(ReportDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(doc.Title);
        if (doc.Subtitle is not null) sb.AppendLine().Append('_').Append(doc.Subtitle).AppendLine("_");
        foreach (var b in doc.Blocks)
        {
            sb.AppendLine();
            switch (b)
            {
                case ReportBlock.Heading h: sb.Append(new string('#', Math.Clamp(h.Level + 1, 2, 6))).Append(' ').AppendLine(h.Text); break;
                case ReportBlock.Paragraph p: sb.AppendLine(p.Text); break;
                case ReportBlock.Bullets l: foreach (var i in l.Items) sb.Append("- ").AppendLine(i); break;
                case ReportBlock.KeyValues kv: sb.AppendLine("| | |").AppendLine("|---|---|"); foreach (var (k, v) in kv.Items) sb.Append("| ").Append(k).Append(" | ").Append(v).AppendLine(" |"); break;
                case ReportBlock.Table t:
                    sb.Append("| ").Append(string.Join(" | ", t.Headers)).AppendLine(" |");
                    sb.Append('|').Append(string.Join("|", t.Headers.Select(_ => "---"))).AppendLine("|");
                    foreach (var r in t.Rows) sb.Append("| ").Append(string.Join(" | ", r.Select(ReportDocument.Cell))).AppendLine(" |");
                    break;
                case ReportBlock.Image img: sb.Append("![").Append(img.Caption).Append("](data:image/png;base64,").Append(Convert.ToBase64String(img.Png)).AppendLine(")"); break;
            }
        }
        sb.AppendLine().Append("---").AppendLine().AppendLine(doc.Footer);
        return sb.ToString();
    }

    public static string Html(ReportDocument doc)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(doc.Language).Append("\"><head><meta charset=\"utf-8\"><title>").Append(E(doc.Title)).Append("</title>");
        sb.Append("<style>body{font-family:system-ui,Segoe UI,sans-serif;color:#0b2e36;max-width:900px;margin:32px auto;padding:0 16px;line-height:1.5}")
          .Append("h1{font-size:28px;margin:0}h2{margin-top:28px;border-bottom:2px solid #12a4b6;padding-bottom:4px}table{border-collapse:collapse;width:100%;margin:10px 0}")
          .Append("th,td{border:1px solid #c9d8d6;padding:6px 8px;text-align:left}th{background:#e6f1f0}td.n{text-align:right;font-variant-numeric:tabular-nums}")
          .Append("footer{margin-top:36px;color:#5b7279;font-size:13px}figure{margin:12px 0}figcaption{color:#5b7279;font-size:13px}</style></head><body>");
        sb.Append("<h1>").Append(E(doc.Title)).Append("</h1>");
        if (doc.Subtitle is not null) sb.Append("<p><em>").Append(E(doc.Subtitle)).Append("</em></p>");
        foreach (var b in doc.Blocks)
            switch (b)
            {
                case ReportBlock.Heading h: sb.Append($"<h{Math.Clamp(h.Level + 1, 2, 6)}>").Append(E(h.Text)).Append($"</h{Math.Clamp(h.Level + 1, 2, 6)}>"); break;
                case ReportBlock.Paragraph p: sb.Append("<p>").Append(E(p.Text)).Append("</p>"); break;
                case ReportBlock.Bullets l: sb.Append("<ul>"); foreach (var i in l.Items) sb.Append("<li>").Append(E(i)).Append("</li>"); sb.Append("</ul>"); break;
                case ReportBlock.KeyValues kv: sb.Append("<table>"); foreach (var (k, v) in kv.Items) sb.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v)).Append("</td></tr>"); sb.Append("</table>"); break;
                case ReportBlock.Table t:
                    sb.Append("<table><thead><tr>"); foreach (var h in t.Headers) sb.Append("<th>").Append(E(h)).Append("</th>"); sb.Append("</tr></thead><tbody>");
                    foreach (var r in t.Rows) { sb.Append("<tr>"); foreach (var c in r) sb.Append(c is double or int or long or float ? "<td class=\"n\">" : "<td>").Append(E(ReportDocument.Cell(c))).Append("</td>"); sb.Append("</tr>"); }
                    sb.Append("</tbody></table>"); break;
                case ReportBlock.Image img: sb.Append("<figure><img alt=\"").Append(E(img.Caption)).Append("\" style=\"max-width:100%\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(img.Png)).Append("\"><figcaption>").Append(E(img.Caption)).Append("</figcaption></figure>"); break;
            }
        sb.Append("<footer>").Append(E(doc.Footer)).Append(" · ").Append(doc.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</footer></body></html>");
        return sb.ToString();
    }
}
