using System.IO;
using System.Text;

namespace InterviewAssistant.App;

/// <summary>
/// Resolves <c>prompt_resume_summary.txt</c>, <c>prompt_jd_summary.txt</c>, <c>prompt_initial_interview.txt</c>
/// from the app output directory or by walking up from <see cref="AppContext.BaseDirectory"/> toward the repo root.
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
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p))
            {
                try
                {
                    return File.ReadAllText(p, Encoding.UTF8);
                }
                catch
                {
                    return "";
                }
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return "";
    }

    private static string ResolveWritablePath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p))
                return p;
            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}
