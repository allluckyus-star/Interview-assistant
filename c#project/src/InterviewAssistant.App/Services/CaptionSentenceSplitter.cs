using System.Text;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Mirrors <c>IaCaptionSentences</c> in <c>caption-sentences.js</c>.
/// Splits caption text into sentence tokens and exposes character-range metadata
/// so the backend can compute sentence-level diffs without the extension.
/// </summary>
public static class CaptionSentenceSplitter
{
    private static readonly HashSet<char> SentenceEnds = ['.', '!', '?', '…', '。', '！', '？'];

    /// <summary>Split <paramref name="text"/> into sentence strings (trims each).</summary>
    public static string[] Split(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return [];

        var parts = new List<string>();
        var buf = new StringBuilder();

        for (var i = 0; i < t.Length; i++)
        {
            buf.Append(t[i]);
            if (!SentenceEnds.Contains(t[i])) continue;

            // Consume trailing whitespace as part of this sentence token (matches JS)
            while (i + 1 < t.Length && char.IsWhiteSpace(t[i + 1]))
            {
                i++;
                buf.Append(t[i]);
            }

            var sent = buf.ToString().Trim();
            if (sent.Length > 0) parts.Add(sent);
            buf.Clear();
        }

        var tail = buf.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);

        return parts.Count > 0 ? [.. parts] : [t];
    }

    /// <summary>Character range of a sentence within the original source string.</summary>
    public sealed record SentenceRange(string Text, int Start, int End);

    /// <summary>
    /// Split with character offsets into <paramref name="text"/> — mirrors
    /// <c>IaCaptionSentences.splitWithRanges</c>.
    /// </summary>
    public static SentenceRange[] SplitWithRanges(string? text)
    {
        var src = text ?? "";
        var t = src.Trim();
        if (t.Length == 0) return [];

        var sentences = Split(t);
        var result = new List<SentenceRange>(sentences.Length);
        var pos = 0;

        foreach (var sent in sentences)
        {
            var start = src.IndexOf(sent, pos, StringComparison.Ordinal);
            if (start < 0) start = pos;
            var end = start + sent.Length;
            result.Add(new SentenceRange(sent, start, end));
            pos = end;
        }

        return [.. result];
    }
}
