using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>Polls Windows Live Captions via UI Automation (same approach as live.py).</summary>
public sealed class LiveCaptionsCaptureService : IDisposable
{
    private readonly CaptionState _state;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;

    public LiveCaptionsCaptureService(CaptionState state) => _state = state;

    public event Action<string>? DraftUpdated;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _captureThread = new Thread(() => RunLoop(token))
        {
            IsBackground = true,
            Name = "LiveCaptionsCapture",
        };
        _captureThread.SetApartmentState(ApartmentState.STA);
        _captureThread.Start();
        Trace.WriteLine("[InterviewAssistant] LiveCaptions capture started (STA thread)");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_captureThread is { IsAlive: true })
        {
            try
            {
                _captureThread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        }

        _captureThread = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, LiveCaptionsRestarter.WindowTitle));
                if (window is null)
                {
                    Thread.Sleep(500);
                    continue;
                }

                var text = GetAllTextControls(window);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Thread.Sleep(200);
                    continue;
                }

                var refined = CaptionState.NormalizeCaptionText(text);
                if (string.IsNullOrWhiteSpace(refined))
                {
                    Thread.Sleep(200);
                    continue;
                }

                _state.ApplyNormalizedCaption(refined);
                var draft = _state.GetDraftTail();
                RaiseDraftUpdated(draft);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] caption capture: {ex.Message}");
                Thread.Sleep(300);
            }

            Thread.Sleep(200);
        }
    }

    private void RaiseDraftUpdated(string draft)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            DraftUpdated?.Invoke(draft);
            return;
        }

        if (dispatcher.CheckAccess())
            DraftUpdated?.Invoke(draft);
        else
            dispatcher.BeginInvoke(() => DraftUpdated?.Invoke(draft), DispatcherPriority.Background);
    }

    private static string GetAllTextControls(AutomationElement control)
    {
        AutomationElementCollection children;
        try
        {
            children = control.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] UIA FindAll: {ex.Message}");
            return "";
        }

        foreach (AutomationElement child in children)
        {
            try
            {
                if (child.Current.ControlType == ControlType.Text && (child.Current.Name?.Length ?? 0) > 10)
                    return child.Current.Name ?? "";
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] UIA Current: {ex.Message}");
                continue;
            }

            var nested = GetAllTextControls(child);
            if (!string.IsNullOrEmpty(nested))
                return nested;
        }

        return "";
    }

    public void Dispose() => Stop();
}
