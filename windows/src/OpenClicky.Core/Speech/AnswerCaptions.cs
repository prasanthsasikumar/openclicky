// Captions of the spoken answer: what Clicky says, written beside the pointer while it is said. A
// child in a noisy classroom, on a laptop with the sound off, or one who cannot hear well reads along
// instead of missing the answer. Each piece the voice speaks (SpeechQueue's pieces, a sentence or a
// few) shows whole as it starts; the last one stays a moment to finish reading, then fades. Fast voice
// shows its transcript as it comes in. A notice in this PC's voice is captioned the same way.
//
// When: "When it can't be heard" (the default) uses the Teacher's rule (TeacherListening): a typed
// question, muted sound or a low volume. "Always" and "Off" are the other two. Saved in shell.json's
// `answerCaptions` (absent for the default).

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.Teacher;

namespace OpenClicky.Core.Speech;

public enum AnswerCaptionsChoice
{
    /// <summary>Only when the question was typed, or the sound is muted or low.</summary>
    WhenItCannotBeHeard,
    Always,
    Off,
}

public static class AnswerCaptions
{
    public static readonly IReadOnlyList<AnswerCaptionsChoice> AllCases =
        [AnswerCaptionsChoice.WhenItCannotBeHeard, AnswerCaptionsChoice.Always, AnswerCaptionsChoice.Off];

    /// <summary>How long the last caption stays after the voice has said it.</summary>
    public const double SecondsAfterTheVoice = 2.5;

    /// <summary>The longest fast-voice caption; a longer transcript shows its end, from a sentence start.</summary>
    public const int LongestLiveCaption = 300;

    /// <summary>The stored name: "auto", "always", "off".</summary>
    public static string RawValue(this AnswerCaptionsChoice choice) => choice switch
    {
        AnswerCaptionsChoice.Always => "always",
        AnswerCaptionsChoice.Off => "off",
        _ => "auto",
    };

    public static AnswerCaptionsChoice? FromRawValue(string? rawValue) =>
        AllCases.Cast<AnswerCaptionsChoice?>().FirstOrDefault(choice => string.Equals(choice!.Value.RawValue(), rawValue?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Settings' picker.</summary>
    public static string Title(this AnswerCaptionsChoice choice) => choice switch
    {
        AnswerCaptionsChoice.Always => "Always",
        AnswerCaptionsChoice.Off => "Off",
        _ => "When it can't be heard",
    };

    /// <summary>The saved choice; anything unknown is the default.</summary>
    public static AnswerCaptionsChoice Current => FromRawValue(OpenClickyConfiguration.Settings.AnswerCaptions) ?? AnswerCaptionsChoice.WhenItCannotBeHeard;

    /// <summary>The value shell.json keeps: none for the default.</summary>
    public static string? Stored(AnswerCaptionsChoice choice) => choice == AnswerCaptionsChoice.WhenItCannotBeHeard ? null : choice.RawValue();

    public static void Set(AnswerCaptionsChoice choice) => OpenClickyConfiguration.Update(shell => shell.AnswerCaptions = Stored(choice));

    /// <summary>Whether this answer is captioned; decided once, as it starts.</summary>
    public static bool AreShown(AnswerCaptionsChoice choice, bool questionWasTyped, TeacherListening.Output output) => choice switch
    {
        AnswerCaptionsChoice.Always => true,
        AnswerCaptionsChoice.Off => false,
        _ => TeacherListening.AnswersShouldBeWritten(questionWasTyped, output),
    };

    /// <summary>
    /// A fast-voice transcript as a caption: whole when it is short, else its end from the first sentence that starts in
    /// the last <see cref="LongestLiveCaption"/> characters (after "…"), so the words being said now are always shown.
    /// </summary>
    public static string LiveText(string transcriptSoFar)
    {
        var text = transcriptSoFar.Trim();
        if (text.Length <= LongestLiveCaption) return text;
        var tailStart = text.Length - LongestLiveCaption;
        var sentenceStart = -1;
        for (var index = tailStart; index < text.Length - 1; index++)
        {
            if (text[index] is '.' or '!' or '?' or '\n' or '।' or '۔' or '。' && char.IsWhiteSpace(text[index + 1]))
            {
                sentenceStart = index + 1;
                break;
            }
        }
        var tail = sentenceStart > 0 ? text[sentenceStart..] : text[tailStart..];
        tail = tail.Trim();
        // No sentence started in the window: cut at a space, never in the middle of a word.
        if (sentenceStart < 0 && tail.IndexOf(' ') is var space and >= 0 && space < tail.Length - 1) tail = tail[(space + 1)..];
        return "… " + tail;
    }
}
