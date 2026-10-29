// Port of macos/OpenClicky/OpenClicky/AskPointerContext.swift
//
// "What is this?" with the mouse on something. The ask lane used to send the screenshots and the
// highlighted text but never where the mouse was, so the model answered about the window with
// focus (a tester pointed at a desktop item and heard about the Outlook window behind it). Now the
// question carries the pointer's place in the screenshot, a close-up of the area around it rides
// along as one more image, and the same question asked again straight after is flagged as "the
// last answer was probably not what they meant".
//
// Windows: the mouse and the captures' display frames are both in global physical pixels with y
// going down, so no flip is needed (AppKit's y went up). The crop itself is the platform's
// (IScreenCapture.CropJpeg); where to cut is decided here.

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Answers;

/// <summary>
/// Where the mouse pointer is in the screenshot it is on, in that screenshot's pixels from the
/// top-left (the space the model's [POINT:x,y] tags use).
/// </summary>
/// <param name="ScreenLabel">The label of the capture the pointer is on, as the model sees it.</param>
public sealed record PointerInCapture(int X, int Y, string ScreenLabel)
{
    /// <summary>The pointer placed in the capture whose display holds the mouse; null when none does.</summary>
    public static PointerInCapture? Locate(PixelPoint mouse, IReadOnlyList<CompanionScreenCapture> captures)
    {
        var capture = captures.FirstOrDefault(capture => capture.DisplayFrame.Contains(mouse));
        if (capture is null) return null;
        var (x, y) = capture.ScreenshotPoint(mouse);
        return new PointerInCapture((int)x, (int)y, capture.Label);
    }

    /// <summary>Added to the question, in brackets like the highlight line.</summary>
    public string PromptLine =>
        $"The mouse pointer is at ({X}, {Y}) from the top-left of the screenshot labelled {ScreenLabel}. When the user asks what \"this\" or \"that\" is, or what they are pointing at, they mean what is under the pointer or right next to it, not the window with keyboard focus. Name the element you are describing.";
}

/// <summary>
/// The square around the pointer, cut from the screenshot it is on, so small things (a desktop
/// icon, a toolbar glyph) are readable at full size.
/// </summary>
public static class PointerCloseUp
{
    public const int DefaultSideInPixels = 480;

    /// <summary>
    /// The square to cut (in the screenshot's pixels): centred on the pointer, pushed back inside the
    /// image near an edge, and no bigger than the image. Null for an empty image.
    /// </summary>
    public static PixelRect? CropRect(PointerInCapture pointer, int imageWidth, int imageHeight, int sideInPixels = DefaultSideInPixels)
    {
        var side = Math.Min(sideInPixels, Math.Min(imageWidth, imageHeight));
        if (side <= 0) return null;
        var originX = Math.Min(Math.Max(pointer.X - side / 2, 0), imageWidth - side);
        var originY = Math.Min(Math.Max(pointer.Y - side / 2, 0), imageHeight - side);
        return new PixelRect(originX, originY, side, side);
    }

    /// <summary>The image's label: what it is, and that its pixels are not a screen to point in.</summary>
    public static string Label(int sideInPixels = DefaultSideInPixels) =>
        $"Close-up: the {sideInPixels}x{sideInPixels} pixel area around the mouse pointer, cut from the screenshot the pointer is on (the pointer is at its centre unless it is near an edge). For looking only: never use this image's coordinates in a [POINT:x,y] tag.";
}

/// <summary>
/// The same question asked again right after an answer: the first answer was probably about the
/// wrong thing (the focused window, not the pointer), so the model is told to look again.
/// </summary>
public static class RepeatedQuestion
{
    /// <summary>How soon after the previous question a near-identical one counts as asking again.</summary>
    public const double WithinSeconds = 180;
    /// <summary>
    /// How different the words may be (edit distance over the longer length) and still be the same
    /// question: "what is this" / "what's that" are one question, "open my mail" is not.
    /// </summary>
    public const double MaximumDifference = 0.25;

    public const string PromptLine = "The user just asked nearly the same question again. The previous answer was probably not about what they meant: this time answer about what is under the mouse pointer, and say which element you are describing.";

    public static bool IsRepeat(string transcript, string? previous, double secondsSincePrevious)
    {
        if (secondsSincePrevious > WithinSeconds || previous is null) return false;
        var current = Normalized(transcript);
        var before = Normalized(previous);
        if (current.Length == 0 || before.Length == 0) return false;
        var distance = EditDistance(current, before);
        return (double)distance / Math.Max(current.Length, before.Length) <= MaximumDifference;
    }

    /// <summary>Lowercase letters, digits and single spaces: punctuation and apostrophes do not make a new question.</summary>
    public static string Normalized(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var character in text.ToLowerInvariant())
        {
            if (character is '\'' or '’') continue;
            kept.Append(char.IsLetterOrDigit(character) || character == ' ' ? character : ' ');
        }
        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int EditDistance(string left, string right)
    {
        var previousRow = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 0; leftIndex < left.Length; leftIndex++)
        {
            var currentRow = new int[right.Length + 1];
            currentRow[0] = leftIndex + 1;
            for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
            {
                var substitution = previousRow[rightIndex] + (left[leftIndex] == right[rightIndex] ? 0 : 1);
                currentRow[rightIndex + 1] = Math.Min(Math.Min(previousRow[rightIndex + 1] + 1, currentRow[rightIndex] + 1), substitution);
            }
            previousRow = currentRow;
        }
        return previousRow[right.Length];
    }
}
