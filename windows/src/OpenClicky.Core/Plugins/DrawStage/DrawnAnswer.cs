//
//  DrawnAnswer.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  A drawing that finished on the stage and is still up: what it answered, its whole program, where
//  it was drawn, and its spoken lines with how long each took and its audio. A follow-up question
//  is answered about it (the model reads its program and may add to it), and saving replays it
//  from this, line by line on the same clock, to record it as a video with the same voice.
//

using System.Text.RegularExpressions;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>One spoken line of a drawing.</summary>
/// <param name="Seconds">How long it was spoken for.</param>
/// <param name="PauseBefore">The quiet before it (after the line before it, or from the start for the first).</param>
/// <param name="Audio">What the voice said, as the voice gave it (WAV or MP3); null when it had none.</param>
public sealed record DrawnLine(string Text, double Seconds, double PauseBefore, byte[]? Audio);

/// <param name="Questions">What it answered: the first question, then each follow-up that added to it.</param>
/// <param name="Program">The whole program on the stage (follow-ups that kept the drawing are appended to it).</param>
/// <param name="Frame">The screen it is drawn over, in physical pixels, on a monitor of <paramref name="Scale"/>.</param>
public sealed partial record DrawnAnswer(
    IReadOnlyList<string> Questions,
    string Program,
    PixelRect Frame,
    double Scale,
    int CanvasWidth,
    int CanvasHeight,
    IReadOnlyList<DrawnLine> Lines)
{
    /// <summary>The longest pause kept between two answers' lines when a drawing that was added to is replayed.</summary>
    public const double LongestPauseReplayed = 1.2;

    /// <summary>This drawing with a follow-up's beats added to it.</summary>
    public DrawnAnswer AddedTo(string question, string program, IReadOnlyList<DrawnLine> lines) => this with
    {
        Questions = [.. Questions, question],
        Program = Program.TrimEnd() + "\n\n" + program.Trim() + "\n",
        Lines = [.. Lines, .. lines.Select((line, index) => index == 0 ? line with { PauseBefore = Math.Min(line.PauseBefore, LongestPauseReplayed) } : line)],
    };

    /// <summary>A name for it: its first card's title, else the first question.</summary>
    public string Title
    {
        get
        {
            var title = CardTitle().Match(Program);
            return title.Success ? title.Groups[2].Value : Questions.FirstOrDefault()?.Trim().TrimEnd('?', '.', '!') ?? "Drawing";
        }
    }

    [GeneratedRegex(@"title:\s*(['""`])(.+?)\1")]
    private static partial Regex CardTitle();
}
