// How the person wants answers, remembered: simpler words, shorter answers, yes or no first. A child
// who says "use easier words" once should not have to say it before every question. The model notices
// the request (in any language) and calls `set_answer_style`, on the ask lane and on fast voice; the
// choice is saved in shell.json's `answerStyle` and added to every lane's prompt (ask, fast voice,
// drawings) until it is switched off, by asking or in Settings → Clicky → How it answers.

using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Answers;

public enum AnswerStyleChoice
{
    /// <summary>Everyday words a young child knows; a hard word is explained.</summary>
    Simpler,
    /// <summary>One or two sentences unless they ask for more.</summary>
    Shorter,
    /// <summary>A question that can be answered yes or no starts with yes or no.</summary>
    YesOrNoFirst,
}

public static class AnswerStyle
{
    public const string ToolName = "set_answer_style";

    public static readonly IReadOnlyList<AnswerStyleChoice> AllCases = [AnswerStyleChoice.Simpler, AnswerStyleChoice.Shorter, AnswerStyleChoice.YesOrNoFirst];

    /// <summary>The stored and tool name: "simpler", "shorter", "yesOrNoFirst".</summary>
    public static string RawValue(this AnswerStyleChoice choice) => choice switch
    {
        AnswerStyleChoice.Simpler => "simpler",
        AnswerStyleChoice.Shorter => "shorter",
        _ => "yesOrNoFirst",
    };

