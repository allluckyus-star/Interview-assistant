using System.Windows;

namespace InterviewAssistant.App.Ui;

public static class ToastService
{
    private static ToastOverlay? _overlay;

    public static void Register(ToastOverlay overlay) => _overlay = overlay;

    public static void Show(string message, ToastLevel level = ToastLevel.Success, int maxChars = 96)
    {
        if (_overlay is null)
            return;
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.BeginInvoke(() => _overlay.Show(message, level, maxChars));
    }

    public static void Clear()
    {
        if (_overlay is null)
            return;
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.BeginInvoke(() => _overlay.ClearAll());
    }
}
