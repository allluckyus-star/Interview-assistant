using System.Diagnostics;
using InterviewAssistant.App;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

/// <summary>Caption session + hotkeys for the Chrome extension (no WebView2).</summary>
public sealed class CompanionSessionService : IDisposable
{
    private readonly CaptionState _captionState = new();
    private readonly InterviewHistory _history = new();
    private readonly List<HistoryEventDto> _apiHistory = [];
    private readonly ModePromptStore _modePrompts = new();
    private readonly LanguagePromptStore _languagePrompts = new();
    private readonly LiveCaptionsCaptureService _capture;
    private readonly InterviewHotkeyService _hotkeys = new();

    public CompanionSessionService()
    {
        _capture = new LiveCaptionsCaptureService(_captionState);
        _capture.DraftUpdated += draft => DraftChanged?.Invoke(draft);
        _hotkeys.EndPressed += () => EndPressed?.Invoke();
        _hotkeys.DeletePressed += () => DeletePressed?.Invoke();
    }

    public ModePromptStore ModePrompts => _modePrompts;
    public LanguagePromptStore LanguagePrompts => _languagePrompts;
    public InterviewHistory History => _history;
    public bool IsRunning { get; private set; }

    public int SessionGeneration { get; private set; }

    public event Action<string>? DraftChanged;
    public event Action<string>? StatusMessage;
    public event Action<HistoryEventDto>? HistoryAdded;
    public event Action? EndPressed;
    public event Action? DeletePressed;

    public void Start()
    {
        if (IsRunning)
            return;

        SessionGeneration++;
        _captionState.ResetForNewSession();
        _history.Clear();
        _apiHistory.Clear();
        try
        {
            LiveCaptionsRestarter.Restart();
            _capture.Start();
            _hotkeys.Start();
            IsRunning = true;
            StatusMessage?.Invoke("Live captions listening.");
            PublishCaptionSnapshot();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Companion] start failed: {ex}");
            Stop();
            throw;
        }
    }

    /// <summary>Restart Live Captions capture without clearing interview history.</summary>
    public void RestartCaptions()
    {
        Stop();
        SessionGeneration++;
        _captionState.ResetForNewSession();
        try
        {
            LiveCaptionsRestarter.Restart();
            _capture.Start();
            _hotkeys.Start();
            IsRunning = true;
            StatusMessage?.Invoke("Live captions restarted.");
            PublishCaptionSnapshot();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Companion] restart captions failed: {ex}");
            Stop();
            throw;
        }
    }

    private void PublishCaptionSnapshot() =>
        DraftChanged?.Invoke(_captionState.GetDraftTail());

    public void Stop()
    {
        IsRunning = false;
        _hotkeys.Stop();
        _capture.Stop();
    }

    public string GetDraft() => _captionState.GetDraftTail();

    public string GetFullCaption() => _captionState.GetFullSessionCaption();

    public int GetPendingStartIndex() => _captionState.GetPendingStartIndex();

    public object GetDraftPayload() => new
    {
        draft = GetDraft(),
        full = GetFullCaption(),
        pending_start = GetPendingStartIndex(),
        running = IsRunning,
        mode = ModePrompts.SessionMode,
        language = LanguagePrompts.SessionLanguage,
        session_generation = SessionGeneration,
    };

    public IReadOnlyList<HistoryEventDto> GetHistorySnapshot() => _apiHistory.ToList();

    public EndResult TryEnd(string? overrideChunk = null)
    {
        var chunk = _captionState.SnapshotChunkSinceLastEnd();
        if (string.IsNullOrWhiteSpace(chunk))
        {
            var draft = _captionState.GetDraftTail();
            DraftChanged?.Invoke(draft);
            return new EndResult(false, "", "", draft, "No caption yet.");
        }

        if (!string.IsNullOrWhiteSpace(overrideChunk))
            chunk = overrideChunk.Trim();

        _history.AppendInterviewer(chunk, "sent_gpt");
        _apiHistory.Add(new HistoryEventDto("interviewer", chunk, "sent_gpt"));
        var intent = ResolveIntent(chunk);
        var (_, finalPrompt) = ChunkPromptBuilder.Build(
            intent,
            _modePrompts.GetActiveTemplate(),
            _languagePrompts.GetActiveTemplate());
        HistoryAdded?.Invoke(_apiHistory[^1]);
        DraftChanged?.Invoke(_captionState.GetDraftTail());
        return new EndResult(true, chunk, finalPrompt ?? "", _captionState.GetDraftTail(), null);
    }

    public SkipResult TryDelete()
    {
        var skipped = _captionState.SkipPendingWithoutGpt();
        if (!string.IsNullOrWhiteSpace(skipped))
        {
            _history.AppendInterviewer(skipped, "delete_skip");
            var ev = new HistoryEventDto("interviewer", skipped, "delete_skip");
            _apiHistory.Add(ev);
            HistoryAdded?.Invoke(ev);
        }

        DraftChanged?.Invoke(_captionState.GetDraftTail());
        var msg = string.IsNullOrWhiteSpace(skipped) ? "Nothing to skip." : "Skipped pending caption.";
        return new SkipResult(skipped ?? "", msg);
    }

    public IReadOnlyList<EndpointWordOption> GetEndpointWords(int count) =>
        _captionState.GetWordsBeforeEndpoint(count);

    public bool SetEndpoint(int startIndex)
    {
        if (!_captionState.SetEndpointAtCharacterIndex(startIndex))
            return false;
        DraftChanged?.Invoke(_captionState.GetDraftTail());
        return true;
    }

    public string ResolveIntent(string chunk) =>
        string.Equals(_modePrompts.SessionMode, "closing", StringComparison.OrdinalIgnoreCase)
            ? _captionState.GetFullSessionCaption().Trim()
            : (chunk ?? "").Trim();

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _hotkeys.Dispose();
    }
}

public sealed record EndResult(bool Ok, string Chunk, string Prompt, string Draft, string? Message);
public sealed record SkipResult(string Skipped, string Message);
public sealed record HistoryEventDto(string Role, string Text, string Source);
