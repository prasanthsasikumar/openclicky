//
//  PolyPizza.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Poly Pizza (api.poly.pizza) keeps thousands of free low-poly models. Before a 3D maker builds a piece, the piece is
//  looked for there: the search words are looked up, the drawing model is shown each result's picture, and the one it
//  says is the thing asked for is downloaded as a GLB. Its model carries the title, creator and licence, so the stage
//  can credit it (CC-BY needs a credit; CC0 none).
//
//  Nothing is asked of the network when no Poly Pizza key is set (PieceMakers.For only builds this maker then). Every
//  search word's outcome is kept for a week in the cache folder (a GLB, and an index.json of word → model or none), so
//  the same piece asked for again costs nothing. A search that fails (a 500, a timeout, bad JSON) is not remembered:
//  the next word is tried, and the word may work next time.
//

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>Finds a piece among Poly Pizza's ready-made models; null when none of the search words has one that fits, so
/// the makers after it build the piece as usual.</summary>
public sealed class PolyPizzaPieceMaker : IPieceMaker
{
    /// <summary>Where the search is asked (with the key in X-Auth-Token, and the word URL-escaped after it).</summary>
    public const string SearchBase = "https://api.poly.pizza/v1.1/search";

    /// <summary>A piece's pictures: the word searched for, the whole thing it is part of (null for a whole object), the
    /// thumbnail bytes (one per result, labelled "1", "2"…), and back the picture the drawing model picked (1-based), or
    /// null for NONE. Injectable for tests.</summary>
    public delegate Task<int?> PicturePicker(string word, string? partOf, IReadOnlyList<byte[]> pictures, CancellationToken cancellationToken);

    private const string IndexFile = "index.json";
    private const int SearchLimit = 8;
    private const int PicturesAtMost = 4;
    private const int TrianglesAtMost = 50000;
    private static readonly TimeSpan RememberNothing = TimeSpan.FromDays(7);
    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(8);

    private readonly string key;
    private readonly ClaudeAPI? model;
    private readonly HttpClient client;
    private readonly string cacheDirectory;
    private readonly PicturePicker pick;
    // One maker is made per piece, and a few pieces are made at once: every read and write of the shared index file waits
    // its turn, so no piece's entry is lost.
    private static readonly SemaphoreSlim IndexLock = new(1, 1);

    public PolyPizzaPieceMaker(string key, ClaudeAPI? drawingModel, HttpClient? client = null, string? cacheDirectory = null,
        PicturePicker? picker = null)
    {
        this.key = key;
        model = drawingModel;
        this.client = client ?? HttpClients.Shared;
        this.cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClicky", "Pieces", "polypizza");
        pick = picker ?? PickWithModelAsync;
    }

    public string Name => "Poly Pizza";

