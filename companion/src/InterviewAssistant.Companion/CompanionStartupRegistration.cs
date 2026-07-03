using System.IO;
using System.Text.Json;
using InterviewAssistant.App.Services;
using Microsoft.Win32;

namespace InterviewAssistant.Companion;

/// <summary>Register Companion in HKCU Run so it starts when Windows logs in.</summary>
internal static class CompanionStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "InterviewAssistantCompanion";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string PreferencePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".interview_assistant",
            "companion_startup.json");

    public static bool IsEnabledInRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(RunValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA startup] registry read failed: {ex.Message}");
            return false;
        }
    }

    public static bool IsUserPreferenceEnabled()
    {
        try
        {
            if (!File.Exists(PreferencePath))
                return true;

            using var doc = JsonDocument.Parse(File.ReadAllText(PreferencePath));
            if (doc.RootElement.TryGetProperty("run_at_login", out var el))
                return el.ValueKind == JsonValueKind.True;

            return true;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA startup] preference read failed: {ex.Message}");
            return true;
        }
    }

    public static bool ShouldRunAtLogin() => IsUserPreferenceEnabled();

    public static bool Enable()
    {
        var exePath = ResolveExePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            StartupDiagnostics.Log("[IA startup] enable failed — exe path unknown");
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(RunValueName, QuoteExePath(exePath));
            SavePreference(true);
            StartupDiagnostics.Log($"[IA startup] registered Run key: {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA startup] enable failed: {ex.Message}");
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            SavePreference(false);
            StartupDiagnostics.Log("[IA startup] removed Run key");
            return true;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA startup] disable failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Apply user preference — register or remove Run key.</summary>
    public static void ApplyPreference()
    {
        if (ShouldRunAtLogin())
            Enable();
        else
            Disable();
    }

    public static bool SetRunAtLogin(bool enabled)
    {
        return enabled ? Enable() : Disable();
    }

    private static void SavePreference(bool enabled)
    {
        try
        {
            var dir = Path.GetDirectoryName(PreferencePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = new Dictionary<string, bool> { ["run_at_login"] = enabled };
            File.WriteAllText(PreferencePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA startup] preference save failed: {ex.Message}");
        }
    }

    private static string? ResolveExePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return Path.GetFullPath(path);
        }
        catch
        {
            // ignore
        }

        try
        {
            var module = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(module) && File.Exists(module))
                return Path.GetFullPath(module);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string QuoteExePath(string exePath) =>
        exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
}
