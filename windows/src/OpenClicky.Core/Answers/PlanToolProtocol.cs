// Port of macos/OpenClicky/OpenClicky/PlanToolProtocol.swift
//
// Quick actions for the plan CLIs. `claude -p --tools ""` and `codex exec` run with no tools of
// their own, so the request's quick-action tools cannot be offered the way an API offers them.
// Instead the system prompt gets a block listing each tool (name, description, compact JSON
// schema) and asking for a line of its own after the spoken words:
//
//   [ACTION: create_folder {"name":"test","location":"desktop"}]
//
// `PlanToolProtocol.Parse` takes those lines out of the finished answer and turns them into the
// same tool calls an API returns. Models are not exact about the format, so the parser tolerates a
// missing colon or space, a code fence around the line, a missing closing bracket and punctuation
// after it; an unknown tool name or arguments that are not a JSON object drop that action, never
// the answer. At most two actions are kept, as the ask lane runs at most two.
//
// `PlanActionLineFilter` does the same for streamed text before it reaches the voice.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Answers;

/// <summary>How a tag ended: its own "]", the end of its line, or the end of the text so far.</summary>
public enum ActionTagEnding
{
    ClosingBracket,
    LineBreak,
    EndOfText,
}

public static class PlanToolProtocol
{
    public const int MaximumActionCount = 2;
    public const string ActionTagOpening = "[ACTION";

