using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterviewAssistant.App;

/// <summary>Saves / loads interview Q+A pair sessions under
/// <c>{companion.exe folder}/history/</c>.</summary>
public static class InterviewHistoryStore
{
    /// <summary>Exe directory — not <see cref="AppContext.BaseDirectory"/>, which for
    /// single-file publish points at a temp extract folder under %TEMP%\.net\.</summary>
    private static string Dir => Path.Combine(ResolveAppRoot(), "history");

    private static string ResolveAppRoot()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
                return dir;
        }

        return AppContext.BaseDirectory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── DTOs ────────────────────────────────────────────────────────────────

    public sealed class QaPair
    {
        [JsonPropertyName("caption")]  public string Caption { get; set; } = "";
        [JsonPropertyName("result")]   public string Result  { get; set; } = "";
        [JsonPropertyName("ts_utc")]   public DateTime TsUtc { get; set; }
    }

    public sealed class Session
    {
        [JsonPropertyName("created_utc")] public DateTime CreatedUtc { get; set; }
        [JsonPropertyName("pairs")]       public List<QaPair> Pairs  { get; set; } = [];
    }

    public sealed class SessionMeta
    {
        public string   Name       { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public int      PairCount  { get; set; }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public static string Save(Session session)
    {
        Directory.CreateDirectory(Dir);
        var local = session.CreatedUtc.ToLocalTime();
        var name  = $"interview-{local:yyyy-MM-dd-HH-mm}.json";
        var path  = Path.Combine(Dir, name);
        var n = 1;
        while (File.Exists(path))
        {
            name = $"interview-{local:yyyy-MM-dd-HH-mm}-{n++}.json";
            path = Path.Combine(Dir, name);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(session, JsonOptions));
        return name;
    }

    public static IReadOnlyList<SessionMeta> List()
    {
        if (!Directory.Exists(Dir)) return [];
        return new DirectoryInfo(Dir)
            .EnumerateFiles("*.json")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f =>
            {
                var created = f.LastWriteTime.ToUniversalTime();
                var count   = 0;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f.FullName));
                    if (doc.RootElement.TryGetProperty("pairs", out var p))
                        count = p.GetArrayLength();
                    if (doc.RootElement.TryGetProperty("created_utc", out var c))
                        created = c.GetDateTime();
                }
                catch { /* skip */ }
                return new SessionMeta { Name = f.Name, CreatedUtc = created, PairCount = count };
            })
            .ToList();
    }

    public static Session? Load(string filename)
    {
        var path = SafePath(filename);
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Session>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    public static bool Rename(string oldName, string newName)
    {
        var src = SafePath(oldName);
        if (src is null || !File.Exists(src)) return false;

        var normalized = NormalizeName(newName);
        if (normalized.Length == 0) return false;
        if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            normalized += ".json";

        var dst = Path.Combine(Dir, normalized);
        if (File.Exists(dst)) return false;
        File.Move(src, dst);
        return true;
    }

    public static bool Delete(string filename)
    {
        var path = SafePath(filename);
        if (path is null || !File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? SafePath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var name = Path.GetFileName(filename);
        if (name != filename) return null; // reject path traversal
        return Path.Combine(Dir, name);
    }

    private static string NormalizeName(string name)
    {
        var t = (name ?? "").Trim();
        if (t.Length == 0) return "";
        foreach (var c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        return t.Length > 100 ? t[..100] : t;
    }
}
