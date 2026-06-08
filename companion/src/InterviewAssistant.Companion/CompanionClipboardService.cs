using InterviewAssistant.App.Services;

namespace InterviewAssistant.Companion;

public sealed class CompanionClipboardService
{
    private int _imageInProgress;
    private int _textInProgress;

    public async Task<ShareXImageResponse> WaitForShareXImageAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _imageInProgress, 1, 0) != 0)
            return ShareXImageResponse.FromBusy();

        try
        {
            var dispatcher = CompanionSnipDispatcher.Get();
            var png = await WindowsScreenSnipCapture
                .WaitForShareXImageAsync(dispatcher, cancellationToken)
                .ConfigureAwait(false);

            if (png is null || png.Length == 0)
                return ShareXImageResponse.FromCancelled();

            return ShareXImageResponse.FromImage(png);
        }
        catch (OperationCanceledException)
        {
            return ShareXImageResponse.FromCancelled();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] image wait error: {ex.Message}");
            return ShareXImageResponse.FromError(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _imageInProgress, 0);
        }
    }

    public async Task<ShareXTextResponse> WaitForShareXTextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _textInProgress, 1, 0) != 0)
            return ShareXTextResponse.FromBusy();

        try
        {
            var dispatcher = CompanionSnipDispatcher.Get();
            var text = await WindowsScreenSnipCapture
                .WaitForShareXTextAsync(dispatcher, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
                return ShareXTextResponse.FromCancelled();

            return ShareXTextResponse.FromText(text.Trim());
        }
        catch (OperationCanceledException)
        {
            return ShareXTextResponse.FromCancelled();
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"[IA ShareX] text wait error: {ex.Message}");
            return ShareXTextResponse.FromError(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _textInProgress, 0);
        }
    }
}

public sealed record ShareXImageResponse(bool Ok, bool Cancelled, string? Error, string? ImageId)
{
    public static ShareXImageResponse FromImage(byte[] png) =>
        new(true, false, null, CompanionImageCache.Store(png));

    public static ShareXImageResponse FromCancelled() =>
        new(false, true, null, null);

    public static ShareXImageResponse FromBusy() =>
        new(false, false, "clipboard_busy", null);

    public static ShareXImageResponse FromError(string message) =>
        new(false, false, message, null);
}

public sealed record ShareXTextResponse(bool Ok, bool Cancelled, string? Error, string? Text)
{
    public static ShareXTextResponse FromText(string text) =>
        new(true, false, null, text);

    public static ShareXTextResponse FromCancelled() =>
        new(false, true, null, null);

    public static ShareXTextResponse FromBusy() =>
        new(false, false, "clipboard_busy", null);

    public static ShareXTextResponse FromError(string message) =>
        new(false, false, message, null);
}
