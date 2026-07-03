using System.IO;
using System.Text.Json;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

internal sealed class ShareXListenState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".interview_assistant",
            "sharex_listen.json");

    private readonly object _gate = new();
    private bool _imageEnabled;
    private bool _textEnabled;

    public ShareXListenState()
    {
        LoadFromDisk();
    }

    public bool ImageEnabled
    {
        get { lock (_gate) return _imageEnabled; }
    }

    public bool TextEnabled
    {
        get { lock (_gate) return _textEnabled; }
    }

    public (bool Image, bool Text) Snapshot()
    {
        lock (_gate) return (_imageEnabled, _textEnabled);
    }

    public (bool Image, bool Text) Update(bool? imageEnabled, bool? textEnabled)
    {
        lock (_gate)
        {
            if (imageEnabled.HasValue) _imageEnabled = imageEnabled.Value;
            if (textEnabled.HasValue) _textEnabled = textEnabled.Value;
            SaveToDiskLocked();
            return (_imageEnabled, _textEnabled);
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            var path = StorePath;
            if (!File.Exists(path))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            lock (_gate)
            {
                if (root.TryGetProperty("image_enabled", out var img))
                    _imageEnabled = img.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("text_enabled", out var txt))
                    _textEnabled = txt.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] listen load failed: {ex.Message}");
        }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = new Dictionary<string, bool>
            {
                ["image_enabled"] = _imageEnabled,
                ["text_enabled"] = _textEnabled,
            };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] listen save failed: {ex.Message}");
        }
    }
}

internal sealed record ShareXShortcutBinding(
    bool Ctrl,
    bool Alt,
    bool Shift,
    int KeyVk,
    int[]? AltKeyVks = null)
{
    public int[] EffectiveKeyVks =>
        AltKeyVks is { Length: > 0 }
            ? AltKeyVks.Prepend(KeyVk).Where(v => v > 0).Distinct().ToArray()
            : KeyVk > 0 ? [KeyVk] : [];
}
