using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace AquaCulture.Reporting;

/// <summary>Office Open XML (XLSX, DOCX) and PDF writers implemented directly on ZIP/XML/PDF primitives (no external dependencies).</summary>
public static partial class ReportWriters
{
    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    private static void Entry(ZipArchive zip, string path, string content)
    {
        var e = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static void Entry(ZipArchive zip, string path, byte[] content)
    {
        var e = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(content);
    }

    // ─────────────────────────────── XLSX ───────────────────────────────

    /// <summary>Excel workbook: a "Summary" sheet (title, key-values, paragraphs) and one sheet per table. Numbers are stored as numbers.</summary>
    public static byte[] Xlsx(ReportDocument doc)
    {
        var sheets = new List<(string Name, List<IReadOnlyList<object?>> Rows, int HeaderRow)>();
        var summary = new List<IReadOnlyList<object?>> { new object?[] { doc.Title } };
        if (doc.Subtitle is not null) summary.Add([doc.Subtitle]);
        summary.Add([]);
        foreach (var b in doc.Blocks)
            switch (b)
            {
                case ReportBlock.Heading h: summary.Add([]); summary.Add([h.Text]); break;
                case ReportBlock.Paragraph p: summary.Add([p.Text]); break;
                case ReportBlock.Bullets l: foreach (var i in l.Items) summary.Add(["• " + i]); break;
                case ReportBlock.KeyValues kv: foreach (var (k, v) in kv.Items) summary.Add([k, double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v]); break;
            }
        summary.Add([]); summary.Add([doc.Footer]);
        sheets.Add(("Summary", summary, -1));
        foreach (var t in doc.Tables)
        {
            var rows = new List<IReadOnlyList<object?>> { t.Headers.Cast<object?>().ToList() };
            rows.AddRange(t.Rows);
            string name = new string(t.Name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            name = name.Length == 0 ? $"Table{sheets.Count}" : name[..Math.Min(31, name.Length)];
            while (sheets.Any(s => s.Name == name)) name = name[..Math.Min(28, name.Length)] + sheets.Count;
            sheets.Add((name, rows, 0));
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
                .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>")
                .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>")
                .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>")
                .Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            for (int i = 0; i < sheets.Count; i++) ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            Entry(zip, "[Content_Types].xml", ct.Append("</Types>").ToString());
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Entry(zip, "docProps/core.xml", CoreProps(doc));
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{X(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            }
            rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            Entry(zip, "xl/workbook.xml", wb.Append("</sheets></workbook>").ToString());
            Entry(zip, "xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());
            Entry(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"16\"/><color rgb=\"FF0B2E36\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0F7C8A\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/><xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/><xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
            for (int i = 0; i < sheets.Count; i++) Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i].Rows, sheets[i].HeaderRow, i == 0));
        }
        return ms.ToArray();
    }

    private static string ColName(int c) { var s = ""; c++; while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }

    private static string Sheet(List<IReadOnlyList<object?>> rows, int headerRow, bool isSummary)
    {
        int maxCols = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Count));
        var widths = Enumerable.Range(0, maxCols).Select(c => Math.Clamp(rows.Where(r => c < r.Count).Select(r => ReportDocument.Cell(r[c]).Length).DefaultIfEmpty(8).Max() + 2, 8, isSummary && c == 0 ? 90 : 48)).ToArray();
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (headerRow == 0) sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols>");
        for (int c = 0; c < maxCols; c++) sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{widths[c]}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Count; c++)
            {
                var v = rows[r][c];
                string refc = ColName(c) + (r + 1);
                int style = r == headerRow ? 1 : isSummary && r == 0 ? 2 : 0;
                switch (v)
                {
                    case null: break;
                    case double d when double.IsFinite(d): sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{refc}\" s=\"{(style == 0 && Math.Abs(d) >= 1000 ? 3 : style)}\"><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                    case int or long or float or decimal: sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{refc}\" s=\"{style}\"><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>"); break;
                    default: sb.Append($"<c r=\"{refc}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(ReportDocument.Cell(v))}</t></is></c>"); break;
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    private static string CoreProps(ReportDocument doc) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        $"<dc:title>{X(doc.Title)}</dc:title><dc:creator>{X(doc.Author)}</dc:creator><dc:description>{X(doc.Footer)}</dc:description>" +
        $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{doc.Created.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>";

    // ─────────────────────────────── DOCX ───────────────────────────────

    /// <summary>Word document with title, headings, paragraphs, bullet lists, tables and PNG images.</summary>
    public static byte[] Docx(ReportDocument doc)
    {
        var body = new StringBuilder();
        void Para(string text, string? style = null, bool bold = false, string? color = null, int? size = null) =>
            body.Append("<w:p>").Append(style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")
                .Append("<w:r>").Append(bold || color is not null || size is not null ? $"<w:rPr>{(bold ? "<w:b/>" : "")}{(color is null ? "" : $"<w:color w:val=\"{color}\"/>")}{(size is null ? "" : $"<w:sz w:val=\"{size}\"/>")}</w:rPr>" : "")
                .Append($"<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>");
        Para(doc.Title, "Title");
        if (doc.Subtitle is not null) Para(doc.Subtitle, "Subtitle");
        var images = new List<byte[]>();
        foreach (var b in doc.Blocks)
            switch (b)
            {
                case ReportBlock.Heading h: Para(h.Text, h.Level <= 1 ? "Heading1" : "Heading2"); break;
                case ReportBlock.Paragraph p: Para(p.Text); break;
                case ReportBlock.Bullets l: foreach (var i in l.Items) Para("• " + i, "ListParagraph"); break;
                case ReportBlock.KeyValues kv: DocxTable(body, ["", ""], kv.Items.Select(x => (IReadOnlyList<object?>)new object?[] { x.Key, x.Value }).ToList(), keyValue: true); break;
                case ReportBlock.Table t: DocxTable(body, t.Headers, t.Rows, keyValue: false); break;
                case ReportBlock.Image img:
                    images.Add(img.Png);
                    var (pw, ph) = PngSize(img.Png);
                    long cx = (long)(img.WidthCm * 360000), cy = pw == 0 ? cx / 2 : cx * ph / pw;
                    int id = images.Count;
                    body.Append($"<w:p><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"Picture {id}\"/>")
                        .Append("<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
                        .Append($"<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"image{id}.png\"/><pic:cNvPicPr/></pic:nvPicPr>")
                        .Append($"<pic:blipFill><a:blip r:embed=\"rImg{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>")
                        .Append("</a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
                    Para(img.Caption, "Caption");
                    break;
            }
        Para(doc.Footer, null, false, "5B7279", 18);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Entry(zip, "docProps/core.xml", CoreProps(doc));
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            for (int i = 0; i < images.Count; i++)
            {
                rels.Append($"<Relationship Id=\"rImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{i + 1}.png\"/>");
                Entry(zip, $"word/media/image{i + 1}.png", images[i]);
            }
            Entry(zip, "word/_rels/document.xml.rels", rels.Append("</Relationships>").ToString());
            Entry(zip, "word/styles.xml", DocxStyles);
            Entry(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"><w:body>"
                + body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
        }
        return ms.ToArray();
    }

    private static void DocxTable(StringBuilder body, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> rows, bool keyValue)
    {
        body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"C9D8D6\"/></w:tblBorders></w:tblPr>");
        void Cell(string text, bool header, bool right, bool bold = false) =>
            body.Append("<w:tc>").Append(header ? "<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"0F7C8A\"/></w:tcPr>" : "")
                .Append("<w:p>").Append(right ? "<w:pPr><w:jc w:val=\"right\"/></w:pPr>" : "").Append("<w:r>")
                .Append(header ? "<w:rPr><w:b/><w:color w:val=\"FFFFFF\"/></w:rPr>" : bold ? "<w:rPr><w:b/></w:rPr>" : "").Append($"<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p></w:tc>");
        if (!keyValue) { body.Append("<w:tr>"); foreach (var h in headers) Cell(h, true, false); body.Append("</w:tr>"); }
        foreach (var r in rows)
        {
            body.Append("<w:tr>");
            for (int i = 0; i < r.Count; i++) Cell(ReportDocument.Cell(r[i]), false, r[i] is double or int or long or float, keyValue && i == 0);
            body.Append("</w:tr>");
        }
        body.Append("</w:tbl><w:p/>");
    }

    private const string DocxStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:color w:val=\"0B2E36\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:b/><w:sz w:val=\"44\"/><w:color w:val=\"0B2E36\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Subtitle\"><w:name w:val=\"Subtitle\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:i/><w:sz w:val=\"24\"/><w:color w:val=\"5B7279\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"360\" w:after=\"120\"/><w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:color=\"12A4B6\"/></w:pBdr><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"30\"/><w:color w:val=\"0F7C8A\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"240\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"26\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Caption\"><w:name w:val=\"caption\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:i/><w:sz w:val=\"18\"/><w:color w:val=\"5B7279\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:ind w:left=\"360\"/></w:pPr></w:style>" +
        "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblCellMar><w:left w:w=\"100\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style></w:styles>";

    private static (int W, int H) PngSize(byte[] png) =>
        png.Length >= 24 ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))) : (0, 0);

