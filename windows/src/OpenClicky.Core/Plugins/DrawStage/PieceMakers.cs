//
//  PieceMakers.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  A detailed 3D thing is built in pieces (f.piece in the drawing language): the drawing model plans
//  it as a few pieces (a wing, an engine, a cockpit), each with what it is and the box it fills, and
//  a piece maker makes each one on its own. The stage puts each piece in its box, where it can be
//  labelled, clicked, cut open, mirrored and copied like any other part.
//
//  The makers:
//    - the drawing model (whichever source draws: Claude, OpenAI, Ollama, LM Studio…) builds the
//      piece from the figure's own parts, as a JSON list. No graphics card needed.
//    - LLaMA-Mesh, in Ollama or LM Studio, writes the piece as a mesh (OBJ text).
//    - a Hunyuan3D-2 server (its own api_server.py) makes it as a GLB: from a picture made first
//      by OpenAI when there is an OpenAI key, else from the words (a server started with
//      --enable_t23d).
//  A maker that fails hands the piece to the drawing model. Which ones suit a PC follows its
//  graphics card's memory (GraphicsGrade).
//

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>What a piece came back as: a GLB file, a mesh as OBJ text, or parts of the figure language (a JSON array).</summary>
public enum DrawStagePieceKind
{
    Glb,
    Obj,
    Parts,
}

/// <param name="Data">The GLB file (Glb).</param>
/// <param name="Text">The OBJ text (Obj) or the JSON array of parts (Parts).</param>
/// <param name="MadeBy">Who made it, for the log.</param>
public sealed record DrawStagePiece(DrawStagePieceKind Kind, byte[]? Data, string? Text, string MadeBy);

/// <summary>One piece the drawing asked for: what it is, the box it fills (world units: width across, height up, depth),
/// and what it is part of ("an F-35 fighter jet").</summary>
public sealed record PieceRequest(string Make, double Width, double Height, double Depth, string? PartOf = null);

public interface IPieceMaker
{
    string Name { get; }

    /// <summary>The piece, or null when this maker couldn't make it (throws only when cancelled).</summary>
    Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken);
}

/// <summary>Who makes 3D pieces (Settings → Clicky → Drawings → 3D pieces come from).</summary>
public enum PieceMakerKind
{
    DrawingModel,
    LlamaMeshOllama,
    LlamaMeshLMStudio,
    Hunyuan3D,
}

/// <summary>How much a PC's graphics card can take: low (no card, or under 6 GB), medium (6-15 GB), high (16 GB and up).</summary>
public enum GraphicsGrade
{
    Low,
    Medium,
    High,
}

/// <summary>A maker as Settings offers it: what it needs and the grade it suits.</summary>
public sealed record PieceMakerChoice(PieceMakerKind Kind, string Title, string Needs, GraphicsGrade Grade);

public static partial class PieceMakers
{
    /// <summary>Every maker, from the one that needs least to the one that needs most.</summary>
    public static readonly IReadOnlyList<PieceMakerChoice> Choices =
    [
        new(PieceMakerKind.DrawingModel, "Your drawing model",
            "Builds each piece from the figure's own parts. Works on any PC; best with a strong model (Claude, GPT, a 30B+ local one).", GraphicsGrade.Low),
        new(PieceMakerKind.LlamaMeshOllama, "LLaMA-Mesh in Ollama",
            "Writes each piece as a mesh. About 5 GB (Q4) to 9 GB (Q8): ollama pull hf.co/bartowski/LLaMA-Mesh-GGUF:Q8_0", GraphicsGrade.Medium),
        new(PieceMakerKind.LlamaMeshLMStudio, "LLaMA-Mesh in LM Studio",
            "The same model in LM Studio: search for LLaMA-Mesh and load it.", GraphicsGrade.Medium),
        new(PieceMakerKind.Hunyuan3D, "Hunyuan3D-2 server",
            "Realistic textured pieces from Tencent's Hunyuan3D-2 (python api_server.py). Shape alone about 6 GB, with textures about 16-24 GB. "
            + "With an OpenAI key, a picture of each piece is made first with OpenAI's image model, charged to that key.", GraphicsGrade.High),
    ];

