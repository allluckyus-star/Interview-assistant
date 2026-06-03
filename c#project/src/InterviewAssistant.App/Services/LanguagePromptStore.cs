using System.IO;
using System.Text.Json;

namespace InterviewAssistant.App.Services;

/// <summary>Output language wrappers (english / chinese+pinyin) combined with mode prompts.</summary>
public sealed class LanguagePromptStore
{
    private static readonly string[] LanguageKeys = ["english", "chinese"];

    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);

    public LanguagePromptStore()
    {
        foreach (var key in LanguageKeys)
            _templates[key] = "";

        foreach (var (key, text) in LanguagePromptSeed.Load())
        {
            if (_templates.ContainsKey(key))
                _templates[key] = text;
        }

        LoadFromDisk();
    }

    private string _sessionLanguage = "english";

    public string SessionLanguage
    {
        get => _sessionLanguage;
        set => _sessionLanguage = NormalizeSessionLanguage(value);
    }

    private static string NormalizeSessionLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "chinese" or "zh" or "cn" or "中文" => "chinese",
            _ => "english",
        };

    public string? GetActiveTemplate() =>
        SessionLanguage switch
        {
            "chinese" => _templates["chinese"],
            _ => _templates["english"],
        };

    public IReadOnlyDictionary<string, string> All => _templates;

    public void SetTemplate(string language, string text)
    {
        var key = NormalizeSessionLanguage(language);
        if (_templates.ContainsKey(key))
            _templates[key] = text ?? "";
    }

    public void SaveToDisk()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".interview_assistant");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "language_prompts.json");
        var payload = new Dictionary<string, string>
        {
            ["english"] = _templates["english"],
            ["chinese"] = _templates["chinese"],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadFromDisk()
    {
        var path = UserPromptsPath();
        if (!File.Exists(path))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && _templates.ContainsKey(prop.Name))
                {
                    var t = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                        _templates[prop.Name] = t!;
                }
            }
        }
        catch
        {
            // ignore corrupt file
        }
    }

    private static string UserPromptsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".interview_assistant",
        "language_prompts.json");
}
