using System.Text;
using System.Text.RegularExpressions;

namespace InterviewAssistant.App.Services;

/// <summary>Rolling Live Captions buffer — mirrors live.py refined_full_caption / next_chunk_start_index.</summary>
public sealed class CaptionState
{
    private readonly object _lock = new();
    private string _refinedFullCaption = "";
    private string _fixedCaption = "";
    private string _previousRefinedText = "";
    private int _nextChunkStartIndex;
    private string _chunkAnchorPrefix = "";
    private bool _alignPendingToLiveEndAfterStale;
    /// <summary>Live window at last skip / resurfaced boundary — realigns index when LC rewrites in place.</summary>
    private string _pendingBoundaryLiveSnapshot = "";
    /// <summary>Live text at last Delete/End (persists through resurfaced) for same-utterance detection.</summary>
    private string _lastSkippedLiveSnapshot = "";
    /// <summary>Handled live chars at skip (0 = skip cap inactive). Boundary in full = fixed + this.</summary>
    private int _handledSkippedLiveLen;
    private bool _manualEndpointPinned;
    private string _manualEndpointAnchorPrefix = "";

    public string GetDraftTail()
    {
        lock (_lock)
        {
            var start = Math.Min(_nextChunkStartIndex, _refinedFullCaption.Length);
            return _refinedFullCaption[start..];
        }
    }

    /// <summary>Full transcript since session start (fixed archive + current live window).</summary>
    public string GetFullSessionCaption()
    {
        lock (_lock)
            return _refinedFullCaption;
    }

    /// <summary>Character index in <see cref="GetFullSessionCaption"/> where pending (green) draft begins.</summary>
    public int GetPendingStartIndex()
    {
        lock (_lock)
            return Math.Min(_nextChunkStartIndex, _refinedFullCaption.Length);
    }

    public string SnapshotChunkSinceLastEnd()
    {
        lock (_lock)
        {
            var full = _refinedFullCaption;
            var start = Math.Min(_nextChunkStartIndex, full.Length);
            var chunk = full[start..].Trim();
            _nextChunkStartIndex = full.Length;
            _chunkAnchorPrefix = full;
            _alignPendingToLiveEndAfterStale = true;
            ClearManualEndpointPin();
            ActivateSkipCapFromFull(full);
            LogMetrics("End");
            return chunk;
        }
    }

    public string SkipPendingWithoutGpt()
    {
        lock (_lock)
        {
            var full = _refinedFullCaption;
            var start = Math.Min(_nextChunkStartIndex, full.Length);
            var skipped = full[start..].Trim();
            _nextChunkStartIndex = full.Length;
            _chunkAnchorPrefix = full;
            _alignPendingToLiveEndAfterStale = true;
            ClearManualEndpointPin();
            ActivateSkipCapFromFull(full);
            LogMetrics("Delete");
            return skipped;
        }
    }

    /// <summary>
    /// Move draft start backward one word boundary in the full transcript (fixed + live).
    /// </summary>
    public bool NudgeChunkStartToPreviousWordBoundary()
    {
        lock (_lock)
        {
            var full = _refinedFullCaption;
            if (string.IsNullOrEmpty(full))
                return false;

            var idx = Math.Clamp(_nextChunkStartIndex, 0, full.Length);
            if (idx <= 0)
            {
                if (full.Length == 0)
                    return false;
                // Whole transcript is draft — nudge from end of full caption backward.
                idx = full.Length;
            }

            var newIdx = FindPreviousWordBoundary(full, idx);
            if (newIdx < 0 || newIdx >= idx)
                return false;

            _nextChunkStartIndex = newIdx;
            PinManualEndpointAtCurrentIndex();
            LogMetrics("NudgeEndpoint");
            return true;
        }
    }

