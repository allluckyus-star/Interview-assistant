namespace InterviewAssistant.App.Services;

/// <summary>Maps <c>nextChunkStartIndex</c> after Live Captions rewrites text (mirrors live.py <c>_realign_chunk_boundary</c>).</summary>
internal static class CaptionChunkBoundaryRealign
{
    private const int ChunkAnchorTailChars = 200;
    private const int MinStrongOverlapChars = 24;
    private const int BoundaryVisibleTailFallbackChars = 96;

    public static int Realign(string newText, string anchorFull, int fallbackIndex) =>
        Realign(newText, anchorFull, fallbackIndex, out _);

    public static int Realign(string newText, string anchorFull, int fallbackIndex, out bool confident)
    {
        var n = newText.Length;
        var fb = Math.Clamp(fallbackIndex, 0, n);
        var af = anchorFull ?? "";
        if (af.Length == 0)
        {
            confident = false;
            return fb;
        }

        if (n >= af.Length && newText.StartsWith(af, StringComparison.Ordinal))
        {
            confident = true;
            return Math.Min(af.Length, n);
        }

        var tailLen = Math.Min(ChunkAnchorTailChars, af.Length);
        var tail = tailLen > 0 ? af[^tailLen..] : "";
        if (tail.Length < 8)
        {
            var overlap = SuffixPrefixOverlapLen(af, newText, Math.Max(MinStrongOverlapChars, tail.Length));
            if (overlap >= MinStrongOverlapChars)
            {
                confident = true;
                return Math.Min(overlap, n);
            }

            confident = false;
            return fb;
        }

        var target = Math.Min(fb, n);
        var maxReasonableDist = Math.Max(320, Math.Max(af.Length / 3, n / 4));
        var bestPos = -1;
        var bestMappedEnd = -1;
        var bestDist = int.MaxValue;
        var searchFrom = 0;
        while (true)
        {
            var pos = newText.IndexOf(tail, searchFrom, StringComparison.Ordinal);
            if (pos < 0)
                break;

            var mappedEnd = pos + tail.Length;
            var dist = Math.Abs(mappedEnd - target);
            if (dist < bestDist || (dist == bestDist && mappedEnd > bestMappedEnd))
            {
                bestPos = pos;
                bestMappedEnd = mappedEnd;
                bestDist = dist;
            }

            searchFrom = pos + 1;
        }

        if (bestPos >= 0 && bestDist <= maxReasonableDist)
        {
            confident = true;
            return Math.Min(bestMappedEnd, n);
        }

        var rp = newText.LastIndexOf(tail, StringComparison.Ordinal);
        if (rp >= 0)
        {
            var mappedR = rp + tail.Length;
            if (Math.Abs(mappedR - target) <= maxReasonableDist)
            {
                confident = true;
                return Math.Min(mappedR, n);
            }
        }

        var tailOverlap = SuffixPrefixOverlapLen(af, newText, tailLen);
        if (tailOverlap >= MinStrongOverlapChars)
        {
            confident = true;
            return Math.Min(tailOverlap, n);
        }

        if (n > BoundaryVisibleTailFallbackChars && fb >= n)
        {
            confident = false;
            return Math.Max(0, n - BoundaryVisibleTailFallbackChars);
        }

        confident = false;
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
