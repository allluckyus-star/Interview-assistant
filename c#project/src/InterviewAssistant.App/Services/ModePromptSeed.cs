using System.IO;
using System.Text.Json;

namespace InterviewAssistant.App.Services;

internal static class ModePromptSeed
{
    private static Dictionary<string, string>? _cache;

    public static IReadOnlyDictionary<string, string> Load()
    {
        if (_cache is not null)
            return _cache;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "mode_prompts.seed.json");
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
                    if (loaded.Count >= 3)
                    {
                        MergeAssetPromptFromFile(loaded, "closing", "closing_mode_prompt.txt");
                        MergeAssetPromptFromFile(loaded, "error", "error_mode_prompt.txt");
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
            ["read"] = "",
            ["type"] = "",
            ["behavioral"] = "",
            ["closing"] = "",
            ["error"] = "",
        };
        MergeAssetPromptFromFile(_cache, "closing", "closing_mode_prompt.txt");
        MergeAssetPromptFromFile(_cache, "error", "error_mode_prompt.txt");
        return _cache;
    }

    private static void MergeAssetPromptFromFile(Dictionary<string, string> loaded, string key, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path))
            return;
        try
        {
            var text = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text))
                loaded[key] = text;
        }
        catch
        {
            // ignore
        }
    }
}
