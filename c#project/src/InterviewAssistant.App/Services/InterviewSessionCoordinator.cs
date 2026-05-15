using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using InterviewAssistant.Bridge;

namespace InterviewAssistant.App.Services;

/// <summary>Main interview loop: captions, hotkeys, bridge prompts, answer polling.</summary>
public sealed class InterviewSessionCoordinator : IDisposable
{
    private readonly PromptStore _promptStore;
    private readonly ModePromptStore _modePrompts;
    private readonly CaptionState _captionState = new();
    private readonly InterviewHistory _history = new();
    private readonly LiveCaptionsCaptureService _capture;
    private readonly InterviewHotkeyService _hotkeys;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _bridgeBase;
    private readonly Dispatcher? _uiDispatcher;

    private CancellationTokenSource? _pollCts;
    private string _pendingRequestId = "";
    private string _lastAnswerRequestId = "";

    public InterviewSessionCoordinator(PromptStore promptStore, string bridgeHost, int bridgePort, InterviewHotkeyService hotkeys)
    {
        _promptStore = promptStore;
        _modePrompts = new ModePromptStore();
        _hotkeys = hotkeys;
        _bridgeBase = $"http://{bridgeHost}:{bridgePort}";
        _uiDispatcher = System.Windows.Application.Current?.Dispatcher;
        _capture = new LiveCaptionsCaptureService(_captionState);
        _capture.DraftUpdated += OnDraftUpdated;
        _hotkeys.EndPressed += () => PostToUi(OnEndPressed);
        _hotkeys.DeletePressed += () => PostToUi(OnDeletePressed);
    }

    private void PostToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    public ModePromptStore ModePrompts => _modePrompts;
    public InterviewHistory History => _history;

    public event Action<string>? DraftCaptionChanged;
    public event Action<string>? StatusMessage;
    public event Action<string>? GptAnswerReceived;
    public event Action<string, string>? InterviewerChunkCaptured;
    public event Action<string>? SendChunkToGptRequested;

    public bool IsRunning { get; private set; }

    public void Start(string resume, string jd)
    {
        Stop();
        _captionState.ResetForNewSession();
        _history.Clear();
        _pendingRequestId = "";
        _lastAnswerRequestId = "";
        _promptStore.SetResumeText(resume);
        _promptStore.SetJobDescriptionText(jd);

        try
        {
            Trace.WriteLine("[InterviewAssistant] Restarting LiveCaptions.exe");
            LiveCaptionsRestarter.Restart();

            _capture.Start();
            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollAnswersLoop(_pollCts.Token));
            IsRunning = true;
            StatusMessage?.Invoke("Live captions listening. End = send chunk, Delete = skip.");
            Trace.WriteLine("[InterviewAssistant] Interview session started");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InterviewAssistant] Interview session start failed: {ex}");
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _capture.Stop();
    }

    private void OnDraftUpdated(string draft) => DraftCaptionChanged?.Invoke(draft);

    private void OnEndPressed()
    {
        var chunk = _captionState.SnapshotChunkSinceLastEnd();
        Trace.WriteLine($"[InterviewAssistant] End key chunk len={chunk.Length}");
        if (string.IsNullOrWhiteSpace(chunk))
        {
            StatusMessage?.Invoke("No caption yet.");
            DraftCaptionChanged?.Invoke("");
            return;
        }

        _history.AppendInterviewer(chunk, "sent_gpt");
        InterviewerChunkCaptured?.Invoke(chunk, "sent_gpt");

        var (_, finalPrompt) = ChunkPromptBuilder.Build(chunk, _modePrompts.GetActiveTemplate());
        if (!string.IsNullOrWhiteSpace(finalPrompt))
            SendChunkToGptRequested?.Invoke(finalPrompt);

        DraftCaptionChanged?.Invoke("");
    }

    private void OnDeletePressed()
    {
        var skipped = _captionState.SkipPendingWithoutGpt();
        Trace.WriteLine($"[InterviewAssistant] Delete skip len={skipped.Length}");
        if (!string.IsNullOrWhiteSpace(skipped))
        {
            _history.AppendInterviewer(skipped, "delete_skip");
            InterviewerChunkCaptured?.Invoke(skipped, "delete_skip");
        }

        DraftCaptionChanged?.Invoke(_captionState.GetDraftTail());
        StatusMessage?.Invoke(string.IsNullOrWhiteSpace(skipped) ? "Nothing to skip." : "Skipped pending caption.");
    }

    public void FinishBubbleEdit(bool reject, string editedText, string? finalPrompt = null)
    {
        var body = (editedText ?? "").Trim();
        if (reject)
        {
            if (!string.IsNullOrWhiteSpace(body))
                _history.AppendInterviewer(body, "rejected");
            StatusMessage?.Invoke("Edit rejected.");
            return;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            StatusMessage?.Invoke("Edit empty.");
            return;
        }

        _history.AppendInterviewer(body, "bubble_edit");
        var prompt = finalPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
            (_, prompt) = ChunkPromptBuilder.Build(body, _modePrompts.GetActiveTemplate());

        if (!string.IsNullOrWhiteSpace(prompt))
            SendChunkToGptRequested?.Invoke(prompt);

        DraftCaptionChanged?.Invoke(_captionState.GetDraftTail());
    }

    private async Task PollAnswersLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var pending = _pendingRequestId;
                if (!string.IsNullOrEmpty(pending))
                {
                    using var resp = await _http.GetAsync($"{_bridgeBase}/latest-answer", token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    var json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var rid = root.TryGetProperty("request_id", out var r) ? r.GetString() ?? "" : "";
                    var answer = root.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(rid)
                        && !string.IsNullOrWhiteSpace(answer)
                        && rid == pending
                        && rid != _lastAnswerRequestId)
                    {
                        _lastAnswerRequestId = rid;
                        _pendingRequestId = "";
                        _history.AppendGpt(answer);
                        Trace.WriteLine($"[InterviewAssistant] GPT answer len={answer.Length} rid={rid}");
                        GptAnswerReceived?.Invoke(answer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[InterviewAssistant] answer poll: {ex.Message}");
            }

            try
            {
                await Task.Delay(string.IsNullOrEmpty(_pendingRequestId) ? 1000 : 250, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _http.Dispose();
    }
}
