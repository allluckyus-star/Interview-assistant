using System.IO;

namespace InterviewAssistant.App.Services;

/// <summary>Minimal stub so linked CaptionDiagnostics compiles in Companion.</summary>
public static class StartupDiagnostics
{
    public static string LogDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "InterviewAssistant");
}
