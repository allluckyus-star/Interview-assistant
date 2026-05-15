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

    public string GetDraftTail()
    {
        lock (_lock)
        {
            var start = Math.Min(_nextChunkStartIndex, _refinedFullCaption.Length);
            return _refinedFullCaption[start..];
        }
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
            return skipped;
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
        }
    }

    public void ApplyNormalizedCaption(string refinedText)
    {
        if (string.IsNullOrWhiteSpace(refinedText))
            return;
        lock (_lock)
        {
            var oldFull = _refinedFullCaption;
            var oldNext = _nextChunkStartIndex;
            if (_previousRefinedText.Length - refinedText.Length > 100)
            {
                var head = _previousRefinedText[..Math.Min(500, _previousRefinedText.Length)];
                var idx = _previousRefinedText.IndexOf(head[..Math.Min(head.Length, refinedText.Length)], StringComparison.Ordinal);
                if (idx >= 0)
                    _fixedCaption += _previousRefinedText[..idx];
            }

            _refinedFullCaption = _fixedCaption + refinedText;
            _previousRefinedText = refinedText;
            if (!string.IsNullOrEmpty(_chunkAnchorPrefix))
                _nextChunkStartIndex = Math.Clamp(oldNext, 0, _refinedFullCaption.Length);
            else
                _nextChunkStartIndex = Math.Clamp(oldNext, 0, _refinedFullCaption.Length);
        }
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