    /// <summary>The grade of a graphics card with <paramref name="videoMemoryGB"/> of its own memory (null: none found).</summary>
    public static GraphicsGrade GradeFor(double? videoMemoryGB) => videoMemoryGB switch
    {
        null or < 5.5 => GraphicsGrade.Low,
        < 15.5 => GraphicsGrade.Medium,
        _ => GraphicsGrade.High,
    };

    /// <summary>The maker to suggest for a grade, and a sentence saying why.</summary>
    public static (PieceMakerKind Kind, string Why) Recommend(GraphicsGrade grade) => grade switch
    {
        GraphicsGrade.High => (PieceMakerKind.Hunyuan3D, "This card can make realistic textured pieces with Hunyuan3D-2; LLaMA-Mesh in Ollama is the quick option."),
        GraphicsGrade.Medium => (PieceMakerKind.LlamaMeshOllama, "This card runs LLaMA-Mesh well (Q4 or Q8 in Ollama); Hunyuan3D-2 fits for shapes without textures."),
        _ => (PieceMakerKind.DrawingModel, "Without a big graphics card, your drawing model builds the pieces from parts; LLaMA-Mesh also runs on the processor, slowly."),
    };

    public static PieceMakerKind KindFrom(string? raw) => raw switch
    {
        "llamaMeshOllama" => PieceMakerKind.LlamaMeshOllama,
        "llamaMeshLmStudio" => PieceMakerKind.LlamaMeshLMStudio,
        "hunyuan3d" => PieceMakerKind.Hunyuan3D,
        _ => PieceMakerKind.DrawingModel,
    };

    public static string RawValue(PieceMakerKind kind) => kind switch
    {
        PieceMakerKind.LlamaMeshOllama => "llamaMeshOllama",
        PieceMakerKind.LlamaMeshLMStudio => "llamaMeshLmStudio",
        PieceMakerKind.Hunyuan3D => "hunyuan3d",
        _ => "drawingModel",
    };

    /// <summary>Where a maker is reached when no address is set: Ollama's and LM Studio's own, Hunyuan3D-2's api_server port.</summary>
    public static string DefaultUrl(PieceMakerKind kind) => kind switch
    {
        PieceMakerKind.LlamaMeshOllama => "http://localhost:11434",
        PieceMakerKind.LlamaMeshLMStudio => "http://localhost:1234",
        PieceMakerKind.Hunyuan3D => "http://localhost:8081",
        _ => "",
    };

    public const string DefaultLlamaMeshModel = "hf.co/bartowski/LLaMA-Mesh-GGUF:Q8_0";

    /// <summary>The maker shell.json picks (`pieceMaker`, `pieceMakerUrl`, `pieceMakerModel`), falling back to the drawing
    /// model (<paramref name="drawingModel"/>) when it fails.</summary>
    public static IPieceMaker For(OpenClickyShellSettings settings, ClaudeAPI drawingModel, Func<string?> openAIKey, HttpClient? client = null)
    {
        var fallback = new DrawingModelPieceMaker(drawingModel);
        var kind = KindFrom(settings.PieceMaker);
        var url = ProviderCredentials.BaseUrl(settings.PieceMakerUrl)
            ?? (kind == PieceMakerKind.LlamaMeshOllama ? ProviderCredentials.BaseUrl(settings.OllamaUrl) : null)
            ?? (kind == PieceMakerKind.LlamaMeshLMStudio ? ProviderCredentials.BaseUrl(settings.LMStudioUrl) : null)
            ?? DefaultUrl(kind);
        IPieceMaker? primary = kind switch
        {
            PieceMakerKind.LlamaMeshOllama or PieceMakerKind.LlamaMeshLMStudio =>
                new LlamaMeshPieceMaker(url, OpenClickyConfiguration.Cleaned(settings.PieceMakerModel) ?? (kind == PieceMakerKind.LlamaMeshOllama ? DefaultLlamaMeshModel : "llama-mesh"),
                    kind == PieceMakerKind.LlamaMeshOllama, client),
            PieceMakerKind.Hunyuan3D => new Hunyuan3DPieceMaker(url, openAIKey, client),
            _ => null,
        };
        return primary is null ? fallback : new FallingBackPieceMaker(primary, fallback);
    }

