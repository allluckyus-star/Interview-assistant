namespace InterviewAssistant.App.Ui;

internal static class ToastMessages
{
    private const int MaxChars = 96;
    private const int CapturedPreviewMaxWords = 6;
    private const int CapturedPreviewMaxChars = 48;

    public static string Trim(string? text, int maxChars = MaxChars)
    {
        var t = (text ?? "").Trim();
        if (t.Length <= maxChars)
            return t;
        return t[..(maxChars - 1)] + "…";
    }

    /// <summary>Full string pasted into ChatGPT: Captured text: """…"""</summary>
    public static string FormatCapturedTextForPaste(string? rawOcr)
    {
        var t = (rawOcr ?? "").Trim();
        if (t.Length == 0)
            return "Captured text: \"\"\"\"\"\"";
        return $"Captured text: \"\"\"{t}\"\"\"";
    }

    /// <summary>Short toast only — not pasted into ChatGPT.</summary>
    public static string ForSnipRecognizedToast(string? rawOcr)
    {
        var t = (rawOcr ?? "").Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        while (t.Contains("  ", StringComparison.Ordinal))
            t = t.Replace("  ", " ", StringComparison.Ordinal);

        if (t.Length == 0)
            return "No text recognized.";

        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > CapturedPreviewMaxWords)
            t = string.Join(' ', words, 0, CapturedPreviewMaxWords) + "...";
        else if (t.Length > CapturedPreviewMaxChars)
            t = t[..(CapturedPreviewMaxChars - 3)] + "...";

        return $"Recognized: {t}";
    }

    public static string ForSendFailure(GptSendResult parsed)
    {
        var err = (parsed.Error ?? "").Trim();
        if (err.Contains("send_confirmation_timeout", StringComparison.OrdinalIgnoreCase))
            return "Send failed. Timed out.";
        if (!string.IsNullOrEmpty(err))
            return Trim($"Send failed. {FirstReason(err)}");
        if (parsed.Ok && !string.Equals(parsed.Phase, "sent", StringComparison.OrdinalIgnoreCase))
            return Trim($"Send failed. Bad phase ({parsed.Phase ?? "?"}).");
        return "Send failed. Not confirmed.";
    }

    public static string ForException(Exception ex) =>
        Trim($"Failed. {FirstReason(ex.Message)}");

    public static string ForFileSaveException(Exception ex) =>
        Trim($"Save failed. {FirstReason(ex.Message)}");

    /// <summary>Maps interview status lines to short toast text. Returns null to skip toast (static UI hint only).</summary>
    public static (string Text, ToastLevel Level)? ForInterviewStatus(string message)
    {
        var m = message.Trim();
        if (m.Length == 0)
            return null;
        if (m.Contains("End = send", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Double-click", StringComparison.OrdinalIgnoreCase))
            return null;

        if (m.StartsWith("No caption", StringComparison.OrdinalIgnoreCase))
            return ("No caption yet.", ToastLevel.Warning);
        if (m.StartsWith("Nothing to skip", StringComparison.OrdinalIgnoreCase))
            return ("Nothing to skip.", ToastLevel.Warning);
        if (m.StartsWith("Skipped pending", StringComparison.OrdinalIgnoreCase))
            return ("Skipped.", ToastLevel.Info);
        if (m.StartsWith("End: captured", StringComparison.OrdinalIgnoreCase))
            return ("Chunk captured.", ToastLevel.Success);
        if (m.Contains("Edit rejected", StringComparison.OrdinalIgnoreCase))
            return ("Rejected.", ToastLevel.Warning);
        if (m.Contains("Edit empty", StringComparison.OrdinalIgnoreCase))
            return ("Edit empty.", ToastLevel.Warning);
        if (m.Contains("edit empty", StringComparison.OrdinalIgnoreCase))
            return ("Edit empty.", ToastLevel.Warning);
        if (m.Contains("sent to bridge", StringComparison.OrdinalIgnoreCase))
            return ("Sent to bridge.", ToastLevel.Success);
        if (m.Contains("Caption saved", StringComparison.OrdinalIgnoreCase))
            return ("Caption saved.", ToastLevel.Success);

        return (Trim(m), ToastLevel.Info);
    }

    private static string FirstReason(string message)
    {
        var t = message.Trim();
        if (t.Length == 0)
            return "Unknown error.";
        var cut = t.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (cut.Length > 56)
            cut = cut[..53] + "…";
        return cut;
    }
}
