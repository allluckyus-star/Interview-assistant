namespace InterviewAssistant.App.Services;

public static class ChunkPromptBuilder
{
    /// <summary>Mode template only (legacy).</summary>
    public static (string CleanedIntent, string FinalPrompt) Build(string rawChunk, string? modeTemplate) =>
        Build(rawChunk, modeTemplate, null);

    /// <summary>Language wrapper + mode template + interviewer intent.</summary>
    public static (string CleanedIntent, string FinalPrompt) Build(
        string rawChunk,
        string? modeTemplate,
        string? languageTemplate)
    {
        var cleaned = (rawChunk ?? "").Trim();

        string modeSection;
        if (string.IsNullOrWhiteSpace(modeTemplate))
            modeSection = cleaned;
        else
            modeSection = modeTemplate.Trim().Replace("{cleaned_interviewer_intent}", cleaned, StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(languageTemplate))
            return (cleaned, modeSection);

        var final = languageTemplate.Trim()
            .Replace("{mode_prompt}", modeSection, StringComparison.Ordinal)
            .Replace("{cleaned_interviewer_intent}", cleaned, StringComparison.Ordinal);
        return (cleaned, final);
    }
}
