using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterviewAssistant.App;

/// <summary>Named resume / JD history under <c>~/.interview_assistant/saved_context_history.json</c>.</summary>
public static class ResumeJdHistoryStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".interview_assistant");

    private static readonly string HistoryPath = Path.Combine(Dir, "saved_context_history.json");
    private static readonly string LegacyPath = Path.Combine(Dir, "saved_resume_jd.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public sealed class NamedEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("updated_utc")]
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class Snapshot
    {
        [JsonPropertyName("active_resume")]
        public string ActiveResumeName { get; set; } = "";

        [JsonPropertyName("active_jd")]
        public string ActiveJdName { get; set; } = "";

        [JsonPropertyName("resumes")]
        public List<NamedEntry> Resumes { get; set; } = [];

        [JsonPropertyName("jds")]
        public List<NamedEntry> Jds { get; set; } = [];
    }

    public static Snapshot LoadSnapshot()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var snap = JsonSerializer.Deserialize<Snapshot>(json, JsonOptions);
                if (snap is not null)
                    return NormalizeSnapshot(snap);
            }

            return MigrateLegacyIfPresent();
        }
        catch
        {
            return new Snapshot();
        }
    }

    public static (string ResumeText, string JdText) GetActiveTexts()
    {
        var snap = LoadSnapshot();
        return (FindText(snap.Resumes, snap.ActiveResumeName), FindText(snap.Jds, snap.ActiveJdName));
    }

    public static IReadOnlyList<NamedEntry> ListResumes() => LoadSnapshot().Resumes;

    public static IReadOnlyList<NamedEntry> ListJds() => LoadSnapshot().Jds;

    public static NamedEntry SaveResume(string name, string text)
    {
        var snap = LoadSnapshot();
        var entry = Upsert(snap.Resumes, name, text);
        snap.ActiveResumeName = entry.Name;
        Persist(snap);
        return entry;
    }

    public static NamedEntry SaveJd(string name, string text)
    {
        var snap = LoadSnapshot();
        var entry = Upsert(snap.Jds, name, text);
        snap.ActiveJdName = entry.Name;
        Persist(snap);
        return entry;
    }

    public static bool DeleteResume(string name)
    {
        var snap = LoadSnapshot();
        var key = NormalizeName(name);
        if (key.Length == 0)
            return false;

        var removed = snap.Resumes.RemoveAll(e => NameEquals(e.Name, key)) > 0;
        if (!removed)
            return false;

        if (NameEquals(snap.ActiveResumeName, key))
            snap.ActiveResumeName = snap.Resumes.FirstOrDefault()?.Name ?? "";
        Persist(snap);
        return true;
    }

    public static bool DeleteJd(string name)
    {
        var snap = LoadSnapshot();
        var key = NormalizeName(name);
        if (key.Length == 0)
            return false;

        var removed = snap.Jds.RemoveAll(e => NameEquals(e.Name, key)) > 0;
        if (!removed)
            return false;

        if (NameEquals(snap.ActiveJdName, key))
            snap.ActiveJdName = snap.Jds.FirstOrDefault()?.Name ?? "";
        Persist(snap);
        return true;
    }

    public static void SetActiveResume(string name)
    {
        var snap = LoadSnapshot();
        var key = NormalizeName(name);
        if (!snap.Resumes.Any(e => NameEquals(e.Name, key)))
            return;
        snap.ActiveResumeName = snap.Resumes.First(e => NameEquals(e.Name, key)).Name;
        Persist(snap);
    }

    public static void SetActiveJd(string name)
    {
        var snap = LoadSnapshot();
        var key = NormalizeName(name);
        if (!snap.Jds.Any(e => NameEquals(e.Name, key)))
            return;
        snap.ActiveJdName = snap.Jds.First(e => NameEquals(e.Name, key)).Name;
        Persist(snap);
    }

    public static string NormalizeName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return "";

        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');

        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    private static NamedEntry Upsert(List<NamedEntry> list, string name, string text)
    {
        var key = NormalizeName(name);
        if (key.Length == 0)
            throw new ArgumentException("Name is required.");

        var body = (text ?? "").Trim();
        if (body.Length == 0)
            throw new ArgumentException("Content is required.");

        var existing = list.FirstOrDefault(e => NameEquals(e.Name, key));
        if (existing is not null)
        {
            existing.Text = body;
            existing.UpdatedUtc = DateTime.UtcNow;
            return existing;
        }

        var entry = new NamedEntry
        {
            Name = key,
            Text = body,
            UpdatedUtc = DateTime.UtcNow,
        };
        list.Insert(0, entry);
        return entry;
    }

    private static Snapshot NormalizeSnapshot(Snapshot snap)
    {
        snap.Resumes ??= [];
        snap.Jds ??= [];
        snap.Resumes = snap.Resumes
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .OrderByDescending(e => e.UpdatedUtc)
            .ToList();
        snap.Jds = snap.Jds
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .OrderByDescending(e => e.UpdatedUtc)
            .ToList();

        if (string.IsNullOrWhiteSpace(snap.ActiveResumeName) && snap.Resumes.Count > 0)
            snap.ActiveResumeName = snap.Resumes[0].Name;
        if (string.IsNullOrWhiteSpace(snap.ActiveJdName) && snap.Jds.Count > 0)
            snap.ActiveJdName = snap.Jds[0].Name;

        return snap;
    }

    private static Snapshot MigrateLegacyIfPresent()
    {
        var snap = new Snapshot();
        try
        {
            if (!File.Exists(LegacyPath))
                return snap;

            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyPath));
            var root = doc.RootElement;
            var resume = root.TryGetProperty("resume_text", out var r) ? r.GetString() ?? "" : "";
            var jd = root.TryGetProperty("jd_text", out var j) ? j.GetString() ?? "" : "";

            if (!string.IsNullOrWhiteSpace(resume))
            {
                snap.Resumes.Add(new NamedEntry
                {
                    Name = "Imported resume",
                    Text = resume.Trim(),
                    UpdatedUtc = DateTime.UtcNow,
                });
                snap.ActiveResumeName = snap.Resumes[0].Name;
            }

            if (!string.IsNullOrWhiteSpace(jd))
            {
                snap.Jds.Add(new NamedEntry
                {
                    Name = "Imported JD",
                    Text = jd.Trim(),
                    UpdatedUtc = DateTime.UtcNow,
                });
                snap.ActiveJdName = snap.Jds[0].Name;
            }

            if (snap.Resumes.Count > 0 || snap.Jds.Count > 0)
                Persist(snap);
        }
        catch
        {
            // ignore
        }

        return snap;
    }

    private static void Persist(Snapshot snap)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(NormalizeSnapshot(snap), JsonOptions);
        File.WriteAllText(HistoryPath, json);

        var (resume, jd) = GetActiveTexts();
        var legacy = new { resume_text = resume, jd_text = jd };
        File.WriteAllText(LegacyPath, JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindText(List<NamedEntry> list, string activeName)
    {
        if (string.IsNullOrWhiteSpace(activeName))
            return "";
        return list.FirstOrDefault(e => NameEquals(e.Name, activeName))?.Text ?? "";
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(NormalizeName(a), NormalizeName(b), StringComparison.OrdinalIgnoreCase);
}
