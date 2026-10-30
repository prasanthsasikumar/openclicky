//
//  AskLaneClick.cs
//  OpenClicky (Windows only: the Mac clicks on the fast voice lane alone)
//
//  `click` on the ask lane. A free account has no fast voice, so hands-free and push-to-talk both
//  answer here, and "click the first result" could only point (2026-10-29, on the HP). Claude sees
//  the whole screenshot and places the element the way it places a [POINT] tag, which counts as
//  located (as the fast lane's grounder does since 5602681); the same refusals apply: words that
//  commit something are pointed at, never pressed. There is no second look after the click: the
//  ask lane's answer is already written, so the sentence says what was clicked and stops there.
//

using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Actions;

/// <summary>One click the ask lane's answer asked for, read from its tool call.</summary>
/// <param name="Point">In the screenshot's pixels, origin top-left, like a [POINT] tag.</param>
/// <param name="ScreenNumber">1-based, as the screenshots are labeled; null for the cursor's screen.</param>
public sealed record AskLaneClickRequest(double X, double Y, string Label, string Text, int? ScreenNumber, bool InNewTab);

public static class AskLaneClick
{
    public const string ToolName = "click";

    /// <summary>The tool in Anthropic's shape, next to the quick actions.</summary>
    public static JsonObject AnthropicToolDefinition() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Click something on the user's screen for them: a link, a search result, a button, a tab, a video, a cookie banner's accept button. "
            + "Use it when they tell you to click, open or press something they can see (\"click the first link\", \"open that video\", \"press accept\"). "
            + "Give the element's center in the screenshot's pixel coordinates, exactly as you would for a point tag, and do not also write a point tag. "
            + "A link opens in the tab they are looking at. Controls that send, buy, pay, delete, remove, submit or confirm are pointed at, never clicked.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["x"] = new JsonObject { ["type"] = "integer", ["description"] = "The element's center, in the screenshot's pixels from the left." },
                ["y"] = new JsonObject { ["type"] = "integer", ["description"] = "The element's center, in the screenshot's pixels from the top." },
                ["label"] = new JsonObject { ["type"] = "string", ["description"] = "A short name for it, like \"first result\" or \"accept all button\"." },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "The element's exact on-screen text, if it has any." },
                ["screen"] = new JsonObject { ["type"] = "integer", ["description"] = "Which screenshot it is on (1, 2…), when there is more than one." },
                ["new_tab"] = new JsonObject { ["type"] = "boolean", ["description"] = "Only when they ask for it in a new or another tab." },
            },
            ["required"] = new JsonArray("x", "y", "label"),
        },
    };

    /// <summary>The call's arguments as a click, or null when the point is missing or not a number.</summary>
    public static AskLaneClickRequest? Parse(IReadOnlyDictionary<string, string> arguments)
    {
        if (!TryNumber(arguments, "x", out var x) || !TryNumber(arguments, "y", out var y)) return null;
        var label = arguments.GetValueOrDefault("label")?.Trim() is { Length: > 0 } named ? named : "that";
        var text = arguments.GetValueOrDefault("text")?.Trim() ?? "";
        int? screen = TryNumber(arguments, "screen", out var screenNumber) && screenNumber >= 1 ? (int)screenNumber : null;
        // Tool arguments carry a JSON true as "1" (AnswerProviderHttpSupport.ToolArguments); a plan's [ACTION] line may say "true".
        var inNewTab = arguments.GetValueOrDefault("new_tab")?.Trim().ToLowerInvariant() is "1" or "true";
        return new AskLaneClickRequest(x, y, label, text, screen, inNewTab);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string> arguments, string key, out double value)
    {
        value = 0;
        return arguments.GetValueOrDefault(key) is { } raw
            && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>What is said after the click, or instead of it.</summary>
    public static string SpokenSentence(ScreenClickResult result, string label) => result switch
    {
        ScreenClickResult.Clicked or ScreenClickResult.FollowedLink => $"Clicked {label}.",
        ScreenClickResult.OpenedLinkInNewTab => $"Opened {label} in a new tab.",
        ScreenClickResult.UnderTaskbar => $"{Capitalized(label)} is under the taskbar. Say scroll down a little, then ask me again.",
        ScreenClickResult.OwnWindow => "That spot is on my own window, so I left it.",
        _ => "Windows won't let me click in an app that runs as administrator. Could you click it?",
    };

    /// <summary>Said when the words on the control commit something (ScreenNavigation.ClickIsRisky).</summary>
    public static string RiskySentence(string label) =>
        $"I've pointed at {label}, but you'll want to click that one yourself.";

    public const string UnreadableSentence = "I couldn't tell where to click.";

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
