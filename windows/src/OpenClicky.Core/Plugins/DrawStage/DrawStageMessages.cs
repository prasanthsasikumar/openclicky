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

/// <summary>One message from the page. <c>Id</c> and <c>Query</c> come with a photo it wants (photoWanted).</summary>
public sealed record DrawStageEvent(string Type, int? K, string? Text, string? Id = null, string? Query = null);

/// <summary>A photo found for the page: the image bytes, their media type, and who to credit.</summary>
public sealed record DrawStagePhoto(byte[] ImageData, string MediaType, string Credit);

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

    /// <summary>The photo the page asked for (photoWanted) as a data URL with its credit; without one, that none was found.</summary>
    public static string Photo(string id, DrawStagePhoto? photo) => photo is null
        ? new JsonObject { ["type"] = "photo", ["id"] = id, ["failed"] = true }.ToJsonString()
        : new JsonObject
        {
            ["type"] = "photo", ["id"] = id, ["credit"] = photo.Credit,
            ["data"] = $"data:{photo.MediaType};base64,{Convert.ToBase64String(photo.ImageData)}",
        }.ToJsonString();

    /// <summary>"image/png", "image/gif", "image/webp", else "image/jpeg", from the file's first bytes.</summary>
    public static string MediaTypeOf(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 ? "image/png"
        : data.Length > 4 && data[0] == 0x47 && data[1] == 0x49 ? "image/gif"
        : data.Length > 12 && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50 ? "image/webp"
        : "image/jpeg";

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
            string? Str(string key) => message[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
            return new DrawStageEvent(type, k, Str("text"), Str("id"), Str("query"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
