using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Threading;

namespace InterviewAssistant.App.Services;

/// <summary>Polls Windows Live Captions via UI Automation (same approach as live.py).</summary>
public sealed class LiveCaptionsCaptureService : IDisposable
{
    private const int ActivePollMs = 15;
    private const int IdlePollMs = 40;
    private const int WindowMissingPollMs = 120;

    private readonly CaptionState _state;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private volatile bool _discardStaleUntilEmpty;
    private DateTime _startedUtc;
    private int _unchangedPolls;

    public LiveCaptionsCaptureService(CaptionState state) => _state = state;

    public event Action<string>? DraftUpdated;

    public void Start()
    {
        Stop();
        _discardStaleUntilEmpty = true;
        _startedUtc = DateTime.UtcNow;
        _unchangedPolls = 0;
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
                if (!_captureThread.Join(TimeSpan.FromSeconds(2)))
                    Trace.WriteLine("[InterviewAssistant] LiveCaptions capture thread did not exit within 2s (process may still exit)");
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
            var pollMs = IdlePollMs;
            try
            {
                var window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, LiveCaptionsRestarter.WindowTitle));
                if (window is null)
                {
                    Thread.Sleep(WindowMissingPollMs);
                    continue;
                }

                var text = GetAllTextControls(window);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (_discardStaleUntilEmpty)
                    {
                        _discardStaleUntilEmpty = false;
                        _state.ResetForNewSession();
                        RaiseDraftUpdated(_state.GetDraftTail());
                    }
                    Thread.Sleep(ActivePollMs);
                    continue;
                }

                var refined = CaptionState.NormalizeCaptionText(text);
                if (string.IsNullOrWhiteSpace(refined))
                {
                    Thread.Sleep(ActivePollMs);
                    continue;
                }

                if (_discardStaleUntilEmpty)
                {
                    if ((DateTime.UtcNow - _startedUtc).TotalSeconds < 2)
                        continue;
                    _discardStaleUntilEmpty = false;
                }

                if (_state.ApplyNormalizedCaption(refined))
                {
                    _unchangedPolls = 0;
                    pollMs = ActivePollMs;
                    RaiseDraftUpdated(_state.GetDraftTail());
                }
                else
                {
                    _unchangedPolls++;
                    pollMs = _unchangedPolls >= 3 ? IdlePollMs : ActivePollMs;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] caption capture: {ex.Message}");
                pollMs = 60;
            }

            Thread.Sleep(pollMs);
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
            dispatcher.BeginInvoke(() => DraftUpdated?.Invoke(draft), DispatcherPriority.Send);
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
                if (child.Current.ControlType == ControlType.Text && (child.Current.Name?.Length ?? 0) > 0)
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
