using System.IO;
using System.Text;

namespace InterviewAssistant.App.Services;

public sealed class InterviewHistory
{
    private readonly object _lock = new();
    private readonly List<(string Role, string Text, string Source)> _events = [];

    public void Clear()
    {
        lock (_lock)
            _events.Clear();
    }

    public bool HasInterviewerLines()
    {
        lock (_lock)
            return _events.Any(e => e.Role == "interviewer" && !string.IsNullOrWhiteSpace(e.Text));
    }

    public void AppendInterviewer(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (_lock)
            _events.Add(("interviewer", text.Trim(), source));
    }

    public void AppendGpt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        lock (_lock)
            _events.Add(("gpt", text.Trim(), "final"));
    }

    public string BuildTranscriptText()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var (role, text, source) in _events)
            {
                var label = role == "interviewer" ? $"Interviewer ({source})" : "GPT";
                sb.AppendLine($"--- {label} ---");
                sb.AppendLine(text);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
    }

    public static string DefaultSavePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "InterviewAssistant");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        return Path.Combine(dir, $"interview_transcript_{stamp}.txt");
    }
}
