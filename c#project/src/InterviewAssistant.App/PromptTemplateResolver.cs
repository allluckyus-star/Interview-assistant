using System.IO;
using System.Text;

namespace InterviewAssistant.App;

/// <summary>
/// Resolves <c>prompt_resume_summary.txt</c>, <c>prompt_jd_summary.txt</c>, <c>prompt_initial_interview.txt</c>
/// from the app output directory (<c>Assets/</c> or base dir).
/// </summary>
public static class PromptTemplateResolver
{
    private const string ResumeFile = "prompt_resume_summary.txt";
    private const string JdFile = "prompt_jd_summary.txt";
    private const string InitialInterviewFile = "prompt_initial_interview.txt";

    public static string TryReadResumeTemplate() => TryReadFile(ResumeFile);

    public static string TryReadJdTemplate() => TryReadFile(JdFile);

    public static string TryReadInitialInterviewTemplate() => TryReadFile(InitialInterviewFile);

    public static string ReadPrepTemplate(string key) => key switch
    {
        "resume_summary" => TryReadResumeTemplate(),
        "jd_summary" => TryReadJdTemplate(),
        "initial_interview" => TryReadInitialInterviewTemplate(),
        _ => "",
    };

    public static void SavePrepTemplate(string key, string text)
    {
        var fileName = key switch
        {
            "resume_summary" => ResumeFile,
            "jd_summary" => JdFile,
            "initial_interview" => InitialInterviewFile,
            _ => throw new ArgumentException($"Unknown prep template key: {key}", nameof(key)),
        };
        var path = ResolveWritablePath(fileName);
        File.WriteAllText(path, text ?? "", Encoding.UTF8);
    }

    public static string BuildResumePrompt(string resumeText, string templateBody)
    {
        var t = templateBody.Trim();
        if (string.IsNullOrEmpty(t))
            return "";
        return t.Replace("{resume_text}", resumeText ?? "", StringComparison.Ordinal);
    }

    public static string BuildJdPrompt(string jdText, string templateBody)
    {
        var t = templateBody.Trim();
        if (string.IsNullOrEmpty(t))
            return "";
        return t.Replace("{jd_text}", jdText ?? "", StringComparison.Ordinal);
    }

    private static string TryReadFile(string fileName)
    {
        foreach (var path in CandidatePaths(fileName))
        {
            if (!File.Exists(path))
                continue;
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch
            {
                return "";
            }
        }

        return "";
    }

    private static string ResolveWritablePath(string fileName)
    {
        foreach (var path in CandidatePaths(fileName))
        {
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
    }

    private static IEnumerable<string> CandidatePaths(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Assets", fileName);
        yield return Path.Combine(baseDir, fileName);
    }
}
