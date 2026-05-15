using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterviewAssistant.App;

/// <summary>
/// Persists resume + JD to the same file as <c>local_profile_store.py</c> (<c>~/.interview_assistant/saved_resume_jd.json</c>).
/// </summary>
public static class ResumeJdStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".interview_assistant");

    private static readonly string FilePath = Path.Combine(Dir, "saved_resume_jd.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private sealed class Dto
    {
        [JsonPropertyName("resume_text")]
        public string ResumeText { get; set; } = "";

        [JsonPropertyName("jd_text")]
        public string JdText { get; set; } = "";
    }

    public static (string ResumeText, string JdText) Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return ("", "");
            var json = File.ReadAllText(FilePath);
            var dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
            if (dto is null)
                return ("", "");
            return (dto.ResumeText ?? "", dto.JdText ?? "");
        }
        catch
        {
            return ("", "");
        }
    }

    public static void Save(string resumeText, string jdText)
    {
        Directory.CreateDirectory(Dir);
        var dto = new Dto { ResumeText = resumeText ?? "", JdText = jdText ?? "" };
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