    public static AnswerStyleChoice? FromRawValue(string? rawValue) =>
        AllCases.Cast<AnswerStyleChoice?>().FirstOrDefault(choice => string.Equals(choice!.Value.RawValue(), rawValue?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Settings' row title.</summary>
    public static string Title(this AnswerStyleChoice choice) => choice switch
    {
        AnswerStyleChoice.Simpler => "Simpler words",
        AnswerStyleChoice.Shorter => "Shorter answers",
        _ => "Yes or no first",
    };

    /// <summary>The line each lane's prompt gets while the choice is on.</summary>
    public static string PromptLine(this AnswerStyleChoice choice) => choice switch
    {
        AnswerStyleChoice.Simpler => "use simple, everyday words a young child knows, in short sentences. if a hard word is needed, say what it means right after it.",
        AnswerStyleChoice.Shorter => "keep answers short: one or two sentences, unless they ask for more or the steps need more.",
        _ => "when a question can be answered yes or no, start with yes or no, then explain.",
    };

    /// <summary>What is said when the choice changes (a tool's outcome, spoken after the answer).</summary>
    public static string SpokenSentence(this AnswerStyleChoice choice, bool on) => (choice, on) switch
    {
        (AnswerStyleChoice.Simpler, true) => "okay, i'll use simpler words from now on.",
        (AnswerStyleChoice.Simpler, false) => "okay, back to my usual words.",
        (AnswerStyleChoice.Shorter, true) => "okay, i'll keep my answers short.",
        (AnswerStyleChoice.Shorter, false) => "okay, i'll give fuller answers again.",
        (_, true) => "okay, i'll say yes or no first.",
        _ => "okay, i won't start with yes or no anymore.",
    };

    // MARK: - Stored choices

    /// <summary>The choices that are on, in AllCases order; unknown names in shell.json are ignored.</summary>
    public static IReadOnlyList<AnswerStyleChoice> From(IEnumerable<string>? stored)
    {
        var on = (stored ?? []).Select(FromRawValue).OfType<AnswerStyleChoice>().ToHashSet();
        return AllCases.Where(on.Contains).ToList();
    }

    public static IReadOnlyList<AnswerStyleChoice> Current => Pinned ?? From(OpenClickyConfiguration.Settings.AnswerStyle);

    /// <summary>Tests pin this so the machine's own shell.json does not change their prompts.</summary>
    internal static IReadOnlyList<AnswerStyleChoice>? Pinned { get; set; }

    public static bool IsOn(AnswerStyleChoice choice) => Current.Contains(choice);

    /// <summary>The stored list after switching one choice; null when none is left (the key is dropped from shell.json).</summary>
    public static List<string>? Switched(IEnumerable<string>? stored, AnswerStyleChoice choice, bool on)
    {
        var choices = From(stored).Where(existing => existing != choice).ToList();
        if (on) choices.Add(choice);
        var ordered = AllCases.Where(choices.Contains).Select(RawValue).ToList();
        return ordered.Count == 0 ? null : ordered;
    }

    /// <summary>Saves the choice in shell.json (Settings' switches and the tool both come here).</summary>
    public static void Set(AnswerStyleChoice choice, bool on)
    {
        if (IsOn(choice) == on) return;
        OpenClickyConfiguration.Update(saved => saved.AnswerStyle = Switched(saved.AnswerStyle, choice, on));
    }

    // MARK: - Prompts

    /// <summary>
    /// The ask lane's and fast voice's section: the tool's instruction always, then the choices that are on.
    /// Goes before the reply-language rule, which stays last.
    /// </summary>
    public static string PromptSection(IReadOnlyList<AnswerStyleChoice> choices)
    {
        var section = "answer style: " + ToolInstruction;
        if (choices.Count == 0) return section;
        return section + "\n" + ChosenLines(choices);
    }

    /// <summary>The drawing stage's section: only the choices that are on (drawings have no tools); empty for none.</summary>
    public static string DrawingSection(IReadOnlyList<AnswerStyleChoice> choices) =>
        choices.Count == 0 ? "" : "\n\nHOW THEY WANT ANSWERS (for every line you say and every word you write)\n" + ChosenLines(choices);

    private static string ChosenLines(IReadOnlyList<AnswerStyleChoice> choices) =>
        "they asked for this, so keep doing it until they say otherwise:\n" + string.Join("\n", choices.Select(choice => "- " + choice.PromptLine()));

    private const string ToolInstruction =
        "when the user asks you to change how you answer them, in any language (\"use easier words\", \"that's too long, shorter please\", \"just say yes or no first\"), or to go back to normal, call set_answer_style. "
        + "even a request made once counts. it is about how you answer, not about rewriting their text. "
        + "write your reply first, already in the new style (the answer to their question, or your last answer again if they asked for it again), and call set_answer_style after it: nothing you write after the call is heard.";

    // MARK: - Tool

    public static JsonObject AnthropicToolDefinition() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Remember how the user wants your answers from now on: simpler words, shorter answers, or yes or no first. Call it when they ask you to answer that way (even once), or to stop. Not for rewriting their own text.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["style"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(AllCases.Select(choice => (JsonNode)choice.RawValue()).ToArray()),
                    ["description"] = "simpler: easier words. shorter: shorter answers. yesOrNoFirst: start with yes or no.",
                },
                ["on"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true to start answering this way, false to go back to normal.",
                },
            },
            ["required"] = new JsonArray("style", "on"),
        },
    };

    /// <summary>The same tool in the Realtime session's function shape (Gemini Live's dialect translates it).</summary>
    public static JsonObject RealtimeToolDefinition()
    {
        var anthropic = AnthropicToolDefinition();
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = ToolName,
            ["description"] = anthropic["description"]!.DeepClone(),
            ["parameters"] = anthropic["input_schema"]!.DeepClone(),
        };
    }

    /// <summary>The tool call's choice and whether it goes on; null for arguments that name no choice.
    /// `on` is "true"/"false" (or "1"/"0", as the ask lane flattens booleans); missing means on.</summary>
    public static (AnswerStyleChoice Choice, bool On)? Parse(IReadOnlyDictionary<string, string> arguments)
    {
        if (FromRawValue(arguments.GetValueOrDefault("style")) is not { } choice) return null;
        var on = arguments.GetValueOrDefault("on")?.Trim().ToLowerInvariant() is not ("false" or "0" or "off" or "no");
        return (choice, on);
    }

    /// <summary>Saves a tool call's choice; the sentence to say, or null for arguments that name no choice.</summary>
    /// <param name="set">Where it is saved; null is shell.json (<see cref="Set"/>).</param>
    public static string? Perform(IReadOnlyDictionary<string, string> arguments, Action<string>? log = null, Action<AnswerStyleChoice, bool>? set = null)
    {
        if (Parse(arguments) is not { } request)
        {
            log?.Invoke($"answer style: no style in {string.Join(", ", arguments.Select(argument => $"{argument.Key}={argument.Value}"))}");
            return null;
        }
        (set ?? Set)(request.Choice, request.On);
        log?.Invoke($"answer style: {request.Choice.RawValue()} {(request.On ? "on" : "off")}");
        return request.Choice.SpokenSentence(request.On);
    }
}
