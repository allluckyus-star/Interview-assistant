using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

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
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return Encoding.Default.GetString(bytes);
        }
    }

    private static string ExtractPdf(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = PdfDocument.Open(ms);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var text = page.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null)
            return "";
        return body.InnerText?.Trim() ?? "";
    }
}
