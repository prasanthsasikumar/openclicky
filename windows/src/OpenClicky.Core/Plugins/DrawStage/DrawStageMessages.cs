//
//  DrawStageMessages.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  The messages between the app and the drawing stage's page (Assets/DrawStage/stage.js), as JSON
//  objects with a "type". The app sends beginStream, continueStream, feed, lineStarted, lineEnded,
//  finishNow, clear, measure, recording, saving, photo and piece; the page sends ready, line, programRan,
//  lineDue, finished, bounds, save, close, photoWanted, pieceWanted, log and error.
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

/// <summary>One message from the page. <c>Id</c> and <c>Query</c> come with a photo it wants (photoWanted); <c>Bounds</c>
/// with "bounds";
/// <c>Size</c> and <c>Of</c> come with a 3D piece it wants made (pieceWanted: Text is what to make).</summary>
public sealed record DrawStageEvent(string Type, int? K, string? Text, string? Id = null, string? Query = null, DrawStageBounds? Bounds = null,
    double[]? Size = null, string? Of = null);

/// <summary>What the page has drawn, as a box in page pixels (x, y, width, height) on a page of <c>PageWidth</c> ×
/// <c>PageHeight</c> pixels; empty when nothing is drawn.</summary>
public sealed record DrawStageBounds(double X, double Y, double Width, double Height, double PageWidth, double PageHeight)
{
    public bool IsEmpty => Width < 1 || Height < 1 || PageWidth < 1 || PageHeight < 1;
}

/// <summary>A photo found for the page: the image bytes, their media type, and who to credit.</summary>
public sealed record DrawStagePhoto(byte[] ImageData, string MediaType, string Credit);

public static class DrawStageMessages
{
    /// <summary>A new program on a canvas of that size; <paramref name="keepPlace"/> leaves the drawing where the person moved
    /// the last one (a replay of it), else it goes where it is drawn.</summary>
    public static string BeginStream(int canvasWidth, int canvasHeight, DrawStagePace pace, bool keepPlace = false, string? scriptFont = null)
    {
        var message = new JsonObject
        {
            ["type"] = "beginStream",
            ["w"] = Math.Max(1, canvasWidth),
            ["h"] = Math.Max(1, canvasHeight),
            ["pace"] = pace == DrawStagePace.Speech ? "speech" : "estimate",
        };
        if (keepPlace) message["keepPlace"] = true;
        // The font Urdu and other Arabic-script words are drawn in (shell.json drawingScriptFont; unset is the bundled one).
        if (scriptFont is { Length: > 0 }) message["scriptFont"] = scriptFont;
        return message.ToJsonString();
    }

    /// <summary>How a save is going (state "working", with how far through it is when known) or how it ended ("done",
    /// "failed"), shown beside the drawing.</summary>
    public static string Saving(string text, double? fraction, string state)
    {
        var message = new JsonObject { ["type"] = "saving", ["text"] = text, ["state"] = state };
        if (fraction is { } known && double.IsFinite(known)) message["fraction"] = Math.Clamp(known, 0, 1);
        return message.ToJsonString();
    }

    /// <summary>A follow-up's program, run after the one on the stage: the drawing and the program's names stay.</summary>
    public static string ContinueStream(DrawStagePace pace) => new JsonObject
    {
        ["type"] = "continueStream",
        ["pace"] = pace == DrawStagePace.Speech ? "speech" : "estimate",
    }.ToJsonString();

    /// <summary>While a drawing is being recorded the page hides its caption and its buttons.</summary>
    public static string Recording(bool on) => new JsonObject { ["type"] = "recording", ["on"] = on }.ToJsonString();

    public static string Feed(string programSoFar, bool done) => new JsonObject
    {
        ["type"] = "feed",
        ["code"] = programSoFar,
        ["done"] = done,
    }.ToJsonString();

    public static string Line(string type, int k) => new JsonObject { ["type"] = type, ["k"] = k }.ToJsonString();

    public static string Simple(string type) => new JsonObject { ["type"] = type }.ToJsonString();

    /// <summary>The finished drawing is remembered: its buttons show, the Quiz me button too when <paramref name="offerQuiz"/>.</summary>
    public static string Remembered(bool offerQuiz) => new JsonObject { ["type"] = "remembered", ["quiz"] = offerQuiz }.ToJsonString();

    /// <summary>The photo the page asked for (photoWanted) as a data URL with its credit; without one, that none was found.</summary>
    public static string Photo(string id, DrawStagePhoto? photo) => photo is null
        ? new JsonObject { ["type"] = "photo", ["id"] = id, ["failed"] = true }.ToJsonString()
        : new JsonObject
        {
            ["type"] = "photo", ["id"] = id, ["credit"] = photo.Credit,
            ["data"] = $"data:{photo.MediaType};base64,{Convert.ToBase64String(photo.ImageData)}",
        }.ToJsonString();

    /// <summary>The 3D piece the page asked for (pieceWanted): a GLB as a data URL, OBJ text or a JSON array of parts; without
    /// one, that it couldn't be made (the page then fills its box itself).</summary>
    public static string Piece(string id, DrawStagePiece? piece)
    {
        if (piece is null) return new JsonObject { ["type"] = "piece", ["id"] = id, ["failed"] = true }.ToJsonString();
        var message = new JsonObject { ["type"] = "piece", ["id"] = id, ["madeBy"] = piece.MadeBy };
        switch (piece.Kind)
        {
            case DrawStagePieceKind.Glb:
                message["kind"] = "glb";
                message["data"] = "data:model/gltf-binary;base64," + Convert.ToBase64String(piece.Data ?? []);
                break;
            case DrawStagePieceKind.Obj:
                message["kind"] = "obj";
                message["text"] = piece.Text;
                break;
            default:
                message["kind"] = "parts";
                message["parts"] = JsonNode.Parse(piece.Text ?? "[]");
                break;
        }
        return message.ToJsonString();
    }

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
            double Num(string key) => message[key] is JsonValue value && value.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;
            var bounds = type == "bounds" ? new DrawStageBounds(Num("x"), Num("y"), Num("w"), Num("h"), Num("pageWidth"), Num("pageHeight")) : null;
            double[]? size = message["size"] is JsonArray sizes && sizes.Count == 3
                ? [.. sizes.Select(value => value is JsonValue number && number.TryGetValue<double>(out var d) && double.IsFinite(d) ? Math.Abs(d) : 1)]
                : null;
            return new DrawStageEvent(type, k, Str("text"), Str("id"), Str("query"), bounds, size, Str("of"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
