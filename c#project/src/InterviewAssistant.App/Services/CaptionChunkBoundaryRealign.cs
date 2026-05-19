namespace InterviewAssistant.App.Services;

/// <summary>Maps <c>nextChunkStartIndex</c> after Live Captions rewrites text (mirrors live.py <c>_realign_chunk_boundary</c>).</summary>
internal static class CaptionChunkBoundaryRealign
{
    private const int ChunkAnchorTailChars = 200;
    private const int MinStrongOverlapChars = 24;
    private const int BoundaryVisibleTailFallbackChars = 96;

    public static int Realign(string newText, string anchorFull, int fallbackIndex)
    {
        var n = newText.Length;
        var fb = Math.Clamp(fallbackIndex, 0, n);
        var af = anchorFull ?? "";
        if (af.Length == 0)
            return fb;

        if (n >= af.Length && newText.StartsWith(af, StringComparison.Ordinal))
            return Math.Min(af.Length, n);

        var tailLen = Math.Min(ChunkAnchorTailChars, af.Length);
        var tail = tailLen > 0 ? af[^tailLen..] : "";
        if (tail.Length < 8)
        {
            var overlap = SuffixPrefixOverlapLen(af, newText, Math.Max(MinStrongOverlapChars, tail.Length));
            if (overlap >= MinStrongOverlapChars)
                return Math.Min(overlap, n);
            return fb;
        }

        var target = Math.Min(fb, n);
        var maxReasonableDist = Math.Max(320, Math.Max(af.Length / 3, n / 4));
        var bestPos = -1;
        var bestDist = int.MaxValue;
        var searchFrom = 0;
        while (true)
        {
            var pos = newText.IndexOf(tail, searchFrom, StringComparison.Ordinal);
            if (pos < 0)
                break;

            var mappedEnd = pos + tail.Length;
            var dist = Math.Abs(mappedEnd - target);
            if (dist < bestDist || (dist == bestDist && pos > bestPos))
            {
                bestPos = pos;
                bestDist = dist;
            }

            searchFrom = pos + 1;
        }

        if (bestPos >= 0 && bestDist <= maxReasonableDist)
            return Math.Min(bestPos + tail.Length, n);

        var rp = newText.LastIndexOf(tail, StringComparison.Ordinal);
        if (rp >= 0)
        {
            var mappedR = rp + tail.Length;
            if (Math.Abs(mappedR - target) <= maxReasonableDist)
                return Math.Min(mappedR, n);
        }

        var tailOverlap = SuffixPrefixOverlapLen(af, newText, tailLen);
        if (tailOverlap >= MinStrongOverlapChars)
            return Math.Min(tailOverlap, n);

        if (n > BoundaryVisibleTailFallbackChars && fb >= n)
            return Math.Max(0, n - BoundaryVisibleTailFallbackChars);

        return fb;
    }

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
}