    /// <summary>The block appended to the system prompt when the request offers tools; empty for none.</summary>
    public static string InstructionBlock(IReadOnlyList<AnswerRequest.Tool> tools)
    {
        if (tools.Count == 0) return "";
        var lines = new List<string>
        {
            "Quick actions: you can do these things on the person's PC yourself.",
            "To do one, write your spoken words first, then put the action on a line of its own:",
            "[ACTION: <action name> <arguments as one JSON object>]",
            "For example: [ACTION: some_action {\"argument\":\"value\"}]",
            $"Use at most {MaximumActionCount} actions, only the ones listed below, with arguments that match their schema.",
            "Never explain, mention or read out the action line; say in plain words what you are doing.",
            "If no action fits, write no action line.",
            "Actions:",
        };
        foreach (var tool in tools)
        {
            lines.Add($"- {tool.Name}: {tool.Description} Arguments: {CompactSchemaText(tool.InputSchemaJson)}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>The system prompt the CLI gets: the request's own, then the actions block when there is one.</summary>
    public static string SystemPrompt(string systemPrompt, IReadOnlyList<AnswerRequest.Tool> offering)
    {
        var block = InstructionBlock(offering);
        if (block.Length == 0) return systemPrompt;
        var trimmedSystemPrompt = systemPrompt.Trim();
        return trimmedSystemPrompt.Length == 0 ? block : trimmedSystemPrompt + "\n\n" + block;
    }

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The schema re-serialized with sorted keys and no spaces, so the prompt stays short and the
    /// same tools always give the same prompt. A schema that does not parse is passed as it is.
    /// </summary>
    public static string CompactSchemaText(string inputSchemaJson)
    {
        try
        {
            var node = JsonNode.Parse(inputSchemaJson);
            // JSONSerialization only takes an object or an array at the top.
            if (node is not (JsonObject or JsonArray)) return inputSchemaJson;
            return SortedCopy(node)!.ToJsonString(CompactJsonOptions);
        }
        catch (JsonException)
        {
            return inputSchemaJson;
        }
    }

    private static JsonNode? SortedCopy(JsonNode? node) => node switch
    {
        JsonObject jsonObject => new JsonObject(jsonObject
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, SortedCopy(pair.Value)))),
        JsonArray jsonArray => new JsonArray(jsonArray.Select(SortedCopy).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    /// <summary>The answer with every action tag taken out (and trimmed), and the actions it asked for.</summary>
    public static (string Text, IReadOnlyList<ClaudeToolCall> ToolCalls) Parse(string answerText, IReadOnlySet<string> knownToolNames)
    {
        var remainingText = answerText;
        var toolCalls = new List<ClaudeToolCall>();
        var searchStart = 0;
        while (searchStart <= remainingText.Length)
        {
            var openingIndex = remainingText.IndexOf(ActionTagOpening, searchStart, StringComparison.OrdinalIgnoreCase);
            if (openingIndex < 0) break;
            var tagEnd = ScanActionTag(remainingText, openingIndex).End;
            var tagText = remainingText[(openingIndex + ActionTagOpening.Length)..tagEnd];
            if (toolCalls.Count < MaximumActionCount && ToolCall(tagText, knownToolNames, toolCalls.Count) is ClaudeToolCall toolCall)
            {
                toolCalls.Add(toolCall);
            }
            // Punctuation right after the tag ("…}]." ) belonged to the tag, not to the answer.
            var removalEnd = tagEnd;
            while (removalEnd < remainingText.Length && ".,;!".Contains(remainingText[removalEnd])) removalEnd++;
            remainingText = remainingText.Remove(openingIndex, removalEnd - openingIndex);
            searchStart = openingIndex;
        }
        return (CleanedText(remainingText), toolCalls);
    }

    /// <summary>
    /// Where a tag that opens at `openingIndex` ends (exclusive) and how: just after the "]" that
    /// closes it outside the JSON (a "]" inside a JSON string or array does not count), else at the
    /// line break when the model left the bracket off.
    /// </summary>
    public static (int End, ActionTagEnding Ending) ScanActionTag(string text, int openingIndex)
    {
        var braceDepth = 0;
        var bracketDepthInsideJson = 0;
        var isInsideJsonString = false;
        var previousCharacterWasEscape = false;
        var currentIndex = Math.Min(openingIndex + ActionTagOpening.Length, text.Length);
        while (currentIndex < text.Length)
        {
            var character = text[currentIndex];
            if (character == '\n')
            {
                // A tag never spans lines; an unclosed quote or brace must not swallow the answer.
                return (currentIndex, ActionTagEnding.LineBreak);
            }
            if (isInsideJsonString)
            {
                if (previousCharacterWasEscape) previousCharacterWasEscape = false;
                else if (character == '\\') previousCharacterWasEscape = true;
                else if (character == '"') isInsideJsonString = false;
            }
            else
            {
                switch (character)
                {
                    case '"' when braceDepth > 0: isInsideJsonString = true; break;
                    case '{': braceDepth += 1; break;
                    case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                    case '[' when braceDepth > 0: bracketDepthInsideJson += 1; break;
                    case ']':
                        if (braceDepth > 0 && bracketDepthInsideJson > 0) bracketDepthInsideJson -= 1;
                        else if (braceDepth == 0) return (currentIndex + 1, ActionTagEnding.ClosingBracket);
                        break;
                }
            }
            currentIndex++;
        }
        return (text.Length, ActionTagEnding.EndOfText);
    }

    /// <summary>
    /// One call from what follows "[ACTION" in a tag: an optional ":", the tool name, then a JSON
    /// object (or nothing, for a tool without arguments). Null when the name is unknown or the
    /// arguments are not a JSON object.
    /// </summary>
    public static ClaudeToolCall? ToolCall(string tagContent, IReadOnlySet<string> knownToolNames, int callIndex)
    {
        var content = tagContent;
        if (content.EndsWith(']')) content = content[..^1];
        var nameStart = 0;
        while (nameStart < content.Length && (content[nameStart] == ':' || char.IsWhiteSpace(content[nameStart]))) nameStart++;
        content = content[nameStart..];
        var nameLength = 0;
        while (nameLength < content.Length && (char.IsLetter(content[nameLength]) || char.IsNumber(content[nameLength]) || content[nameLength] is '_' or '-')) nameLength++;
        var toolName = content[..nameLength];
        if (toolName.Length == 0 || !knownToolNames.Contains(toolName)) return null;
        var argumentsText = content[nameLength..];
        Dictionary<string, string> arguments;
        var firstBrace = argumentsText.IndexOf('{');
        var lastBrace = argumentsText.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace >= 0 && firstBrace < lastBrace)
        {
            var jsonText = argumentsText[firstBrace..(lastBrace + 1)];
            try
            {
                using var document = JsonDocument.Parse(jsonText);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                arguments = ToolArguments(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else if (argumentsText.All(character => char.IsWhiteSpace(character) || ".,;:)".Contains(character)))
        {
            arguments = new Dictionary<string, string>();
        }
        else
        {
            return null;
        }
        return new ClaudeToolCall($"plan-action-{callIndex + 1}", toolName, arguments);
    }

    /// <summary>Strings stay strings and numbers become their text; anything else is left out (AnswerProviderHttpSupport.ToolArguments).</summary>
    public static Dictionary<string, string> ToolArguments(JsonElement argumentsObject) =>
        AnswerProviderHttpSupport.ToolArguments(argumentsObject);

    private static readonly Regex EmptyFencePattern = new(@"```[A-Za-z]*\s*```", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingSpacePattern = new(@"[ \t]+\n", RegexOptions.CultureInvariant);
    private static readonly Regex BlankLinesPattern = new(@"\n{3,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// The answer once its tags are gone: a code fence that only held a tag goes too, lines that
    /// were only a tag leave no run of blank lines behind, and the ends are trimmed.
    /// </summary>
    public static string CleanedText(string text)
    {
        var cleaned = EmptyFencePattern.Replace(text, "");
        cleaned = TrailingSpacePattern.Replace(cleaned, "\n");
        cleaned = BlankLinesPattern.Replace(cleaned, "\n\n");
        return cleaned.Trim();
    }
}

/// <summary>
/// Holds action tags back from streamed text. Each `Consume` returns the text that is safe to hand
/// on now; `Finish` returns whatever was still held and is not a tag. Text from a "[" is held until
/// it can no longer become "[ACTION"; a tag is then dropped up to its closing "]" (or the end of
/// its line), with any punctuation right after it. A line that is only a code fence ("```json") is
/// dropped as well.
/// </summary>
public sealed class PlanActionLineFilter
{
    private enum State
    {
        /// <summary>Text flows through.</summary>
        Passing,
        /// <summary>Holding from a "[" that might open a tag.</summary>
        HoldingPossibleTag,
        /// <summary>Inside a tag: everything is dropped until it ends.</summary>
        DroppingTag,
        /// <summary>Just after a tag: punctuation is dropped, anything else flows.</summary>
        DroppingPunctuationAfterTag,
        /// <summary>Holding a line that starts with a backtick until it ends.</summary>
        HoldingPossibleFenceLine,
    }

    private readonly object gate = new();
    private State state = State.Passing;
    private string heldText = "";
    private bool isAtLineStart = true;

    public string Consume(string chunk)
    {
        lock (gate)
        {
            var safeText = new StringBuilder();
            foreach (var character in chunk) Handle(character, safeText);
            return safeText.ToString();
        }
    }

    public string Finish()
    {
        lock (gate)
        {
            var safeText = "";
            switch (state)
            {
                case State.HoldingPossibleTag:
                    safeText = heldText;
                    break;
                case State.HoldingPossibleFenceLine:
                    if (!IsFenceOnlyLine(heldText)) safeText = heldText;
                    break;
            }
            heldText = "";
            state = State.Passing;
            return safeText;
        }
    }

    private void Handle(char character, StringBuilder safeText)
    {
        switch (state)
        {
            case State.Passing:
                Pass(character, safeText);
                break;
            case State.DroppingPunctuationAfterTag:
                if (".,;!".Contains(character)) return;
                state = State.Passing;
                Pass(character, safeText);
                break;
            case State.HoldingPossibleTag:
                heldText += character;
                var uppercasedHeldText = heldText.ToUpperInvariant();
                if (uppercasedHeldText.StartsWith(PlanToolProtocol.ActionTagOpening, StringComparison.Ordinal))
                {
                    state = State.DroppingTag;
                    CheckWhetherTagEnded(safeText);
                }
                else if (!PlanToolProtocol.ActionTagOpening.StartsWith(uppercasedHeldText, StringComparison.Ordinal))
                {
                    // Not a tag after all ("[POINT:…" or a bracket in prose): let it through.
                    // The "[" goes out as it is; what followed it may itself open a tag ("[[ACTION").
                    var textAfterBracket = heldText[1..];
                    heldText = "";
                    state = State.Passing;
                    safeText.Append('[');
                    isAtLineStart = false;
                    foreach (var releasedCharacter in textAfterBracket) Handle(releasedCharacter, safeText);
                }
                break;
            case State.DroppingTag:
                heldText += character;
                CheckWhetherTagEnded(safeText);
                break;
            case State.HoldingPossibleFenceLine:
                heldText += character;
                if (character == '\n')
                {
                    if (!IsFenceOnlyLine(heldText)) safeText.Append(heldText);
                    heldText = "";
                    state = State.Passing;
                    isAtLineStart = true;
                }
                else if (heldText.Length == 2 && !heldText.StartsWith("``", StringComparison.Ordinal))
                {
                    // One backtick then something else: inline code, not a fence.
                    safeText.Append(heldText);
                    heldText = "";
                    state = State.Passing;
                    isAtLineStart = false;
                }
                break;
        }
    }

    private void Pass(char character, StringBuilder safeText)
    {
        if (character == '[')
        {
            heldText = "[";
            state = State.HoldingPossibleTag;
            return;
        }
        if (character == '`' && isAtLineStart)
        {
            heldText = "`";
            state = State.HoldingPossibleFenceLine;
            return;
        }
        safeText.Append(character);
        if (character == '\n') isAtLineStart = true;
        else if (!(character == ' ' || character == '\t')) isAtLineStart = false;
    }

    /// <summary>
    /// Ends the tag held in `heldText` once it is complete: after its "]" any punctuation is
    /// dropped too; when its line ended first, the line break itself still goes out.
    /// </summary>
    private void CheckWhetherTagEnded(StringBuilder safeText)
    {
        switch (PlanToolProtocol.ScanActionTag(heldText, 0).Ending)
        {
            case ActionTagEnding.ClosingBracket:
                heldText = "";
                state = State.DroppingPunctuationAfterTag;
                break;
            case ActionTagEnding.LineBreak:
                heldText = "";
                state = State.Passing;
                Pass('\n', safeText);
                break;
        }
    }

    private static readonly Regex FenceOnlyLinePattern = new(@"^```[A-Za-z]*$", RegexOptions.CultureInvariant);

    private static bool IsFenceOnlyLine(string line) => FenceOnlyLinePattern.IsMatch(line.Trim());
}
