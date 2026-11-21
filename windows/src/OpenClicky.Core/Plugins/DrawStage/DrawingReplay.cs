//
//  DrawingReplay.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  "Play it again": a drawing that finished (DrawnAnswer) is drawn again where it is, with the voice it had, so a child
//  can watch an explanation again without asking again. Nothing goes to the model and no voice is fetched: the program is
//  fed to the stage whole, and each line's kept audio plays while the page draws that line's strokes (pace "speech", the
//  page waits for each line's start and end). A line with no audio waits as long as it was said for. The drawing is
//  remembered again once it has played, so it can be followed up, saved or played again.
//
//  Asked for by the page's Play again button, or by words while a finished drawing is up (IsReplayRequest), the way
//  "save it" is (DrawStageLesson.SaveRequestKind).
//

using System.Text.RegularExpressions;

namespace OpenClicky.Core.Plugins.DrawStage;

public static partial class DrawingReplay
{
    /// <summary>The quiet before the first line, so the page has begun before the voice does.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(0.6);

    /// <summary>The quiet before line <paramref name="k"/> when it is replayed: as it was, the first line's at most a second
    /// (the wait for the model's first words isn't replayed).</summary>
    public static double PauseBefore(DrawnAnswer drawing, int k) =>
        k == 0 ? Math.Min(drawing.Lines[0].PauseBefore, 1) : drawing.Lines[k].PauseBefore;

    /// <summary>
    /// Draws <paramref name="drawing"/> again on <paramref name="stage"/> where it was last placed, playing each line's audio
    /// with <paramref name="play"/> (to its end) while its strokes are drawn; <paramref name="wait"/> keeps the pauses (and a
    /// line's length when it has no audio). Remembers the drawing again when it has played; a cancellation (esc, a new
    /// question) is thrown and leaves the stage as it is.
    /// </summary>
    public static async Task PlayAsync(DrawStageController stage, DrawnAnswer drawing, Func<byte[], CancellationToken, Task> play,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        wait ??= Task.Delay;
        stage.Begin(drawing.Frame, drawing.Scale, drawing.CanvasWidth, drawing.CanvasHeight, DrawStagePace.Speech, keepPlace: true);
        stage.Feed(drawing.Program, done: true);
        await wait(Lead, cancellationToken);
        for (var k = 0; k < drawing.Lines.Count; k++)
        {
            var line = drawing.Lines[k];
            var pause = PauseBefore(drawing, k);
            if (pause > 0) await wait(TimeSpan.FromSeconds(pause), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stage.LineStarted(k);
            if (line.Audio is { Length: > 0 } audio) await play(audio, cancellationToken);
            else await wait(TimeSpan.FromSeconds(Math.Max(0, line.Seconds)), cancellationToken);
            stage.LineEnded(k);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Every line has ended: anything still waiting is drawn, and the drawing is the one up again.
        stage.FinishNow();
        stage.Remember(drawing);
    }

    /// <summary>
    /// Whether <paramref name="request"/> asks to watch the drawing up again ("play it again", "show me that again", "replay",
    /// "one more time", "repeat that", "دوبارہ دکھاؤ"). Short, and nothing but the asking: "draw it again bigger" or "explain
    /// it again slower" change the drawing, so they go to the model as follow-ups.
    /// </summary>
    public static bool IsReplayRequest(string request)
    {
        var words = Words().Matches(request.ToLowerInvariant().Replace('’', '\'')).Select(match => match.Value).ToList();
        if (words.Count is 0 or > 9) return false;
        if (IsUrduReplayRequest(request)) return true;
        // Asks to see it again: a replay word, or "again" / "one more time" / "from the start".
        var again = words.Contains("again") || words.Contains("replay") || words.Contains("rewatch") || words.Contains("repeat")
            || ContainsRun(words, "one", "more", "time") || ContainsRun(words, "once", "more") || ContainsRun(words, "from", "the", "start")
            || ContainsRun(words, "from", "the", "beginning");
        if (!again) return false;
        // Every other word is the asking itself; any other word changes what is wanted. A longer request needs a verb
        // ("play", "show"…); a short one ("one more time", "once more please") is the asking on its own.
        return words.All(word => AskingWords.Contains(word)) && (words.Count <= 4 || words.Any(word => ReplayVerbs.Contains(word)));
    }

    private static readonly HashSet<string> ReplayVerbs =
        ["replay", "rewatch", "repeat", "play", "show", "watch", "see", "run", "do", "explain", "say", "again"];

    private static readonly HashSet<string> AskingWords =
    [
        .. ReplayVerbs,
        "please", "pls", "can", "could", "would", "will", "you", "u", "hey", "ok", "okay", "so", "now", "just", "clicky",
        "i", "want", "to", "let", "let's", "lets", "me", "us", "it", "that", "this", "the", "drawing", "picture", "diagram",
        "lesson", "animation", "explanation", "once", "more", "one", "time", "from", "start", "beginning", "all", "over", "whole",
        "thing", "thanks", "thank",
    ];

    private static bool ContainsRun(List<string> words, params string[] run)
    {
        for (var i = 0; i + run.Length <= words.Count; i++)
        {
            if (run.Select((word, j) => words[i + j] == word).All(same => same)) return true;
        }
        return false;
    }

    /// <summary>Urdu: "again" (دوبارہ، پھر سے، ایک بار پھر) with show, play, tell or explain (دکھاؤ، چلاؤ، سناؤ، سمجھاؤ).</summary>
    private static bool IsUrduReplayRequest(string request) =>
        (request.Contains("دوبارہ") || request.Contains("پھر سے") || request.Contains("ایک بار پھر"))
        && (request.Contains("دکھا") || request.Contains("چلا") || request.Contains("سنا") || request.Contains("سمجھا"))
        && !request.Contains("بنا"); // "draw it again" (دوبارہ بناؤ) is a follow-up

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}']+")]
    private static partial Regex Words();
}
