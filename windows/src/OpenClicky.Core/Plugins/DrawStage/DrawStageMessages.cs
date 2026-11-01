//
//  DrawStageMessages.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The messages between the app and the drawing stage's page (Assets/DrawStage/stage.js), as JSON
//  objects with a "type". The app sends beginStream, feed, lineStarted, lineEnded, finishNow and
//  clear; the page sends ready, line, programRan, lineDue, finished, log and error.
//

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>How the page times each beat's strokes: by its own estimate of the line's length (about 2.6 words a second),
/// or by the voice (the page asks for each line with lineDue and waits for lineStarted / lineEnded).</summary>
public enum DrawStagePace
{
    Estimate,
    Speech,
}

/// <summary>One message from the page.</summary>
public sealed record DrawStageEvent(string Type, int? K, string? Text);

public static class DrawStageMessages
{
    public static string BeginStream(int canvasWidth, int canvasHeight, DrawStagePace pace) => new JsonObject
    {
        ["type"] = "beginStream",
        ["w"] = Math.Max(1, canvasWidth),
        ["h"] = Math.Max(1, canvasHeight),
        ["pace"] = pace == DrawStagePace.Speech ? "speech" : "estimate",
    }.ToJsonString();

    public static string Feed(string programSoFar, bool done) => new JsonObject
    {
        ["type"] = "feed",
        ["code"] = programSoFar,
        ["done"] = done,
    }.ToJsonString();

    public static string Line(string type, int k) => new JsonObject { ["type"] = type, ["k"] = k }.ToJsonString();

    public static string Simple(string type) => new JsonObject { ["type"] = type }.ToJsonString();

    /// <summary>An error in the page's own shape, for one the app runs into before the page can say it (no WebView2).</summary>
    public static string Error(string text) => new JsonObject { ["type"] = "error", ["text"] = text }.ToJsonString();

    /// <summary>A page message; null when it isn't a JSON object with a string "type".</summary>
    public static DrawStageEvent? ParseEvent(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject message) return null;
            if (message["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)) return null;
            // Line messages carry "k"; programRan carries its count of lines as "lines".
            int? k = (message["k"] ?? message["lines"]) is JsonValue kValue && kValue.TryGetValue<int>(out var number) ? number : null;
            var text = message["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var words) ? words : null;
            return new DrawStageEvent(type, k, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
