namespace InterviewAssistant.App.Services;

public static class ChunkPromptBuilder
{
    public static (string CleanedIntent, string FinalPrompt) Build(string rawChunk, string? templateOverride)
    {
        var cleaned = (rawChunk ?? "").Trim();
        if (templateOverride is null)
            return (cleaned, cleaned);
        var template = templateOverride.Trim();
        if (template.Length == 0)
            return (cleaned, cleaned);
        var final = template.Replace("{cleaned_interviewer_intent}", cleaned, StringComparison.Ordinal);
        return (cleaned, final);
    }
}
