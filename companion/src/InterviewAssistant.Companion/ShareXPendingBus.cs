namespace InterviewAssistant.Companion;

internal static class ShareXPendingBus
{
    private static readonly object Gate = new();
    private static int _nextId;
    private static int _pendingId;
    private static string? _imageId;
    private static string? _text;

    public static int PublishImage(string imageId)
    {
        lock (Gate)
        {
            _pendingId = ++_nextId;
            _imageId = imageId;
            _text = null;
            return _pendingId;
        }
    }

    public static int PublishText(string text)
    {
        lock (Gate)
        {
            _pendingId = ++_nextId;
            _imageId = null;
            _text = text;
            return _pendingId;
        }
    }

    public static object? GetPending(int afterId)
    {
        lock (Gate)
        {
            if (_pendingId <= afterId)
                return null;

            return new
            {
                pending_id = _pendingId,
                image_id = _imageId,
                text = _text,
            };
        }
    }

    public static (bool Ok, string? ImageId) Ack(int pendingId)
    {
        lock (Gate)
        {
            if (_pendingId != pendingId)
                return (false, null);

            var imageId = _imageId;
            _imageId = null;
            _text = null;
            return (true, imageId);
        }
    }
}