    public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var word in SearchWords(request))
            {
                if (await CachedPieceAsync(word, cancellationToken).ConfigureAwait(false) is { } kept)
                {
                    AppLog.Write($"piece: poly pizza \"{word}\": kept from cache ({kept.MadeBy})");
                    return kept;
                }
                if (await CachedNothingAsync(word, cancellationToken).ConfigureAwait(false)) continue;

                var found = await SearchAsync(word, cancellationToken).ConfigureAwait(false);
                if (found is null) continue;
                if (found.Count == 0)
                {
                    AppLog.Write($"piece: poly pizza \"{word}\": none fit");
                    await RememberNothingForAsync(word, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var picked = await PickAsync(word, request.PartOf, found, cancellationToken).ConfigureAwait(false);
                if (picked is null)
                {
                    AppLog.Write($"piece: poly pizza \"{word}\": none fit");
                    await RememberNothingForAsync(word, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var result = found[picked.Value];
                AppLog.Write($"piece: poly pizza \"{word}\": {found.Count} found, picked {picked.Value + 1} ({result.Title})");
                var data = await DownloadAsync(result.Download, cancellationToken).ConfigureAwait(false);
                if (data is null || !PieceMakers.IsGlb(data))
                {
                    AppLog.Write($"piece: poly pizza \"{word}\": download failed");
                    continue;
                }
                await SaveAndRememberAsync(word, result, data, cancellationToken).ConfigureAwait(false);
                return PieceFor(data, result);
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AppLog.Write($"piece: poly pizza failed ({error.GetType().Name})");
            return null;
        }
    }

    // MARK: - Search words

    /// <summary>Words that never go into a search: colours and styles (a colour match is a different model) and the
    /// filler words of an English description.</summary>
    private static readonly HashSet<string> NotSearchable = new(StringComparer.OrdinalIgnoreCase)
    {
        // colours
        "white", "grey", "gray", "red", "blue", "green", "black", "yellow", "orange", "purple", "pink", "brown",
        "silver", "golden", "gold", "beige", "cream", "navy",
        // styles and sizes
        "matte", "matt", "glossy", "shiny", "low", "poly", "small", "large", "big", "cute", "realistic", "simple",
        "detailed", "thin", "thick", "sleek", "modern", "old", "dark", "light",
        // filler
        "a", "an", "the", "with", "and", "of", "for", "on", "in", "at", "to", "its", "their", "his", "her",
        "piece", "part", "two", "three", "four", "one",
    };

    /// <summary>The words to search Poly Pizza by, most specific first: what the drawing gave (find), else up to two
    /// content words from the end of what to make ("a white passenger airliner with two engines" → "engines",
    /// "airliner"). No colour is ever searched for.</summary>
    public static IReadOnlyList<string> SearchWords(PieceRequest request)
    {
        if (request.Find is { Length: > 0 } given)
        {
            return given.Select(word => word.Trim()).Where(word => word.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        }
        // The thing itself comes before what it has or does: "a white passenger airliner with two engines" is an
        // airliner, not engines, so only the words before "with", "of", "that"… are searched.
        var head = Regex.Split(request.Make.ToLowerInvariant(), @"\b(?:with|of|that|which|for|in|on|showing|having|and)\b")[0];
        var words = Regex.Split(head, @"[^\p{L}\p{N}]+")
            .Where(word => word.Length > 1 && !NotSearchable.Contains(word)).ToList();
        var found = new List<string>();
        for (var word = words.Count - 1; word >= 0 && found.Count < 2; word--)
        {
            if (!found.Contains(words[word], StringComparer.OrdinalIgnoreCase)) found.Add(words[word]);
        }
        return found;
    }

    // MARK: - Searching and picking

    private sealed record Result(string Id, string Title, string Attribution, string Thumbnail, string Download,
        string Creator, string Licence);

    /// <summary>The results worth showing, top 4; null when the search failed (a non-200, a timeout or bad JSON), which is
    /// never remembered.</summary>
    private async Task<List<Result>?> SearchAsync(string word, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{SearchBase}/{Uri.EscapeDataString(word)}?Limit={SearchLimit}");
        message.Headers.TryAddWithoutValidation("X-Auth-Token", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestLimit);
        try
        {
            using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"piece: poly pizza \"{word}\": search failed ({(int)response.StatusCode})");
                return null;
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (JsonNode.Parse(text)?["results"] is not JsonArray results) return [];
            var kept = new List<Result>();
            foreach (var node in results)
            {
                if (node is not JsonObject result) continue;
                var title = Text(result, "Title") ?? "";
                var download = Text(result, "Download") ?? "";
                var licence = Text(result, "Licence") ?? "";
                var creator = result["Creator"] is JsonObject maker ? Text(maker, "Username") ?? "" : "";
                var animated = result["Animated"] is JsonValue flag && flag.TryGetValue<bool>(out var turns) && turns;
                if (animated || download.Length == 0 || Whole(result["Tri Count"]) > TrianglesAtMost) continue;
                kept.Add(new Result(Text(result, "ID") ?? title, title, Text(result, "Attribution") ?? "",
                    Text(result, "Thumbnail") ?? "", download, creator, licence));
                if (kept.Count == PicturesAtMost) break;
            }
            return kept;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            AppLog.Write($"piece: poly pizza \"{word}\": search failed ({error.GetType().Name})");
            return null;
        }
    }

    /// <summary>The index (0-based) of the result to use: the drawing model's pick among their pictures, or, when the
    /// pictures can't be shown to it, the first result whose title names the word. Null when none fits.</summary>
    private async Task<int?> PickAsync(string word, string? partOf, IReadOnlyList<Result> results, CancellationToken cancellationToken)
    {
        var pictures = new List<byte[]>();
        foreach (var result in results)
        {
            var picture = await DownloadAsync(result.Thumbnail, cancellationToken).ConfigureAwait(false);
            if (picture is null) break;
            pictures.Add(picture);
        }
        if (pictures.Count == results.Count)
        {
            try
            {
                if (await pick(word, partOf, pictures, cancellationToken).ConfigureAwait(false) is { } chosen
                    && chosen >= 1 && chosen <= results.Count)
                {
                    return chosen - 1;
                }
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                AppLog.Write($"piece: poly pizza \"{word}\": the pictures couldn't be judged ({error.GetType().Name})");
            }
        }
        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].Title.Contains(word, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return null;
    }

    /// <summary>One question to the drawing model with the pictures labelled "1".."4": which of them is the thing asked
    /// for; null for NONE or no clear number.</summary>
    private async Task<int?> PickWithModelAsync(string word, string? partOf, IReadOnlyList<byte[]> pictures, CancellationToken cancellationToken)
    {
        if (model is null || pictures.Count == 0) return null;
        var images = pictures.Select((picture, index) => (Data: picture, Label: (index + 1).ToString(CultureInfo.InvariantCulture))).ToList();
        var ask = $"Which picture is a {word}" + (partOf is { Length: > 0 } of ? $" (part of {of.Trim()})" : "")
            + "? Judge only what the thing is and its shape; ignore colour and style. Reply with only its number, or NONE.";
        var (reply, _, _) = await model.AnalyzeImageStreamingAsync(images, PickerSystemPrompt, null, ask, null, _ => { },
            cancellationToken, 64, forDrawing: true).ConfigureAwait(false);
        if (reply.Contains("NONE", StringComparison.OrdinalIgnoreCase)) return null;
        var number = Regex.Match(reply, @"\d+");
        return number.Success && int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var chosen)
            && chosen >= 1 && chosen <= pictures.Count ? chosen : null;
    }

    private const string PickerSystemPrompt =
        "You match pictures of ready-made 3D models to a word. Reply with only the number of the picture that is the thing asked for, or NONE.";

    // MARK: - Cache

    /// <summary>The index as it is on disk now (a file that can't be read is an empty index).</summary>
    private async Task<Dictionary<string, JsonObject>> ReadIndexAsync(CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(cacheDirectory, IndexFile);
            if (File.Exists(path) && JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) is JsonObject root)
            {
                foreach (var (word, value) in root)
                {
                    if (value is JsonObject entry) loaded[word] = (JsonObject)entry.DeepClone();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"piece: poly pizza cache not read ({error.GetType().Name})");
        }
        return loaded;
    }

    private async Task<JsonObject?> CachedEntryAsync(string word, CancellationToken cancellationToken)
    {
        await IndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            return all.TryGetValue(word, out var entry) ? (JsonObject)entry.DeepClone() : null;
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private async Task<DrawStagePiece?> CachedPieceAsync(string word, CancellationToken cancellationToken)
    {
        var entry = await CachedEntryAsync(word, cancellationToken).ConfigureAwait(false);
        if (entry is null || IsNone(entry) || Text(entry, "file") is not { Length: > 0 } file) return null;
        byte[] data;
        try
        {
            var path = Path.Combine(cacheDirectory, file);
            if (!File.Exists(path)) return null;
            data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (!PieceMakers.IsGlb(data)) return null;
        return PieceFor(data, new Result(Text(entry, "id") ?? "", Text(entry, "title") ?? "", Text(entry, "attribution") ?? "",
            "", "", Text(entry, "creator") ?? "", Text(entry, "licence") ?? ""));
    }

    private async Task<bool> CachedNothingAsync(string word, CancellationToken cancellationToken)
    {
        var entry = await CachedEntryAsync(word, cancellationToken).ConfigureAwait(false);
        if (entry is null || !IsNone(entry) || Text(entry, "until") is not { Length: > 0 } until) return false;
        return DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            && DateTimeOffset.UtcNow < expires;
    }

    private Task RememberNothingForAsync(string word, CancellationToken cancellationToken) =>
        RememberAsync(word, new JsonObject
        {
            ["none"] = true,
            ["until"] = DateTimeOffset.UtcNow.Add(RememberNothing).ToString("O", CultureInfo.InvariantCulture),
        }, cancellationToken);

    /// <summary>Keeps the downloaded model as &lt;ID&gt;.glb in the cache folder and points the word at it; a cache folder
    /// that can't be written leaves the piece as it is (it is still returned).</summary>
    private async Task SaveAndRememberAsync(string word, Result result, byte[] data, CancellationToken cancellationToken)
    {
        var file = SafeFileName(result.Id.Length > 0 ? result.Id : result.Title) + ".glb";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, file), data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"piece: poly pizza cache not kept ({error.GetType().Name})");
            return;
        }
        await RememberAsync(word, new JsonObject
        {
            ["id"] = result.Id,
            ["file"] = file,
            ["title"] = result.Title,
            ["creator"] = result.Creator,
            ["licence"] = result.Licence,
            ["attribution"] = result.Attribution,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string SafeFileName(string name) =>
        Regex.Replace(name, @"[^\p{L}\p{N}._-]+", "_").Trim('_');

    private async Task RememberAsync(string word, JsonObject entry, CancellationToken cancellationToken)
    {
        await IndexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            all[word] = entry;
            Directory.CreateDirectory(cacheDirectory);
            var root = new JsonObject();
            foreach (var (cachedWord, cached) in all) root[cachedWord] = cached.DeepClone();
            await File.WriteAllTextAsync(Path.Combine(cacheDirectory, IndexFile), root.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"piece: poly pizza cache not written ({error.GetType().Name})");
        }
        finally
        {
            IndexLock.Release();
        }
    }

    // MARK: - Downloading

    /// <summary>The bytes at <paramref name="url"/> (a thumbnail or a model); null on a failure or a timeout.</summary>
    private async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Length == 0) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestLimit);
        try
        {
            var data = await client.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
            return data.Length > 0 ? data : null;
        }
        catch (Exception error) when (error is HttpRequestException
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>The piece, credited; CC-BY is written out for the stage's credit line, CC0 and the rest have none.</summary>
    private static DrawStagePiece PieceFor(byte[] data, Result result) =>
        new(DrawStagePieceKind.Glb, data, null,
            $"Poly Pizza: {result.Title} by {result.Creator} ({result.Licence})",
            CreditFor(result.Title, result.Creator, result.Licence));

    private static string? CreditFor(string title, string creator, string licence)
    {
        if (!licence.StartsWith("CC-BY", StringComparison.OrdinalIgnoreCase)) return null;
        var version = licence["CC-BY".Length..].Trim();
        return $"{title} by {creator}, CC BY" + (version.Length > 0 ? " " + version : "");
    }

    // MARK: - Reading the JSON

    private static bool IsNone(JsonObject entry) =>
        entry["none"] is JsonValue none && none.TryGetValue<bool>(out var isNone) && isNone;

    private static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Whole(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number
        : node is JsonValue other && other.TryGetValue<double>(out var real) ? (int)real
        : 0;
}
