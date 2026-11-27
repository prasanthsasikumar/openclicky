// Next questions: after an answer (spoken, a drawing, or an agent's reply), a few short questions a curious child could
// ask next, as buttons to tap, so a lesson keeps going without having to think of the next question. The answer itself
// is left alone: once it is said, one small text-only request asks the model answering for questions about it, while
// the voice is still speaking (CompanionManager.NextQuestions). That works the same for the fast voice, the standard
// voice and drawings; a fast-voice tool couldn't, since its output makes the model speak again.
//
// Only when the person's own key, model or plan answers: on OpenClicky's free allowance the extra request would use up
// questions. Settings → Clicky → Talking → Next questions turns it off.

using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public static partial class NextQuestions
{
    public const string PreferenceKey = "nextQuestions.enabled";

    /// <summary>The most questions offered.</summary>
    public const int Most = 3;

    /// <summary>A longer line is not a short question to tap.</summary>
    public const int LongestQuestion = 80;

    /// <summary>The reply is short: three lines.</summary>
    public const int OutputTokens = 300;

    /// <summary>How long the questions stay up when nothing is tapped.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(30);

    public const string Heading = "You could ask";

    public static bool IsOn(Preferences preferences) => preferences.GetOptionalBool(PreferenceKey) ?? true;

    public static void SetOn(Preferences preferences, bool on) => preferences.Set(PreferenceKey, on);

    /// <summary>Asked only of the person's own key, model or plan, never of OpenClicky's free allowance (or when nothing
    /// can answer).</summary>
    public static bool RouteAllows(ProviderRouteKind kind) =>
        kind is ProviderRouteKind.OpenAIDirect or ProviderRouteKind.AnthropicDirect or ProviderRouteKind.AnswerProvider;

    /// <summary>Whether an exchange has somewhere to go next: an answer of a sentence or more, to a question rather than
    /// thanks, a greeting or a short command.</summary>
    public static bool WorthOffering(string question, string answer)
    {
        var asked = question.Trim();
        var said = answer.Trim();
        if (asked.Length == 0 || said.Length < 40) return false;
        var words = Words().Matches(asked.ToLowerInvariant()).Select(match => match.Value).ToList();
        if (words.Count == 0) return false;
        string[] smallTalk = ["thanks", "thank", "thx", "hi", "hello", "hey", "bye", "goodbye", "ok", "okay", "cool", "nice", "great", "yes", "no", "stop"];
        return !(words.Count <= 4 && words.All(word => smallTalk.Contains(word) || word is "you" or "clicky" or "so" or "much" or "very"));
    }

    public const string SystemPrompt =
        "You suggest what a curious child might ask next. You are given a question a child asked and the answer they just " +
        "heard. Write up to three short follow-up questions they could ask, each on its own line: simple words, under ten " +
        "words each, about the same topic, each going one step further (why, how, what if, what else). Write them in the " +
        "language the answer is in. Write only the questions: no numbers, no bullets, no quotes, nothing before or after. " +
        "If nothing sensible follows (thanks, a greeting, or a task that was just done), write NONE.";

    public static string UserPrompt(string question, string answer) =>
        $"The question: {question.Trim()}\n\nThe answer they heard: {answer.Trim()}";

    /// <summary>The questions in the model's reply: one per line, bullets, numbers and quotes taken off, long lines and
    /// repeats left out, at most <see cref="Most"/>; none for NONE.</summary>
    public static IReadOnlyList<string> Parse(string reply)
    {
        var questions = new List<string>();
        foreach (var raw in reply.Replace("\r", "").Split('\n'))
        {
            var line = Bullet().Replace(raw.Trim(), "").Trim().Trim('"', '“', '”', '\'', '`', '*').Trim();
            if (line.Length == 0) continue;
            if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase) || line.Equals("NONE.", StringComparison.OrdinalIgnoreCase)) return [];
            if (line.Length > LongestQuestion || line.EndsWith(':')) continue;
            if (questions.Any(seen => string.Equals(seen, line, StringComparison.OrdinalIgnoreCase))) continue;
            questions.Add(line);
            if (questions.Count == Most) break;
        }
        return questions;
    }

    [GeneratedRegex(@"^(?:[-*•·–]+|\d+[.)]|[a-c][.)])\s*")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}']+")]
    private static partial Regex Words();
}
