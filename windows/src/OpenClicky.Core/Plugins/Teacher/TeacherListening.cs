//
//  TeacherListening.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherListening.swift)
//
//  Whether the person can hear Clicky right now. In a loud room with the volume at 31 %, a
//  student typed "please solve this equation on the screen" and the whole answer went to a voice
//  nobody heard. A typed question, muted sound or a low volume means the answer is written on the
//  screen as well: a lesson when it is one, otherwise the reply as a handwritten note
//  (TeacherNote). The decision is pure and unit-tested; reading the volume is the WPF side's
//  (ITeacherBoardSurface.CurrentOutput, the default output device's endpoint volume).
//

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Plugins.Teacher;

public static class TeacherListening
{
    /// <param name="Volume">0…1, null when the device has no main volume control (some USB and HDMI outputs).</param>
    public sealed record Output(float? Volume, bool IsMuted)
    {
        public static Output Unknown { get; } = new(null, false);
    }

    /// <summary>Below this, laptop speakers are lost in any noise.</summary>
    public const float QuietVolume = 0.4f;

    public static bool AnswersShouldBeWritten(bool questionWasTyped, Output output)
    {
        // Typing instead of speaking is the clearest sign they cannot talk, or listen, right now.
        if (questionWasTyped || output.IsMuted) return true;
        return output.Volume is { } volume && volume < QuietVolume;
    }
}

/// <summary>A reply written out as a handwritten note: wrapped lines, left-aligned, on paper.</summary>
public static class TeacherNote
{
    public const int CharactersPerLine = 34;
    public const int MaximumLines = 14;
    public const double LineSpacing = 28;

    public static TeacherSketch? Sketch(string answer)
    {
        var lines = WrappedLines(answer);
        if (lines.Count == 0) return null;
        var strokes = lines.Select((line, index) => new TeacherStroke
        {
            Kind = TeacherStrokeKind.Text, Words = line, IsLeftAligned = true,
            Points = [new ScreenPoint(12, 22 + index * LineSpacing)],
        }).ToList();
        return new TeacherSketch(400, 22 + lines.Count * LineSpacing, strokes);
    }

    /// <summary>Words wrapped at CharactersPerLine, one line per line break the reply already has; past MaximumLines
    /// the last line ends in "…".</summary>
    public static IReadOnlyList<string> WrappedLines(string answer)
    {
        var lines = new List<string>();
        foreach (var paragraph in answer.Split(['\r', '\n', '\u2028', '\u2029']))
        {
            var current = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (candidate.Length > CharactersPerLine && current.Length > 0)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            if (current.Length > 0) lines.Add(current);
        }
        if (lines.Count > MaximumLines)
        {
            lines = lines.Take(MaximumLines).ToList();
            lines[MaximumLines - 1] += " …";
        }
        return lines;
    }
}
