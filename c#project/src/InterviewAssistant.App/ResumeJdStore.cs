namespace InterviewAssistant.App;

/// <summary>Active resume + JD text (backed by <see cref="ResumeJdHistoryStore"/>).</summary>
public static class ResumeJdStore
{
    public static (string ResumeText, string JdText) Load() => ResumeJdHistoryStore.GetActiveTexts();

    public static void Save(string resumeText, string jdText)
    {
        var snap = ResumeJdHistoryStore.LoadSnapshot();
        if (!string.IsNullOrWhiteSpace(resumeText))
        {
            var name = string.IsNullOrWhiteSpace(snap.ActiveResumeName)
                ? "Active resume"
                : snap.ActiveResumeName;
            ResumeJdHistoryStore.SaveResume(name, resumeText);
        }

        if (!string.IsNullOrWhiteSpace(jdText))
        {
            var name = string.IsNullOrWhiteSpace(snap.ActiveJdName)
                ? "Active JD"
                : snap.ActiveJdName;
            ResumeJdHistoryStore.SaveJd(name, jdText);
        }
    }
}
