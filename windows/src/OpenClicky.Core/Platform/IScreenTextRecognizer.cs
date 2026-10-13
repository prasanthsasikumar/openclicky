namespace OpenClicky.Core.Platform;

/// <summary>One recognised word, its box in the original image's pixels (origin top-left).</summary>
public sealed record RecognizedTextWord(string Text, DoubleRect Box);

/// <summary>One recognised line (ScreenTextLine), its box the union of its words, in the original image's pixels.</summary>
public sealed record RecognizedTextLine(string Text, DoubleRect Box, IReadOnlyList<RecognizedTextWord> Words);

/// <summary>OCR of a capture (Vision on the Mac, Windows.Media.Ocr here). Runs off the caller's thread.</summary>
public interface IScreenTextRecognizer
{
    /// <summary>Whether an OCR engine exists for the user's profile languages (or <paramref name="languageTag"/>).</summary>
    bool IsAvailable(string? languageTag = null);

    /// <summary>
    /// Lines and words of a JPEG/PNG, read from the image upscaled by <paramref name="upscale"/>
    /// (2× by default: UI captions are ~11 px tall at 1280 px) with boxes mapped back to the original's pixels.
    /// </summary>
    Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(byte[] image, double upscale = 2, string? languageTag = null, CancellationToken cancellationToken = default);
}

public sealed class ScreenTextRecognizerException(string message, Exception? inner = null) : Exception(message, inner);
