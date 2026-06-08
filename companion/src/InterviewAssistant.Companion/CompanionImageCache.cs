using System.Collections.Concurrent;

namespace InterviewAssistant.Companion;

internal static class CompanionImageCache
{
    private static readonly ConcurrentDictionary<string, (byte[] Data, DateTime ExpiresUtc)> Cache = new();

    public static string Store(byte[] png)
    {
        var id = Guid.NewGuid().ToString("N");
        Cache[id] = (png, DateTime.UtcNow.AddMinutes(5));
        return id;
    }

    public static byte[]? Peek(string id)
    {
        if (!Cache.TryGetValue(id, out var entry))
            return null;
        if (DateTime.UtcNow > entry.ExpiresUtc)
        {
            Cache.TryRemove(id, out _);
            return null;
        }

        return entry.Data;
    }

    public static byte[]? Take(string id)
    {
        if (!Cache.TryRemove(id, out var entry))
            return null;
        if (DateTime.UtcNow > entry.ExpiresUtc)
            return null;
        return entry.Data;
    }

    public static void Remove(string id) => Cache.TryRemove(id, out _);
}
