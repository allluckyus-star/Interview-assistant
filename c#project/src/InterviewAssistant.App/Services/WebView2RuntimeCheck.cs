using Microsoft.Web.WebView2.Core;

namespace InterviewAssistant.App.Services;

public static class WebView2RuntimeCheck
{
    public static bool TryGetInstalledVersion(out string? version, out string? error)
    {
        version = null;
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "WebView2 requires Windows.";
            return false;
        }

        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