    // MARK: - Reading what makers send back

    private static readonly HashSet<string> PartKinds =
        ["box", "cyl", "cone", "sphere", "torus", "capsule", "tube", "pipe", "lathe", "extrude", "spring", "gear", "blob"];

    /// <summary>The JSON array of parts in a model's reply (fenced or not): only objects with a known kind, at most 80;
    /// null when there are none. A reply cut off mid-array (it ran out of room) keeps the parts it finished.</summary>
    public static string? PartsIn(string reply)
    {
        var start = reply.IndexOf('[');
        if (start < 0) return null;
        var end = reply.LastIndexOf(']');
        if (end > start && Parts(reply[start..(end + 1)]) is { } whole) return whole;
        // Cut off: the array up to each finished object from the end, closed, until one reads.
        var tries = 0;
        for (var close = reply.LastIndexOf('}'); close > start && tries < 40; close = reply.LastIndexOf('}', close - 1), tries++)
        {
            if (Parts(reply[start..(close + 1)] + "]") is { } salvaged) return salvaged;
        }
        return null;
    }

    private static string? Parts(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return null;
            var parts = new JsonArray();
            foreach (var node in array)
            {
                if (node is JsonObject part && part["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && PartKinds.Contains(name)
                    && parts.Count < 80)
                {
                    parts.Add(part.DeepClone());
                }
            }
            return parts.Count == 0 ? null : parts.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The mesh in a LLaMA-Mesh reply as OBJ text (its "v" and "f" lines); null without at least a few faces, or with
    /// a face that names a vertex it doesn't have.</summary>
    public static string? ObjIn(string reply)
    {
        var vertices = new List<string>();
        var faces = new List<string>();
        foreach (var raw in reply.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (VertexLine().IsMatch(line)) vertices.Add(line);
            else if (FaceLine().IsMatch(line)) faces.Add(line);
        }
        if (vertices.Count < 4 || faces.Count < 2) return null;
        foreach (var face in faces)
        {
            foreach (var corner in face.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                if (!int.TryParse(corner.Split('/')[0], out var index) || index == 0 || Math.Abs(index) > vertices.Count) return null;
            }
        }
        return string.Join('\n', vertices) + "\n" + string.Join('\n', faces) + "\n";
    }

    /// <summary>Whether <paramref name="data"/> is a GLB file (it starts "glTF").</summary>
    public static bool IsGlb(byte[] data) => data.Length > 20 && data[0] == 'g' && data[1] == 'l' && data[2] == 'T' && data[3] == 'F';

    [GeneratedRegex(@"^v(\s+-?\d+(\.\d+)?([eE][-+]?\d+)?){3,6}$")]
    private static partial Regex VertexLine();

    [GeneratedRegex(@"^f(\s+-?\d+(/\d*){0,2}){3,}$")]
    private static partial Regex FaceLine();

    internal static string Size(PieceRequest request) =>
        FormattableString.Invariant($"{request.Width:0.##} wide × {request.Height:0.##} high × {request.Depth:0.##} deep");
}

/// <summary>The drawing model builds the piece from the figure's own parts (a JSON array): any answer source, no graphics card.</summary>
public sealed class DrawingModelPieceMaker(ClaudeAPI model) : IPieceMaker
{
    public string Name => "the drawing model";

    public const string SystemPrompt = """
        You build ONE piece of a 3D model out of simple solid parts, like a detailed model kit. Reply with ONLY a JSON array of parts and nothing else.
        Each part is an object with "kind" and its options:
          {"kind": "box", "w", "h", "d", "round"}  {"kind": "cyl", "r", "h", "r2", "open"}  {"kind": "cone", "r", "h"}  {"kind": "sphere", "r"}
          {"kind": "torus", "r", "tube"}  {"kind": "capsule", "r", "h"}  {"kind": "lathe", "points": [[radius, y], …]}
          {"kind": "extrude", "points": [[x, y], …], "depth", "holes": [[[x, y], …]], "bevel"}  {"kind": "tube", "points": [[x, y, z], …], "r"}
          {"kind": "pipe", "points": [[x, y, z], …], "r", "bend"}  {"kind": "gear", "r", "teeth", "depth"}  {"kind": "spring", "r", "h", "coils", "wire"}
          {"kind": "blob", "balls": [[x, y, z, r], …], "sticks": [{"from", "to", "r", "r2"}], "smooth"}
        Every part also takes "at": [x, y, z], "rot": [degrees x, y, z], "axis": "x" | "y" | "z" (which way a cyl, cone, capsule, lathe, gear or spring points; extrude is flat in x, y and thick along "axis", default z),
        "mat": "steel" | "alu" | "titanium" | "chrome" | "iron" | "gold" | "copper" | "brass" | "plastic:<color>" | "paint:<color>" | "rubber" | "wood" | "stone" | "ceramic" | "glass" | "glow:<color>" | "tissue" | "muscle" | "skin" | "bone" | "leaf" | "fabric" | "pcb" (colors: names or "#rrggbb"),
        "copies": {"n", "step": [dx, dy, dz]} (a row), "around": {"n", "axis", "center"} (repeated round an axis), "mirror": "x" (its mirror image across x = 0).
        The piece fills the box it is given, centred on the origin: x across (the thing's front at +x), y up, z depth. Its outside must reach the box's size in all three directions, since it is placed against the other pieces by that box. Use 10 to 40 parts, real proportions, real materials and colours, and the small details that make it recognisable (panel lines, bolts, vents, ribs, a lens). Keep the parts touching.
        """;

    public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
    {
        var ask = $"Build: {request.Make.Trim()}." + (request.PartOf is { Length: > 0 } of ? $" It is a piece of {of.Trim()}." : "")
            + $" Its box: {PieceMakers.Size(request)}.";
        var (text, _, _) = await model.AnalyzeImageStreamingAsync([], SystemPrompt, null, ask, null, _ => { }, cancellationToken, 8000, forDrawing: true).ConfigureAwait(false);
        return PieceMakers.PartsIn(text) is { } parts ? new DrawStagePiece(DrawStagePieceKind.Parts, null, parts, Name) : null;
    }
}

/// <summary>LLaMA-Mesh (a Llama 3.1 8B that writes meshes) behind Ollama or an OpenAI-compatible server (LM Studio).
/// Ollama is sent the Llama 3.1 prompt itself (raw): it gives the GGUF the Llama Guard template, which answers every ask
/// with a safety verdict instead of a mesh.</summary>
public sealed class LlamaMeshPieceMaker(string baseUrl, string model, bool ollama, HttpClient? client = null) : IPieceMaker
{
    /// <summary>The longest mesh it writes, in tokens (what it was trained to; a mesh cut off there keeps its whole faces).</summary>
    private const int LongestMesh = 4096;

    public string Name => $"LLaMA-Mesh at {baseUrl}";

    public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
    {
        var ask = $"Create a 3D model of {request.Make.Trim()}" + (request.PartOf is { Length: > 0 } of ? $", a piece of {of.Trim()}." : ".");
        var body = ollama
            ? new JsonObject
            {
                ["model"] = model,
                ["prompt"] = $"<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\n{ask}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n",
                ["raw"] = true,
                ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0.6, ["num_predict"] = LongestMesh, ["num_ctx"] = 8192 },
            }
            : new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ask }),
                ["max_tokens"] = LongestMesh,
                ["temperature"] = 0.6,
                ["stream"] = false,
            };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}{(ollama ? "/api/generate" : "/v1/chat/completions")}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await (client ?? HttpClients.Shared).SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var text = (ollama ? reply?["response"] : reply?["choices"]?[0]?["message"]?["content"])?.GetValue<string>() ?? "";
            return PieceMakers.ObjIn(text) is { } obj ? new DrawStagePiece(DrawStagePieceKind.Obj, null, obj, Name) : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>A Hunyuan3D-2 server (python api_server.py): POST /generate with a picture (base64) or words, back comes a GLB.