    // ─────────────────────────────── PDF ───────────────────────────────

    /// <summary>
    /// A4 PDF with Helvetica text, headings, bullet lists, key-values and ruled tables (images are listed by caption).
    /// Text is encoded in WinAnsi; characters outside it are transliterated.
    /// </summary>
    public static byte[] Pdf(ReportDocument doc)
    {
        const double pageW = 595.28, pageH = 841.89, margin = 50;
        var pages = new List<StringBuilder>();
        StringBuilder page = null!;
        double y = 0;
        void NewPage()
        {
            page = new StringBuilder(); pages.Add(page); y = pageH - margin;
            Text(margin, 28, 8, doc.Footer + $"  ·  {pages.Count}", "F1", "0.36 0.45 0.47");
        }
        void Text(double x, double yy, double size, string s, string font = "F1", string rgb = "0.04 0.18 0.21") =>
            page.Append(CultureInfo.InvariantCulture, $"BT /{font} {size:0.##} Tf {rgb} rg {x:0.##} {yy:0.##} Td ({PdfEsc(s)}) Tj ET\n");
        void Ensure(double h) { if (y - h < margin + 20) NewPage(); }
        IEnumerable<string> Wrap(string s, double size, double width)
        {
            int max = Math.Max(10, (int)(width / (size * 0.5)));
            var words = s.Split(' '); var line = new StringBuilder();
            foreach (var w in words)
            {
                if (line.Length + w.Length + 1 > max && line.Length > 0) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0) yield return line.ToString();
        }
        void Para(string s, double size = 10.5, string font = "F1", double indent = 0, string rgb = "0.04 0.18 0.21")
        {
            foreach (var l in Wrap(s, size, pageW - 2 * margin - indent)) { Ensure(size * 1.45); Text(margin + indent, y - size, size, l, font, rgb); y -= size * 1.45; }
            y -= 4;
        }

        NewPage();
        Para(doc.Title, 20, "F2");
        if (doc.Subtitle is not null) Para(doc.Subtitle, 11, "F1", 0, "0.36 0.45 0.47");
        y -= 6;
        foreach (var b in doc.Blocks)
            switch (b)
            {
                case ReportBlock.Heading h:
                    Ensure(40); y -= 8; Para(h.Text, h.Level <= 1 ? 14 : 12, "F2", 0, "0.06 0.49 0.54");
                    if (h.Level <= 1) { page.Append(CultureInfo.InvariantCulture, $"0.07 0.64 0.71 RG 1.2 w {margin} {y + 4:0.##} m {pageW - margin} {y + 4:0.##} l S\n"); y -= 4; }
                    break;
                case ReportBlock.Paragraph p: Para(p.Text); break;
                case ReportBlock.Bullets l: foreach (var i in l.Items) Para("- " + i, 10.5, "F1", 10); break;
                case ReportBlock.KeyValues kv:
                    foreach (var (k, v) in kv.Items) { Ensure(16); Text(margin, y - 10, 10, k, "F2"); Text(margin + 200, y - 10, 10, v); y -= 15; }
                    y -= 6; break;
                case ReportBlock.Table t:
                {
                    int cols = t.Headers.Count;
                    double tw = pageW - 2 * margin;
                    var lens = Enumerable.Range(0, cols).Select(c => Math.Max(t.Headers[c].Length, t.Rows.Select(r => c < r.Count ? ReportDocument.Cell(r[c]).Length : 0).DefaultIfEmpty(0).Max()) + 2.0).ToArray();
                    double sum = lens.Sum();
                    var cw = lens.Select(l => tw * l / sum).ToArray();
                    double fs = Math.Clamp(9.5 - Math.Max(0, cols - 6) * 0.5, 7, 9.5);
                    void Row(IReadOnlyList<string> cells, bool header)
                    {
                        Ensure(16);
                        if (header) page.Append(CultureInfo.InvariantCulture, $"0.06 0.49 0.54 rg {margin} {y - 14:0.##} {tw:0.##} 15 re f\n");
                        double x = margin;
                        for (int c = 0; c < cols; c++)
                        {
                            string s = c < cells.Count ? cells[c] : "";
                            int maxChars = Math.Max(3, (int)(cw[c] / (fs * 0.52)));
                            if (s.Length > maxChars) s = s[..(maxChars - 1)] + "~";
                            Text(x + 3, y - 10.5, fs, s, header ? "F2" : "F1", header ? "1 1 1" : "0.04 0.18 0.21");
                            x += cw[c];
                        }
                        if (!header) page.Append(CultureInfo.InvariantCulture, $"0.79 0.85 0.84 RG 0.5 w {margin} {y - 14:0.##} m {pageW - margin} {y - 14:0.##} l S\n");
                        y -= 15;
                    }
                    Row(t.Headers, true);
                    foreach (var r in t.Rows) Row(r.Select(ReportDocument.Cell).ToList(), false);
                    y -= 8; break;
                }
                case ReportBlock.Image img: Para($"[Figure: {img.Caption}]", 9.5, "F1", 0, "0.36 0.45 0.47"); break;
            }

        // assemble objects
        var objs = new List<byte[]>();
        int Add(string s) { objs.Add(Latin1(s)); return objs.Count; }
        int catalog = Add(""), pagesObj = Add("");
        int f1 = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int f2 = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int info = Add($"<< /Title ({PdfEsc(doc.Title)}) /Author ({PdfEsc(doc.Author)}) /Producer ({PdfEsc(doc.Footer)}) >>");
        var kids = new List<int>();
        foreach (var p in pages)
        {
            var content = Latin1(p.ToString());
            using var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(content);
            var data = z.ToArray();
            var head = Latin1($"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n");
            objs.Add([.. head, .. data, .. Latin1("\nendstream")]);
            int contentId = objs.Count;
            kids.Add(Add(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {pageW} {pageH}] /Resources << /Font << /F1 {f1} 0 R /F2 {f2} 0 R >> >> /Contents {contentId} 0 R >>")));
        }
        objs[catalog - 1] = Latin1($"<< /Type /Catalog /Pages {pagesObj} 0 R >>");
        objs[pagesObj - 1] = Latin1($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");

        using var ms = new MemoryStream();
        ms.Write(Latin1("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            ms.Write(Latin1($"{i + 1} 0 obj\n")); ms.Write(objs[i]); ms.Write(Latin1("\nendobj\n"));
        }
        long xref = ms.Position;
        var sbx = new StringBuilder($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sbx.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sbx.Append($"trailer\n<< /Size {objs.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Latin1(sbx.ToString()));
        return ms.ToArray();
    }

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Escapes PDF string syntax and maps text to WinAnsi (transliterating characters it lacks).</summary>
    private static string PdfEsc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            string r = c switch
            {
                '(' => "\\(", ')' => "\\)", '\\' => "\\\\",
                '–' => "\x96", '—' => "\x97", '•' => "\x95", '·' => "\xB7", '°' => "\xB0", '×' => "\xD7", '²' => "\xB2", '³' => "\xB3", '±' => "\xB1", 'µ' => "\xB5",
                '‘' or '’' => "'", '“' or '”' => "\"", '≈' => "~", '≤' => "<=", '≥' => ">=", '→' => "->", '←' => "<-", '−' => "-",
                '₀' => "0", '₁' => "1", '₂' => "2", '₃' => "3", '₄' => "4", '⁺' => "+", '⁻' => "-", '…' => "...", '€' => "\x80",
                _ => c < 256 ? c.ToString() : "?",
            };
            sb.Append(r);
        }
        return sb.ToString();
    }
}