    public bool SetEndpointAtCharacterIndex(int startIndexInFull)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_refinedFullCaption))
                return false;

            var idx = Math.Clamp(startIndexInFull, 0, _refinedFullCaption.Length);
            if (idx == _nextChunkStartIndex)
                return false;

            _nextChunkStartIndex = idx;
            PinManualEndpointAtCurrentIndex();
            LogMetrics("SetEndpoint");
            return true;
        }
    }

    /// <summary>Words in full transcript immediately before the current draft start index.</summary>
    public IReadOnlyList<EndpointWordOption> GetWordsBeforeEndpoint(int maxWords)
    {
        lock (_lock)
        {
            if (maxWords <= 0 || string.IsNullOrEmpty(_refinedFullCaption))
                return Array.Empty<EndpointWordOption>();

            var full = _refinedFullCaption;
            var idx = Math.Clamp(_nextChunkStartIndex, 0, full.Length);
            if (idx <= 0)
                return Array.Empty<EndpointWordOption>();

            var prefix = full[..idx];
            var words = new List<EndpointWordOption>();
            var i = 0;
            while (i < prefix.Length)
            {
                while (i < prefix.Length && char.IsWhiteSpace(prefix[i]))
                    i++;
                if (i >= prefix.Length)
                    break;
                var start = i;
                while (i < prefix.Length && !char.IsWhiteSpace(prefix[i]))
                    i++;
                words.Add(new EndpointWordOption(prefix[start..i], start));
            }

            if (words.Count <= maxWords)
                return words;

            return words.Skip(words.Count - maxWords).ToList();
        }
    }

    private static int FindPreviousWordBoundary(string full, int idx)
    {
        var pos = idx - 1;
        while (pos >= 0 && char.IsWhiteSpace(full[pos]))
            pos--;

        if (pos < 0)
            return 0;

        while (pos >= 0 && !char.IsWhiteSpace(full[pos]))
            pos--;

        var newIdx = pos + 1;
        while (newIdx < full.Length && char.IsWhiteSpace(full[newIdx]))
            newIdx++;

        return newIdx;
    }

    private void PinManualEndpointAtCurrentIndex()
    {
        var full = _refinedFullCaption;
        var idx = Math.Clamp(_nextChunkStartIndex, 0, full.Length);
        _manualEndpointPinned = true;
        _manualEndpointAnchorPrefix = full[..idx];
    }

    private void ClearManualEndpointPin()
    {
        _manualEndpointPinned = false;
        _manualEndpointAnchorPrefix = "";
    }

    private void ApplyPinnedManualEndpoint()
    {
        if (!_manualEndpointPinned || string.IsNullOrEmpty(_manualEndpointAnchorPrefix))
            return;

        var full = _refinedFullCaption;
        if (full.StartsWith(_manualEndpointAnchorPrefix, StringComparison.Ordinal))
        {
            _nextChunkStartIndex = _manualEndpointAnchorPrefix.Length;
            return;
        }

        const int tailChars = 96;
        var tail = _manualEndpointAnchorPrefix.Length > tailChars
            ? _manualEndpointAnchorPrefix[^tailChars..]
            : _manualEndpointAnchorPrefix;
        if (tail.Length >= 8)
        {
            var pos = full.IndexOf(tail, StringComparison.Ordinal);
            if (pos >= 0)
            {
                var newIdx = Math.Clamp(pos + tail.Length, 0, full.Length);
                _nextChunkStartIndex = newIdx;
                _manualEndpointAnchorPrefix = full[..newIdx];
            }
        }
    }

    public void ResetForNewSession()
    {
        lock (_lock)
        {
            _refinedFullCaption = "";
            _fixedCaption = "";
            _previousRefinedText = "";
            _nextChunkStartIndex = 0;
            _chunkAnchorPrefix = "";
            _alignPendingToLiveEndAfterStale = false;
            _pendingBoundaryLiveSnapshot = "";
            ClearManualEndpointPin();
            ClearActiveSkipCap("reset");
            LogMetrics("Reset");
        }
    }

    public void ApplyNormalizedCaption(string refinedText)
    {
        if (string.IsNullOrWhiteSpace(refinedText))
            return;
        lock (_lock)
        {
            var oldNext = _nextChunkStartIndex;
            var oldFullLen = _refinedFullCaption.Length;
            var prevLive = _previousRefinedText;
            var prevFixedLen = _fixedCaption.Length;

            var fixedDelta = ArchiveDroppedLiveWindowOnShrink(refinedText);

            _refinedFullCaption = _fixedCaption + refinedText;

            if (fixedDelta > 0 && HasActiveSkipCap())
                ClearActiveSkipCap("archive-shrink");

            var remappedAfterArchive = false;
            var indexFloor = _fixedCaption.Length;

            if (fixedDelta > 0 && oldNext > prevFixedLen)
            {
                RemapIndexAfterLivePrefixArchived(oldNext, prevFixedLen, prevLive, refinedText, fixedDelta);
                remappedAfterArchive = true;
                indexFloor = _nextChunkStartIndex;
                if (!string.IsNullOrEmpty(_chunkAnchorPrefix))
                    ClearChunkAnchor();
            }
            else if (fixedDelta > 0)
            {
                // live.py: next_chunk_start_index = min(old_next, len(new_full)) when cursor never entered live
                SetChunkIndex(
                    Math.Clamp(oldNext, 0, _refinedFullCaption.Length),
                    refinedText,
                    oldNext,
                    "archive-keep-cursor");
                remappedAfterArchive = true;
                indexFloor = _nextChunkStartIndex;
            }

            if (!string.IsNullOrEmpty(_chunkAnchorPrefix))
                ApplyIndexAfterCaptionRewrite(oldNext, oldFullLen, refinedText, indexFloor);

            if (_alignPendingToLiveEndAfterStale)
            {
                SetChunkIndex(_refinedFullCaption.Length, refinedText, oldNext, "align-after-stale");
                _alignPendingToLiveEndAfterStale = false;
            }
            else if (string.IsNullOrEmpty(_chunkAnchorPrefix) && !remappedAfterArchive)
                RealignIndexWithoutChunkAnchor(oldNext, oldFullLen, prevLive, prevFixedLen, refinedText);

            _previousRefinedText = refinedText;
            SetChunkIndex(_nextChunkStartIndex, refinedText, oldNext, "finalize");
            NormalizeChunkIndex();
            ApplyPinnedManualEndpoint();

            if (_refinedFullCaption.Length != oldFullLen || oldNext != _nextChunkStartIndex)
                LogMetrics("update", refinedText.Length);
        }
    }

    private void SetChunkIndex(int proposed, string liveText, int oldNext, string path)
    {
        _nextChunkStartIndex = CapProposedIndex(proposed, liveText, oldNext, path);
    }

    private bool HasActiveSkipCap() =>
        _handledSkippedLiveLen > 0 && !string.IsNullOrEmpty(_lastSkippedLiveSnapshot);

    private void ClearActiveSkipCap(string reason)
    {
        if (_handledSkippedLiveLen > 0 || !string.IsNullOrEmpty(_lastSkippedLiveSnapshot))
        {
            CaptionDiagnostics.LogSkipCap(
                reason, _handledSkippedLiveLen, 0, 0, 0,
                _nextChunkStartIndex, _nextChunkStartIndex, "cap-cleared");
        }

        _handledSkippedLiveLen = 0;
        _lastSkippedLiveSnapshot = "";
        _pendingBoundaryLiveSnapshot = "";
    }

    private void ActivateSkipCapFromFull(string full)
    {
        var liveAtSkip = _fixedCaption.Length < full.Length
            ? full[_fixedCaption.Length..]
            : _previousRefinedText;
        _pendingBoundaryLiveSnapshot = liveAtSkip;
        if (string.IsNullOrEmpty(liveAtSkip))
        {
            _handledSkippedLiveLen = 0;
            return;
        }

        _lastSkippedLiveSnapshot = liveAtSkip;
        _handledSkippedLiveLen = liveAtSkip.Length;
        CaptionDiagnostics.LogSkipCap(
            "activate-skip-cap",
            _handledSkippedLiveLen,
            liveAtSkip.Length,
            0,
            _fixedCaption.Length + _handledSkippedLiveLen,
            _nextChunkStartIndex,
            _nextChunkStartIndex,
            "end-or-delete");
    }

    /// <summary>fixed + min(handledSkippedLiveLen, liveLen).</summary>
    private int SkippedPrefixBoundaryInFull(string liveText)
    {
        var handledInLive = Math.Min(_handledSkippedLiveLen, Math.Max(0, liveText.Length));
        return _fixedCaption.Length + handledInLive;
    }

    private int CapProposedIndex(int proposed, string liveText, int oldNext, string path)
    {
        if (!HasActiveSkipCap())
            return proposed;

        if (!SkipSnapshotStillRelevant(liveText))
        {
            ClearActiveSkipCap("no-overlap");
            return proposed;
        }

        var skippedLen = _handledSkippedLiveLen;
        var liveLen = liveText.Length;
        var tailLen = Math.Max(0, liveLen - skippedLen);
        var boundary = SkippedPrefixBoundaryInFull(liveText);
        var fullLen = _refinedFullCaption.Length;
        string capReason;

        // Live is only the skipped utterance (or shorter) — nothing pending.
        if (liveLen <= skippedLen)
        {
            capReason = "live-within-snapshot";
            var capped = Math.Clamp(Math.Min(proposed, fullLen), _fixedCaption.Length, fullLen);
            CaptionDiagnostics.LogSkipCap(path, skippedLen, liveLen, tailLen, boundary, proposed, capped, capReason);
            return capped;
        }

        // Skipped snapshot + new tail — index must stay at boundary, never fullLen.
        var cappedIndex = Math.Min(proposed, boundary);
        capReason = proposed > boundary ? "capped-skip-plus-tail" : "within-boundary";

        if (GetSkippedPrefixLengthInLive(liveText) >= 0 || SkipPrefixVisibleFuzzy(liveText))
        {
            cappedIndex = boundary;
            capReason = "boundary-skip-plus-tail";
        }

        cappedIndex = Math.Clamp(cappedIndex, _fixedCaption.Length, fullLen);
        CaptionDiagnostics.LogSkipCap(path, skippedLen, liveLen, tailLen, boundary, proposed, cappedIndex, capReason);
        return cappedIndex;
    }

    private bool SkipSnapshotStillRelevant(string liveText)
    {
        if (string.IsNullOrEmpty(_lastSkippedLiveSnapshot))
            return false;

        if (GetSkippedPrefixLengthInLive(liveText) >= 0)
            return true;

        if (liveText.StartsWith(_lastSkippedLiveSnapshot, StringComparison.Ordinal)
            || _lastSkippedLiveSnapshot.StartsWith(liveText, StringComparison.Ordinal))
            return true;

        var headLen = Math.Min(80, Math.Min(_lastSkippedLiveSnapshot.Length, liveText.Length));
        if (headLen >= 32
            && liveText.AsSpan(0, headLen).SequenceEqual(_lastSkippedLiveSnapshot.AsSpan(0, headLen)))
            return true;

        return SuffixPrefixOverlapLen(_lastSkippedLiveSnapshot, liveText, 200) >= MinStrongOverlapChars
            || SuffixPrefixOverlapLen(liveText, _lastSkippedLiveSnapshot, 200) >= MinStrongOverlapChars;
    }

    private bool SkipPrefixVisibleFuzzy(string liveText)
    {
        if (string.IsNullOrEmpty(_lastSkippedLiveSnapshot) || liveText.Length == 0)
            return false;

        var headLen = Math.Min(80, Math.Min(_lastSkippedLiveSnapshot.Length, liveText.Length));
        if (headLen >= 32
            && liveText.AsSpan(0, headLen).SequenceEqual(_lastSkippedLiveSnapshot.AsSpan(0, headLen)))
            return true;

        return SuffixPrefixOverlapLen(_lastSkippedLiveSnapshot, liveText, MinStrongOverlapChars) >= MinStrongOverlapChars;
    }

    /// <summary>Clamp index to [floor, fullLen]. Draft may include archived fixed prefix (live.py).</summary>
    private void NormalizeChunkIndex(int? floor = null)
    {
        var fullLen = _refinedFullCaption.Length;
        var minIndex = Math.Clamp(floor ?? 0, 0, fullLen);
        _nextChunkStartIndex = Math.Clamp(_nextChunkStartIndex, minIndex, fullLen);
    }

    private void ApplyIndexAfterCaptionRewrite(int oldNext, int oldFullLen, string liveText, int indexFloor)
    {
        var full = _refinedFullCaption;
        var anchor = _chunkAnchorPrefix;

        if (full.Length < anchor.Length)
        {
            HandleStaleAnchorBreak(liveText, anchor, "shrink");
            return;
        }

        if (IsResurfacedSkippedContent(liveText, anchor))
        {
            HandleStaleAnchorBreak(liveText, anchor, "resurfaced");
            return;
        }

        var mapped = CaptionChunkBoundaryRealign.Realign(
            full,
            anchor,
            Math.Min(oldNext, oldFullLen),
            out var confident);

        var newIndex = Math.Clamp(mapped, 0, full.Length);
        newIndex = Math.Max(newIndex, indexFloor);

        if (!confident && newIndex < oldNext)
            newIndex = Math.Clamp(oldNext, 0, full.Length);

        if (oldNext >= oldFullLen)
            newIndex = full.Length;

        if (!confident
            && !full.StartsWith(anchor, StringComparison.Ordinal)
            && SuffixPrefixOverlapLen(anchor, full, MinStrongOverlapChars) < MinStrongOverlapChars)
        {
            HandleStaleAnchorBreak(liveText, anchor, "no-overlap");
            return;
        }

        SetChunkIndex(Math.Max(newIndex, indexFloor), liveText, oldNext, "anchor-realign");
    }

    private void HandleStaleAnchorBreak(string liveText, string anchor, string reason)
    {
        var resurfaced = reason == "resurfaced" || IsResurfacedSkippedContent(liveText, anchor);

        if (!resurfaced)
            EnsurePreviousLiveArchivedForSegmentBreak(liveText);

        var full = _refinedFullCaption;
        ClearChunkAnchor();

        if (resurfaced)
        {
            _pendingBoundaryLiveSnapshot = liveText;
            if (!string.IsNullOrEmpty(liveText)
                && (string.IsNullOrEmpty(_lastSkippedLiveSnapshot)
                    || liveText.Length >= _lastSkippedLiveSnapshot.Length))
            {
                _lastSkippedLiveSnapshot = liveText;
            }

            _handledSkippedLiveLen = liveText.Length;
            SetChunkIndex(full.Length, liveText, _nextChunkStartIndex, "stale-resurfaced");
            _alignPendingToLiveEndAfterStale = true;
        }
        else
        {
            ClearActiveSkipCap("stale-no-overlap");
            SetChunkIndex(full.Length, liveText, _nextChunkStartIndex, "stale-no-overlap");
            _alignPendingToLiveEndAfterStale = true;
        }

        CaptionDiagnostics.Log(
            $"stale-{reason}",
            _nextChunkStartIndex,
            full.Length,
            "",
            _fixedCaption.Length,
            liveText.Length,
            GetDraftSlice());
    }

    private string GetDraftSlice()
    {
        var start = Math.Min(_nextChunkStartIndex, _refinedFullCaption.Length);
        return _refinedFullCaption[start..];
    }

    private static bool IsResurfacedSkippedContent(string liveText, string anchor)
    {
        if (string.IsNullOrEmpty(anchor) || string.IsNullOrEmpty(liveText))
            return false;

        var headLen = Math.Min(80, anchor.Length);
        if (liveText.StartsWith(anchor[..headLen], StringComparison.Ordinal))
            return true;

        return SuffixPrefixOverlapLen(anchor, liveText, MinStrongOverlapChars) >= MinStrongOverlapChars;
    }

    private void ClearChunkAnchor() => _chunkAnchorPrefix = "";

    private void RealignIndexWithoutChunkAnchor(
        int oldNext,
        int oldFullLen,
        string prevLive,
        int prevFixedLen,
        string liveText)
    {
        var full = _refinedFullCaption;
        var fixedLen = _fixedCaption.Length;

        if (oldFullLen > 0 && oldNext >= oldFullLen && full.Length <= oldFullLen)
        {
            SetChunkIndex(full.Length, liveText, oldNext, "pin-no-growth");
            return;
        }

        if (string.IsNullOrEmpty(_pendingBoundaryLiveSnapshot) && !HasActiveSkipCap())
        {
            // live.py: min(old_next, len(new_full)) — do not floor at fixedLen when cursor never entered live
            SetChunkIndex(Math.Clamp(oldNext, 0, full.Length), liveText, oldNext, "no-pending");
            return;
        }

        var skipPrefixLen = GetSkippedPrefixLengthInLive(liveText);
        if (skipPrefixLen >= 0)
        {
            SetChunkIndex(
                Math.Clamp(fixedLen + skipPrefixLen, fixedLen, full.Length),
                liveText,
                oldNext,
                "skip-prefix");
            _pendingBoundaryLiveSnapshot = liveText.Length > skipPrefixLen
                ? liveText[..skipPrefixLen]
                : liveText;
            if (HasActiveSkipCap())
                _handledSkippedLiveLen = Math.Max(_handledSkippedLiveLen, skipPrefixLen);
            return;
        }

        if (HasActiveSkipCap() && liveText.Length > _handledSkippedLiveLen)
        {
            SetChunkIndex(SkippedPrefixBoundaryInFull(liveText), liveText, oldNext, "skip-cap-boundary");
            return;
        }

        if (IsSameSkippedUtteranceOnly(liveText))
        {
            SetChunkIndex(full.Length, liveText, oldNext, "same-skip-only");
            _pendingBoundaryLiveSnapshot = liveText;
            return;
        }

        if (!string.IsNullOrEmpty(prevLive)
            && liveText.Length >= prevLive.Length
            && liveText.StartsWith(prevLive, StringComparison.Ordinal))
        {
            var pendingStartInPrevLive = Math.Max(0, Math.Min(oldNext - prevFixedLen, prevLive.Length));
            var appendIndex = Math.Clamp(fixedLen + pendingStartInPrevLive, fixedLen, full.Length);
            SetChunkIndex(Math.Max(oldNext, appendIndex), liveText, oldNext, "append");
            SyncPendingSnapshotToHandledLive(liveText);
            return;
        }

        var handledLiveLen = MapHandledLengthInLive(prevLive, prevFixedLen, oldNext, liveText);
        var candidate = Math.Clamp(fixedLen + handledLiveLen, fixedLen, full.Length);
        SetChunkIndex(
            candidate >= oldNext || handledLiveLen > 0
                ? candidate
                : Math.Clamp(oldNext, 0, full.Length),
            liveText,
            oldNext,
            "fuzzy-remap");
        SyncPendingSnapshotToHandledLive(liveText);
    }

    private void RemapIndexAfterLivePrefixArchived(
        int oldNext,
        int prevFixedLen,
        string prevLive,
        string liveText,
        int fixedDelta)
    {
        var prevFullLen = prevFixedLen + prevLive.Length;
        var pendingStartInPrevLive = Math.Max(0, Math.Min(oldNext - prevFixedLen, prevLive.Length));

        if (oldNext <= prevFixedLen)
        {
            SetChunkIndex(
                Math.Clamp(oldNext, 0, _refinedFullCaption.Length),
                liveText,
                oldNext,
                "remap-archive-cursor-not-in-live");
            return;
        }

        if (oldNext < prevFullLen)
        {
            var handledInNewLive = Math.Max(0, pendingStartInPrevLive - fixedDelta);
            SetChunkIndex(
                _fixedCaption.Length + Math.Clamp(handledInNewLive, 0, liveText.Length),
                liveText,
                oldNext,
                "remap-active-draft");
            SyncPendingSnapshotToHandledLive(liveText);
            return;
        }

        var baselineHandled = Math.Max(0, pendingStartInPrevLive - fixedDelta);
        var handledInLive = MapHandledLengthInLive(prevLive, prevFixedLen, oldNext, liveText);
        handledInLive = Math.Max(handledInLive, baselineHandled);

        if (!string.IsNullOrEmpty(_lastSkippedLiveSnapshot))
        {
            var skipTarget = Math.Min(_lastSkippedLiveSnapshot.Length, liveText.Length);
            var skipEnd = MapHandledSuffixInLive(_lastSkippedLiveSnapshot, liveText, skipTarget);
            if (skipEnd >= MinStrongOverlapChars)
                handledInLive = Math.Max(
                    handledInLive,
                    CapHandledLiveForSkip(liveText, skipEnd, _lastSkippedLiveSnapshot.Length));
        }

        var skipPrefixLen = GetSkippedPrefixLengthInLive(liveText);
        if (skipPrefixLen >= 0)
            handledInLive = Math.Max(handledInLive, skipPrefixLen);

        SetChunkIndex(
            _fixedCaption.Length + Math.Clamp(handledInLive, 0, liveText.Length),
            liveText,
            oldNext,
            "remap-archive");
        SyncPendingSnapshotToHandledLive(liveText);
    }

    private int MapHandledLengthInLive(string prevLive, int prevFixedLen, int oldNext, string liveText)
    {
        var pendingStartInPrevLive = Math.Max(0, Math.Min(oldNext - prevFixedLen, prevLive.Length));
        var prevFullLen = prevFixedLen + prevLive.Length;

        if (oldNext >= prevFullLen)
        {
            if (HasActiveSkipCap() && liveText.Length > _handledSkippedLiveLen)
                return _handledSkippedLiveLen;
            return liveText.Length;
        }

        if (prevLive.Length > 0 && pendingStartInPrevLive > 0)
        {
            var mapped = MapHandledSuffixInLive(prevLive[..pendingStartInPrevLive], liveText, pendingStartInPrevLive);
            if (mapped >= 0)
                return CapHandledLiveForSkipWhenActive(liveText, mapped);
        }

        var skipPrefixLen = GetSkippedPrefixLengthInLive(liveText);
        if (skipPrefixLen >= 0)
            return skipPrefixLen;

        if (_pendingBoundaryLiveSnapshot.Length > 0)
        {
            if (liveText.StartsWith(_pendingBoundaryLiveSnapshot, StringComparison.Ordinal))
                return _pendingBoundaryLiveSnapshot.Length;

            var mapped = MapHandledSuffixInLive(_pendingBoundaryLiveSnapshot, liveText, _pendingBoundaryLiveSnapshot.Length);
            if (mapped >= 0)
                return CapHandledLiveForSkipWhenActive(liveText, mapped);
        }

        if (!string.IsNullOrEmpty(_lastSkippedLiveSnapshot))
        {
            var skipTarget = Math.Min(_lastSkippedLiveSnapshot.Length, liveText.Length);
            var skipEnd = MapHandledSuffixInLive(_lastSkippedLiveSnapshot, liveText, skipTarget);
            if (skipEnd >= MinStrongOverlapChars)
                return CapHandledLiveForSkip(liveText, skipEnd, _lastSkippedLiveSnapshot.Length);
        }

        return 0;
    }

    private int CapHandledLiveForSkipWhenActive(string liveText, int handledLen)
    {
        if (!HasActiveSkipCap())
            return handledLen;
        return CapHandledLiveForSkip(liveText, handledLen, _handledSkippedLiveLen);
    }

    private static int CapHandledLiveForSkip(string liveText, int handledLen, int skipRefLen)
    {
        if (skipRefLen > 0 && liveText.Length > skipRefLen)
            return Math.Min(handledLen, skipRefLen);
        return handledLen;
    }

    private int GetSkippedPrefixLengthInLive(string liveText)
    {
        if (string.IsNullOrEmpty(liveText))
            return -1;

        var best = -1;
        foreach (var reference in new[] { _pendingBoundaryLiveSnapshot, _lastSkippedLiveSnapshot })
        {
            if (string.IsNullOrEmpty(reference))
                continue;
            if (liveText.StartsWith(reference, StringComparison.Ordinal))
                best = Math.Max(best, reference.Length);
            else if (reference.StartsWith(liveText, StringComparison.Ordinal))
                best = Math.Max(best, liveText.Length);
        }

        return best;
    }

    /// <summary>LC shows only the skipped utterance (not skip + new tail).</summary>
    private bool IsSameSkippedUtteranceOnly(string liveText)
    {
        if (string.IsNullOrEmpty(liveText) || !HasActiveSkipCap())
            return false;

        if (liveText.Length > _handledSkippedLiveLen + 24)
            return false;

        return IsSameSkippedUtteranceFuzzy(liveText);
    }

    private bool IsSameSkippedUtteranceFuzzy(string liveText)
    {
        foreach (var reference in new[] { _pendingBoundaryLiveSnapshot, _lastSkippedLiveSnapshot })
        {
            if (string.IsNullOrEmpty(reference))
                continue;

            if (liveText.StartsWith(reference, StringComparison.Ordinal))
                return liveText.Length <= reference.Length;

            if (reference.StartsWith(liveText, StringComparison.Ordinal))
                return true;

            var headLen = Math.Min(80, Math.Min(reference.Length, liveText.Length));
            if (headLen >= 32
                && liveText.AsSpan(0, headLen).SequenceEqual(reference.AsSpan(0, headLen)))
                return true;

            if (SuffixPrefixOverlapLen(reference, liveText, 200) >= MinStrongOverlapChars
                || SuffixPrefixOverlapLen(liveText, reference, 200) >= MinStrongOverlapChars)
                return true;
        }

        return false;
    }

    private static int MapHandledSuffixInLive(string handledText, string liveText, int targetEndInLive)
    {
        if (handledText.Length == 0 || liveText.Length == 0)
            return -1;

        var tailLen = Math.Min(ChunkAnchorTailChars, handledText.Length);
        var tail = handledText[^tailLen..];
        if (tail.Length < 8)
            return -1;

        var maxReasonableDist = Math.Max(320, Math.Max(handledText.Length / 3, liveText.Length / 4));
        var bestEnd = -1;
        var bestDist = int.MaxValue;
        var searchFrom = 0;

        while (true)
        {
            var pos = liveText.IndexOf(tail, searchFrom, StringComparison.Ordinal);
            if (pos < 0)
                break;

            var mappedEnd = pos + tail.Length;
            var dist = Math.Abs(mappedEnd - targetEndInLive);
            if (dist < bestDist || (dist == bestDist && mappedEnd > bestEnd))
            {
                bestDist = dist;
                bestEnd = mappedEnd;
            }

            searchFrom = pos + 1;
        }

        if (bestEnd >= 0 && bestDist <= maxReasonableDist)
        {
            var mapped = Math.Min(bestEnd, liveText.Length);
            var minHandled = Math.Max(0, targetEndInLive - maxReasonableDist);
            return Math.Max(mapped, minHandled);
        }

        var overlap = SuffixPrefixOverlapLen(handledText, liveText, tailLen);
        if (overlap >= MinStrongOverlapChars)
            return Math.Min(overlap, liveText.Length);

        return -1;
    }

    private void SyncPendingSnapshotToHandledLive(string liveText)
    {
        var handledLen = _nextChunkStartIndex - _fixedCaption.Length;
        if (handledLen > 0 && handledLen <= liveText.Length)
            _pendingBoundaryLiveSnapshot = liveText[..handledLen];
        else
            _pendingBoundaryLiveSnapshot = "";
    }

    private void EnsurePreviousLiveArchivedForSegmentBreak(string liveText)
    {
        var previous = _previousRefinedText;
        if (previous.Length == 0 || IsLiveContinuation(previous, liveText))
            return;

        if (_fixedCaption.Length >= previous.Length
            && _fixedCaption.AsSpan(_fixedCaption.Length - previous.Length).SequenceEqual(previous.AsSpan()))
            return;

        _fixedCaption += previous;
        _refinedFullCaption = _fixedCaption + liveText;
    }

    private static bool IsLiveContinuation(string previous, string live)
    {
        if (live.StartsWith(previous, StringComparison.Ordinal)
            || previous.StartsWith(live, StringComparison.Ordinal))
            return true;

        return SuffixPrefixOverlapLen(previous, live, 48) >= 20;
    }

    private int ArchiveDroppedLiveWindowOnShrink(string refinedText)
    {
        var previous = _previousRefinedText;
        if (previous.Length - refinedText.Length <= LiveShrinkArchiveThreshold)
            return 0;

        var fixedBefore = _fixedCaption.Length;

        if (refinedText.Length == 0)
        {
            _fixedCaption += previous;
            BumpIndexWhenBoundaryWasInsideFixed(fixedBefore);
            return _fixedCaption.Length - fixedBefore;
        }

        if (previous.StartsWith(refinedText, StringComparison.Ordinal))
        {
            _fixedCaption += previous[refinedText.Length..];
            BumpIndexWhenBoundaryWasInsideFixed(fixedBefore);
            return _fixedCaption.Length - fixedBefore;
        }

        var headLen = Math.Min(LiveFindHeadChars, refinedText.Length);
        var properIndex = previous.IndexOf(refinedText[..headLen], StringComparison.Ordinal);
        if (properIndex >= 0)
        {
            if (properIndex == 0 && !previous.StartsWith(refinedText, StringComparison.Ordinal))
                _fixedCaption += previous;
            else
                _fixedCaption += previous[..properIndex];
            BumpIndexWhenBoundaryWasInsideFixed(fixedBefore);
            return _fixedCaption.Length - fixedBefore;
        }

        _fixedCaption += previous;
        BumpIndexWhenBoundaryWasInsideFixed(fixedBefore);
        return _fixedCaption.Length - fixedBefore;
    }

    private void BumpIndexWhenBoundaryWasInsideFixed(int fixedBefore)
    {
        // Do not move index=0 to fixedLen when LC only rolls off the start (matches live.py).
        if (_nextChunkStartIndex > fixedBefore && _nextChunkStartIndex < _fixedCaption.Length)
            _nextChunkStartIndex = _fixedCaption.Length;
    }

    private const int LiveShrinkArchiveThreshold = 80;
    private const int LiveFindHeadChars = 500;
    private const int ChunkAnchorTailChars = 200;
    private const int MinStrongOverlapChars = 24;

    private static int SuffixPrefixOverlapLen(string a, string b, int maxChars)
    {
        var maxLen = Math.Min(Math.Min(a.Length, b.Length), Math.Max(0, maxChars));
        for (var k = maxLen; k > 0; k--)
        {
            if (a.AsSpan(a.Length - k, k).SequenceEqual(b.AsSpan(0, k)))
                return k;
        }

        return 0;
    }

    private void LogMetrics(string reason, int liveWindowLen = 0)
    {
        var start = Math.Min(_nextChunkStartIndex, _refinedFullCaption.Length);
        var draftTail = _refinedFullCaption[start..];
        CaptionDiagnostics.Log(
            reason,
            _nextChunkStartIndex,
            _refinedFullCaption.Length,
            _chunkAnchorPrefix,
            _fixedCaption.Length,
            liveWindowLen,
            draftTail);
    }

    public static string NormalizeCaptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        var cleaned = string.Join(' ', lines);
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+([,.;:!?])", "$1");
        return cleaned.Trim();
    }
}
