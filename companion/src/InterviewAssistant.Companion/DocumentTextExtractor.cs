using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InterviewAssistant.Companion;

public static class DocumentTextExtractor
{
    public static string Extract(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" => ExtractTxt(bytes),
            ".pdf" => ExtractPdf(bytes),
            ".docx" => ExtractDocx(bytes),
            _ => throw new NotSupportedException(
                $"Unsupported file type: {(string.IsNullOrEmpty(ext) ? "(none)" : ext)}. Use .txt, .pdf, or .docx."),
        };
    }

    private static string ExtractTxt(byte[] bytes)
    {
        try
        {
            return NormalizeExtractedText(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return NormalizeExtractedText(Encoding.Default.GetString(bytes));
        }
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = PdfDocument.Open(ms);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
                continue;

            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            foreach (var line in GroupWordsIntoLines(words))
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                    sb.AppendLine();
                sb.Append(line);
            }
        }

        return NormalizeExtractedText(sb.ToString());
    }

    private static IEnumerable<string> GroupWordsIntoLines(IReadOnlyList<Word> words)
    {
        const double lineTolerance = 4.0;
        var ordered = words
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var lines = new List<List<Word>>();
        foreach (var word in ordered)
        {
            var bottom = word.BoundingBox.Bottom;
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l[0].BoundingBox.Bottom - bottom) <= lineTolerance);
            if (line is null)
                lines.Add([word]);
            else
                line.Add(word);
        }

        return lines
            .OrderByDescending(l => l[0].BoundingBox.Bottom)
            .Select(l => string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return "";

        var sb = new StringBuilder();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendParagraphBlock(sb, paragraph);
                    break;
                case Table table:
                    AppendTable(sb, table);
                    break;
            }
        }

        return NormalizeExtractedText(sb.ToString());
    }

    private static void AppendParagraphBlock(StringBuilder sb, Paragraph paragraph)
    {
        var text = ExtractParagraphText(paragraph);
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (sb.Length > 0)
            sb.AppendLine();
        sb.Append(text);
    }

    private static string ExtractParagraphText(Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var run in paragraph.Descendants<Run>())
        {
            foreach (var node in run.ChildElements)
            {
                switch (node)
                {
                    case Text text:
                        sb.Append(text.Text);
                        break;
                    case TabChar:
                        sb.Append("    ");
                        break;
                    case Break br when br.Type?.Value != BreakValues.Page:
                        sb.AppendLine();
                        break;
                }
            }
        }

        if (sb.Length == 0)
            sb.Append(string.Concat(paragraph.Descendants<Text>().Select(t => t.Text)));

        return CollapseInlineWhitespace(sb.ToString());
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        foreach (var row in table.Elements<TableRow>())
        {
            var cellTexts = row.Elements<TableCell>()
                .Select(ExtractCellText)
                .Where(t => t.Length > 0)
                .ToList();
            if (cellTexts.Count == 0)
                continue;

            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append(string.Join(Environment.NewLine, cellTexts));
        }
    }

    private static string ExtractCellText(TableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var element in cell.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendParagraphBlock(sb, paragraph);
                    break;
                case Table nested:
                    AppendTable(sb, nested);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string CollapseInlineWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join(
            Environment.NewLine,
            lines.Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"[ \t]{2,}", "    "))
                .Where(l => l.Length > 0));
    }

    private static string NormalizeExtractedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();
        var blankRun = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun <= 1 && sb.Length > 0)
                    sb.AppendLine();
                continue;
            }

            blankRun = 0;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(line);
        }

        return sb.ToString();
    }
}
