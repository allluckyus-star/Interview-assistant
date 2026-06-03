using System.IO;
using System.Text.Json;

namespace InterviewAssistant.App.Services;

internal static class LanguagePromptSeed
{
    private static Dictionary<string, string>? _cache;

    public static IReadOnlyDictionary<string, string> Load()
    {
        if (_cache is not null)
            return _cache;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "language_prompts.seed.json");
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            loaded[prop.Name] = prop.Value.GetString() ?? "";
                    }
                    if (loaded.Count >= 2)
                    {
                        _cache = loaded;
                        return _cache;
                    }
                }
            }
            catch
            {
                // fall through
            }
        }

        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "",
            ["chinese"] = "",
        };
        return _cache;
    }
}