/// With an OpenAI key, the picture is made first by OpenAI's image model (the server's best route); without one the words
/// are sent (the server must have been started with --enable_t23d). Textures are asked for, and left out on a retry when
/// the server can't make them.</summary>
public sealed class Hunyuan3DPieceMaker(string baseUrl, Func<string?> openAIKey, HttpClient? client = null) : IPieceMaker
{
    public string Name => $"Hunyuan3D-2 at {baseUrl}";

    public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
    {
        var picture = openAIKey() is { } key ? await PictureAsync(request, key, cancellationToken).ConfigureAwait(false) : null;
        foreach (var texture in new[] { true, false })
        {
            var body = new JsonObject
            {
                ["texture"] = texture, ["type"] = "glb", ["seed"] = 1234, ["octree_resolution"] = 256, ["num_inference_steps"] = 30,
                ["guidance_scale"] = 5.0, ["face_count"] = 40000,
            };
            if (picture is not null) body["image"] = picture;
            else body["text"] = $"{request.Make.Trim()}, a single object on a plain white background";
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/generate")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            try
            {
                using var response = await (client ?? HttpClients.Shared).SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (PieceMakers.IsGlb(data)) return new DrawStagePiece(DrawStagePieceKind.Glb, data, null, Name + (texture ? "" : " (no texture)"));
            }
            catch (Exception error) when (error is HttpRequestException || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>A clean picture of the piece alone (base64 PNG) from OpenAI's image model; null when it can't make one.</summary>
    private async Task<string?> PictureAsync(PieceRequest request, string key, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = "gpt-image-1",
            ["prompt"] = $"{request.Make.Trim()}" + (request.PartOf is { Length: > 0 } of ? $" (a piece of {of.Trim()})" : "")
                + ", the whole object alone and centred, three-quarter view, realistic product render, plain white background, soft studio light, no text",
            ["size"] = "1024x1024",
            ["quality"] = "medium",
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await (client ?? HttpClients.Shared).SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return reply?["data"]?[0]?["b64_json"]?.GetValue<string>();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>Tries one maker, then another (the drawing model) when the first made nothing. The first gets a limit of its
/// own (<see cref="PrimaryLimit"/>), so a slow one still leaves the other time.</summary>
public sealed class FallingBackPieceMaker(IPieceMaker primary, IPieceMaker fallback, TimeSpan? primaryLimit = null) : IPieceMaker
{
    public static readonly TimeSpan PrimaryLimit = TimeSpan.FromMinutes(3);

    public string Name => primary.Name;

    public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
    {
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            first.CancelAfter(primaryLimit ?? PrimaryLimit);
            try
            {
                if (await primary.MakeAsync(request, first.Token).ConfigureAwait(false) is { } piece) return piece;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
        return await fallback.MakeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Makes the pieces a drawing asks for: a few at a time, the same piece only once (a second ask for it, or a
/// follow-up's, gets the first one's).</summary>
public sealed class PieceWorkshop(Func<IPieceMaker> currentMaker, int atOnce = 4)
{
    /// <summary>The longest one piece may take, its wait for a slot included (a slow 3D maker, then the drawing model).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Task<DrawStagePiece?>> made = new();
    private readonly SemaphoreSlim slots = new(atOnce);

    public Task<DrawStagePiece?> MakeAsync(PieceRequest request)
    {
        var maker = currentMaker();
        var key = FormattableString.Invariant($"{maker.Name}|{request.Make.Trim().ToLowerInvariant()}|{request.Width:0.#}|{request.Height:0.#}|{request.Depth:0.#}");
        var task = made.GetOrAdd(key, _ => RunAsync(maker, request));
        // A piece that couldn't be made may be tried again: it is forgotten as it finishes, before anyone waiting on it
        // hears, so asking again straight away really tries again. Only this attempt is forgotten, never a newer one.
        _ = task.ContinueWith(done =>
        {
            if (!done.IsCompletedSuccessfully || done.Result is null) made.TryRemove(KeyValuePair.Create(key, done));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task<DrawStagePiece?> RunAsync(IPieceMaker maker, PieceRequest request)
    {
        using var timeout = new CancellationTokenSource(Limit);
        var waited = false;
        try
        {
            await slots.WaitAsync(timeout.Token).ConfigureAwait(false);
            waited = true;
            return await maker.MakeAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (waited) slots.Release();
        }
    }
}
