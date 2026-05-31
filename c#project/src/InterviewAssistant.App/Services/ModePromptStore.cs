using System.IO;

using System.Text.Json;



namespace InterviewAssistant.App.Services;



/// <summary>Mode chunk templates (read / type / behavioral).</summary>

public sealed class ModePromptStore

{

    private static readonly string[] ModeKeys = ["read", "type", "error", "behavioral", "closing"];



    private readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);



    public ModePromptStore()

    {

        foreach (var mode in ModeKeys)

            _templates[mode] = "";

        foreach (var (mode, text) in ModePromptSeed.Load())

        {

            if (_templates.ContainsKey(mode))

                _templates[mode] = text;

        }



        LoadFromDisk();

        RepairLegacyStubTemplates();

    }



    private string _sessionMode = "read";

    public string SessionMode
    {
        get => _sessionMode;
        set => _sessionMode = NormalizeSessionMode(value);
    }

    private static string NormalizeSessionMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "type" => "type",
            "error" => "error",
            "behavioral" => "behavioral",
            "closing" => "closing",
            _ => "read",
        };



    public string? GetActiveTemplate()

    {

        return SessionMode switch

        {

            "read" => _templates["read"],

            "type" => _templates["type"],

            "error" => _templates["error"],

            "behavioral" => _templates["behavioral"],

            "closing" => _templates["closing"],

            _ => _templates["read"],

        };

    }



    public IReadOnlyDictionary<string, string> All => _templates;



    public void SetTemplate(string mode, string text)

    {

        var key = mode.Trim().ToLowerInvariant();

        if (_templates.ContainsKey(key))

            _templates[key] = text ?? "";

    }



    public void SaveToDisk()

    {

        var root = Path.Combine(

            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),

            ".interview_assistant");

        Directory.CreateDirectory(root);

        var path = Path.Combine(root, "mode_prompts.json");

        var payload = new Dictionary<string, string>

        {

            ["read"] = _templates["read"],

            ["type"] = _templates["type"],

            ["error"] = _templates["error"],

            ["behavioral"] = _templates["behavioral"],

            ["closing"] = _templates["closing"],

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



    private void RepairLegacyStubTemplates()

    {

        var seed = ModePromptSeed.Load();

        var changed = false;

        foreach (var mode in ModeKeys)

        {

            if (!seed.TryGetValue(mode, out var seedText) || string.IsNullOrEmpty(seedText))

                continue;

            if (!_templates.TryGetValue(mode, out var current) || !IsLegacyStub(mode, current))

                continue;

            _templates[mode] = seedText;

            changed = true;

        }



        if (changed)

            SaveToDisk();

    }



    private static bool IsLegacyStub(string mode, string text)

    {

        return mode switch

        {

            "read" => text.Contains("Otherwise output SHORT ANSWER, DETAILED ANSWER", StringComparison.Ordinal),

            "type" => text.Contains("Output everything inside one fenced code block.", StringComparison.Ordinal)

                && !text.Contains("[SAY-n]", StringComparison.Ordinal),

            "error" => text.Contains("ERROR MODE", StringComparison.Ordinal)

                && !text.Contains("FULL CORRECTED CODE", StringComparison.Ordinal),

            "behavioral" => text.Contains("Output SHORT ANSWER and STORY ANSWER sections.", StringComparison.Ordinal),

            _ => false,

        };

    }



    private static string UserPromptsPath() => Path.Combine(

        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),

        ".interview_assistant",

        "mode_prompts.json");

}


