using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace InterviewAssistant.App.Services;

public sealed record SnipOcrResult(string Text, bool EngineAvailable, string? Hint)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Windows built-in OCR (same <see cref="OcrEngine"/> API ShareX uses).
/// Requires a Windows language pack with OCR / text recognition installed.
/// </summary>
public static class SnipImageOcr
{
    private const int MinLongestSide = 900;

    public static async Task<SnipOcrResult> ExtractAsync(
        byte[] pngBytes,
        CancellationToken cancellationToken = default)
    {
        if (pngBytes.Length == 0)
            return new SnipOcrResult("", true, null);

        cancellationToken.ThrowIfCancellationRequested();

        using var bitmap = await DecodeAndPrepareBitmapAsync(pngBytes, cancellationToken).ConfigureAwait(false);
        if (bitmap is null)
            return new SnipOcrResult("", true, "Could not read snip image.");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Language("en"));
        if (engine is null)
        {
            return new SnipOcrResult(
                "",
                false,
                "Windows OCR is not available. Install a language with text recognition: "
                + "Settings → Time & language → Language & region → Add language → Options → OCR / Text.");
        }

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var text = (result?.Text ?? "").Trim();
        if (text.Length > 0)
            return new SnipOcrResult(text, true, null);

        return new SnipOcrResult(
            "",
            true,
            "Windows OCR found no text. Snip a larger area with clear text.");
    }

    private static async Task<SoftwareBitmap?> DecodeAndPrepareBitmapAsync(
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await WinBitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        var decoded = await decoder
            .GetSoftwareBitmapAsync(WinBitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var longest = Math.Max(decoded.PixelWidth, decoded.PixelHeight);
        if (longest >= MinLongestSide)
            return decoded;

        var scale = MinLongestSide / (double)longest;
        var targetW = Math.Max(1, (int)Math.Round(decoded.PixelWidth * scale));
        var targetH = Math.Max(1, (int)Math.Round(decoded.PixelHeight * scale));
        var scaled = ScaleBitmap(decoded, targetW, targetH);
        decoded.Dispose();
        return scaled;
    }

    private static SoftwareBitmap ScaleBitmap(SoftwareBitmap source, int targetW, int targetH)
    {
        if (Application.Current?.Dispatcher is null)
            return source;

        SoftwareBitmap? scaled = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var converted = SoftwareBitmap.Convert(
                source,
                WinBitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            var stride = converted.PixelWidth * 4;
            var buffer = new byte[stride * converted.PixelHeight];
            converted.CopyToBuffer(buffer.AsBuffer());

            var src = BitmapSource.Create(
                converted.PixelWidth,
                converted.PixelHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                buffer,
                stride);
            src.Freeze();

            var tb = new TransformedBitmap(
                src,
                new ScaleTransform(
                    targetW / (double)converted.PixelWidth,
                    targetH / (double)converted.PixelHeight));
            tb.Freeze();

            var outStride = targetW * 4;
            var outPixels = new byte[outStride * targetH];
            tb.CopyPixels(outPixels, outStride, 0);

            scaled = SoftwareBitmap.CreateCopyFromBuffer(
                outPixels.AsBuffer(),
                WinBitmapPixelFormat.Bgra8,
                targetW,
                targetH,
                BitmapAlphaMode.Premultiplied);
        });

        return scaled ?? source;
    }
}
