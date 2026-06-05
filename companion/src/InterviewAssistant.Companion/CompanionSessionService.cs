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
        ResetDeltaTracking();
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
        ResetDeltaTracking();
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

    // ── Delta / patch tracking ─────────────────────────────────────────────
    private const int DeltaLookbackSentences = 10;
    private readonly object _deltaLock = new();
    private string _lastDeltaFull = "";
    private string[] _lastDeltaSentences = [];
    private int _lastDeltaSessionGen = -1;
    /// <summary>Last caption state sent over SSE (dedup push notifications).</summary>
    private string _lastBroadcastFull = "";
    private string _lastBroadcastDraft = "";
    private int _lastBroadcastPendingStart = -1;
    /// <summary>Last caption state returned on GET /draft (separate from SSE so poll can resync).</summary>
    private string _lastPollFull = "";
    private string _lastPollDraft = "";
    private int _lastPollPendingStart = -1;

    private void ResetDeltaTracking()
    {
        lock (_deltaLock)
        {
            _lastDeltaFull = "";
            _lastDeltaSentences = [];
            _lastDeltaSessionGen = -1;
            _lastBroadcastFull = "";
            _lastBroadcastDraft = "";
            _lastBroadcastPendingStart = -1;
            _lastPollFull = "";
            _lastPollDraft = "";
            _lastPollPendingStart = -1;
        }
    }

    /// <summary>SSE push — skip when already broadcast since last caption change.</summary>
    public bool TryBuildDraftPayload(bool forceFullCaption, out object? payload) =>
        TryBuildDraftPayloadInternal(forceFullCaption, forPoll: false, out payload);

    /// <summary>HTTP GET /draft — separate dedup so poll can resync if SSE was missed.</summary>
    public bool TryBuildDraftPayloadForPoll(bool forceFullCaption, out object? payload) =>
        TryBuildDraftPayloadInternal(forceFullCaption, forPoll: true, out payload);

    private bool TryBuildDraftPayloadInternal(bool forceFullCaption, bool forPoll, out object? payload)
    {
        var fullCaption = GetFullCaption();
        var draft = GetDraft();
        var pendingStart = GetPendingStartIndex();
        var gen = SessionGeneration;

        lock (_deltaLock)
        {
            var sessionChanged = gen != _lastDeltaSessionGen;
            var lastFull = forPoll ? _lastPollFull : _lastBroadcastFull;
            var lastDraft = forPoll ? _lastPollDraft : _lastBroadcastDraft;
            var lastPending = forPoll ? _lastPollPendingStart : _lastBroadcastPendingStart;

            if (!forceFullCaption &&
                !sessionChanged &&
                fullCaption == lastFull &&
                draft == lastDraft &&
                pendingStart == lastPending)
            {
                payload = null;
                return false;
            }

            // Poll resync: if SSE already advanced delta state, send full so client catches up.
            var forceFull = forceFullCaption || sessionChanged ||
                            (forPoll && fullCaption != lastFull && fullCaption == _lastDeltaFull);

            payload = BuildDraftPayload_Locked(
                fullCaption,
                draft,
                pendingStart,
                gen,
                forceFull || _lastDeltaFull.Length == 0);

            if (forPoll)
                CommitPollState(fullCaption, draft, pendingStart);
            else
                CommitBroadcastState(fullCaption, draft, pendingStart, gen);

            return true;
        }
    }

    /// <summary>Legacy helper — always builds (used where caller needs unconditional snapshot).</summary>
    public object GetDraftPayload(bool forceFullCaption = false)
    {
        if (TryBuildDraftPayload(forceFullCaption, out var payload) && payload is not null)
            return payload;

        return new
        {
            changed = false,
            draft = GetDraft(),
            full = (string?)null,
            patch_from = (int?)null,
            patch_tail = (string?)null,
            pending_start = GetPendingStartIndex(),
            running = IsRunning,
            mode = ModePrompts.SessionMode,
            language = LanguagePrompts.SessionLanguage,
            session_generation = SessionGeneration,
        };
    }

    private object BuildDraftPayload_Locked(
        string fullCaption,
        string draft,
        int pendingStart,
        int gen,
        bool forceFullCaption)
    {
        string? payloadFull;
        int? patchFrom;
        string? patchTail;

        _lastDeltaSessionGen = gen;

        if (forceFullCaption || _lastDeltaFull.Length == 0)
        {
            _lastDeltaFull = fullCaption;
            _lastDeltaSentences = CaptionSentenceSplitter.Split(fullCaption);
            payloadFull = fullCaption;
            patchFrom = null;
            patchTail = null;
        }
        else
        {
            var (hasDelta, from, tail, useFull) = ComputeDelta_Locked(fullCaption);
            if (useFull)
            {
                _lastDeltaFull = fullCaption;
                _lastDeltaSentences = CaptionSentenceSplitter.Split(fullCaption);
                payloadFull = fullCaption;
                patchFrom = null;
                patchTail = null;
            }
            else if (hasDelta)
            {
                payloadFull = null;
                patchFrom = from;
                patchTail = tail;
            }
            else
            {
                payloadFull = null;
                patchFrom = null;
                patchTail = null;
            }
        }

        return new
        {
            changed = true,
            draft,
            full = payloadFull,
            patch_from = patchFrom,
            patch_tail = patchTail,
            pending_start = pendingStart,
            running = IsRunning,
            mode = ModePrompts.SessionMode,
            language = LanguagePrompts.SessionLanguage,
            session_generation = gen,
        };
    }

    private void CommitBroadcastState(string full, string draft, int pendingStart, int gen)
    {
        _lastBroadcastFull = full;
        _lastBroadcastDraft = draft;
        _lastBroadcastPendingStart = pendingStart;
        _lastDeltaSessionGen = gen;
    }

    private void CommitPollState(string full, string draft, int pendingStart)
    {
        _lastPollFull = full;
        _lastPollDraft = draft;
        _lastPollPendingStart = pendingStart;
    }

    /// <summary>
    /// Compare <paramref name="newFull"/> against <see cref="_lastDeltaFull"/> using the last
    /// <see cref="DeltaLookbackSentences"/> sentences as the comparison window.
    /// Returns the character offset in <paramref name="newFull"/> where the first divergence
    /// starts, plus the new tail from that point.
    /// Must be called while <see cref="_deltaLock"/> is held.
    /// </summary>
    private (bool HasDelta, int PatchFrom, string PatchTail, bool UseFull) ComputeDelta_Locked(string newFull)
    {
        if (_lastDeltaFull == newFull)
            return (false, 0, "", false);

        var prevFull = _lastDeltaFull;
        var prevFullLength = prevFull.Length;
        var newRanges = CaptionSentenceSplitter.SplitWithRanges(newFull);
        var newSentences = newRanges.Select(static r => r.Text).ToArray();
        var prevSentences = _lastDeltaSentences;

        var prevCount = prevSentences.Length;
        var newCount = newSentences.Length;

        // First content after empty baseline — client has nothing to splice from.
        if (prevCount == 0 || prevFullLength == 0)
        {
            CommitDeltaState(newFull, newSentences);
            return (true, 0, newFull, false);
        }

        var startIdx = Math.Max(0, prevCount - DeltaLookbackSentences);
        var compareLen = Math.Min(prevCount - startIdx, Math.Max(0, newCount - startIdx));

        var divergeAt = -1;
        for (var i = 0; i < compareLen; i++)
        {
            if (prevSentences[startIdx + i] != newSentences[startIdx + i])
            {
                divergeAt = startIdx + i;
                break;
            }
        }

        // Pure-append: all existing sentences still match, new sentences were added.
        var isPureAppend = divergeAt < 0 && newCount > prevCount;
        if (isPureAppend)
            divergeAt = prevCount;

        // LC rolled/shrank or rewrite fell outside the lookback window — send full to resync.
        if (divergeAt < 0)
        {
            CommitDeltaState(newFull, newSentences);
            return (false, 0, "", true);
        }

        int patchFrom;
        if (isPureAppend)
        {
            // Client holds prevFull exactly; splice at its length (not the new sentence offset).
            patchFrom = prevFullLength;
        }
        else
        {
            patchFrom = divergeAt < newRanges.Length ? newRanges[divergeAt].Start : newFull.Length;
            // Safety: if computed offset is past what the client should have, fall back to full.
            if (patchFrom > prevFullLength)
            {
                CommitDeltaState(newFull, newSentences);
                return (false, 0, "", true);
            }
        }

        CommitDeltaState(newFull, newSentences);
        var tail = patchFrom < newFull.Length ? newFull[patchFrom..] : "";
        return (true, patchFrom, tail, false);
    }

    private void CommitDeltaState(string full, string[] sentences)
    {
        _lastDeltaFull = full;
        _lastDeltaSentences = sentences;
    }

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
