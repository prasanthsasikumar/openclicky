using System.Runtime.InteropServices.WindowsRuntime;
using OpenClicky.Core.Platform;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Windows.Media.Ocr on a capture upscaled 2× (UI captions are ~11 px tall at 1280 px), boxes mapped
/// back to the original image's pixels. The engine is the user's profile language (or the tag asked
/// for when its OCR pack is installed). No language correction exists to turn off, unlike Vision.
/// </summary>
public sealed class ScreenTextRecognizer : IScreenTextRecognizer
{
    public bool IsAvailable(string? languageTag = null) => CreateEngine(languageTag) is not null;

    public Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(byte[] image, double upscale = 2, string? languageTag = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecognizeCoreAsync(image, upscale, languageTag, cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<RecognizedTextLine>> RecognizeCoreAsync(byte[] image, double upscale, string? languageTag, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languageTag) ?? throw new ScreenTextRecognizerException("no OCR language is installed on this pc");

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ScreenTextRecognizerException("the image could not be decoded", exception);
        }
        double originalWidth = decoder.PixelWidth, originalHeight = decoder.PixelHeight;

        // Never past the engine's limit (OcrEngine.MaxImageDimension, 10 000 px today).
        var maximumScale = OcrEngine.MaxImageDimension / Math.Max(originalWidth, originalHeight);
        var scale = Math.Max(1, Math.Min(upscale, maximumScale));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Round(originalWidth * scale),
            ScaledHeight = (uint)Math.Round(originalHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var lines = new List<RecognizedTextLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(word => new RecognizedTextWord(word.Text, new DoubleRect(
                    word.BoundingRect.X / scale, word.BoundingRect.Y / scale, word.BoundingRect.Width / scale, word.BoundingRect.Height / scale)))
                .ToList();
            if (words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
            var box = words.Skip(1).Aggregate(words[0].Box, (union, word) => DoubleRect.Union(union, word.Box));
            lines.Add(new RecognizedTextLine(line.Text, box, words));
        }
        return lines;
    }

    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrEmpty(languageTag))
        {
            try
            {
                var language = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(language)) return OcrEngine.TryCreateFromLanguage(language);
            }
            catch (ArgumentException)
            {
            }
        }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }
}
