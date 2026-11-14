// Poly Pizza (f.piece): a ready-made model is looked for before a piece is made, the drawing model picks among the
// results' pictures, a pick is kept in the cache (with the model), a word with nothing is remembered for a week, and a
// failed search is passed over without being remembered. No network at all without a key.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public sealed class PolyPizzaPieceMakerTests
{
    private static readonly PieceRequest Airliner = new("a white passenger airliner with two engines", 6, 1.4, 1.4, "an airliner");

    private sealed class FakePizza(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Asked) Asked.Add(url);
            return Task.FromResult(answer(url));
        }
    }

    private sealed class Cache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclicky-poly-pizza", Guid.NewGuid().ToString("N"));

        public string Index => System.IO.Path.Combine(Path, "index.json");

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (Exception error) when (error is IOException or DirectoryNotFoundException) { }
        }
    }

    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];

    private static byte[] Glb(string id) => [.. "glTF"u8, .. Encoding.UTF8.GetBytes(id), .. new byte[20]];

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage Failed() => new(HttpStatusCode.InternalServerError);

    private static string Found(params (string Id, string Title, string Licence)[] results) => new JsonObject
    {
        ["total"] = results.Length,
        ["results"] = new JsonArray(results.Select(result => (JsonNode)new JsonObject
        {
            ["ID"] = result.Id,
            ["Title"] = result.Title,
            ["Attribution"] = $"\"{result.Title}\" by Poly by Google",
            ["Thumbnail"] = $"https://static.poly.pizza/{result.Id}.webp",
            ["Download"] = $"https://static.poly.pizza/{result.Id}.glb",
            ["Tri Count"] = 11287,
            ["Animated"] = false,
            ["Licence"] = result.Licence,
            ["Creator"] = new JsonObject { ["Username"] = "Poly by Google" },
        }).ToArray()),
    }.ToJsonString();

    private static PolyPizzaPieceMaker Maker(FakePizza server, string cache, PolyPizzaPieceMaker.PicturePicker picker) =>
        new("test-key", new ClaudeAPI(() => "http://localhost/chat"), new HttpClient(server), cache, picker);

    [Fact]
    public async Task ThePickedResultIsReturnedAndKeptSoAskingAgainCostsNoRequest()
    {
        using var cache = new Cache();
        var server = new FakePizza(url =>
            url.Contains("/search/") ? Bytes(Encoding.UTF8.GetBytes(Found(("engine1", "Engine", "CC-BY 3.0"), ("engine2", "Jet engine", "CC0 1.0"))))
            : url.EndsWith(".glb") ? Bytes(Glb(url.Contains("engine2") ? "engine2" : "other"))
            : Bytes(Webp));
        var picked = new List<int>();
        var maker = Maker(server, cache.Path, (word, partOf, pictures, _) =>
        {
            picked.Add(pictures.Count);
            return Task.FromResult<int?>(2);
        });

        var piece = await maker.MakeAsync(Airliner, CancellationToken.None);

        Assert.NotNull(piece);
        Assert.Equal(DrawStagePieceKind.Glb, piece.Kind);
        Assert.Equal(Glb("engine2"), piece.Data);
        Assert.Contains("Poly Pizza: Jet engine by Poly by Google (CC0 1.0)", piece.MadeBy);
        // CC0 asks for no credit line.
        Assert.Null(piece.Credit);
        Assert.Equal([2], picked);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(cache.Index))!;
        Assert.Equal("engine2.glb", (string)index["airliner"]!["file"]!);
        Assert.Null(index["airliner"]!["none"]);
        Assert.True(File.Exists(Path.Combine(cache.Path, "engine2.glb")));

        var asked = server.Asked.Count;
        var again = await maker.MakeAsync(Airliner, CancellationToken.None);
        Assert.Equal(Glb("engine2"), again!.Data);
        Assert.Equal(asked, server.Asked.Count);
    }

    [Fact]
    public async Task AWordWhoseSearchFailsIsPassedOverAndNotRemembered()
    {
        using var cache = new Cache();
        var server = new FakePizza(url =>
            url.Contains("/search/airliner") ? Failed()
            : url.Contains("/search/passenger") ? Bytes(Encoding.UTF8.GetBytes(Found(("plane1", "Airliner", "CC-BY 3.0"))))
            : url.EndsWith(".glb") ? Bytes(Glb("plane1"))
            : Bytes(Webp));
        var maker = Maker(server, cache.Path, (_, _, _, _) => Task.FromResult<int?>(1));

        var piece = await maker.MakeAsync(Airliner, CancellationToken.None);

        Assert.Equal(Glb("plane1"), piece!.Data);
        Assert.StartsWith("Poly Pizza: Airliner by Poly by Google (CC-BY 3.0)", piece.MadeBy);
        Assert.Equal("Airliner by Poly by Google, CC BY 3.0", piece.Credit);
        Assert.Contains(server.Asked, url => url.Contains("/search/passenger"));
        Assert.Contains(server.Asked, url => url.Contains("/search/airliner"));
        var index = JsonNode.Parse(await File.ReadAllTextAsync(cache.Index))!;
        Assert.Equal("plane1.glb", (string)index["passenger"]!["file"]!);
        // The failed search is not remembered: the word is searched for again next time.
        Assert.Null(index["airliner"]);
    }

    [Fact]
    public async Task NothingFittingIsRememberedForAWeekAndSkipsTheSearch()
    {
        using var cache = new Cache();
        var server = new FakePizza(url =>
            url.Contains("/search/") ? Bytes(Encoding.UTF8.GetBytes(Found(("plane1", "Airliner", "CC-BY 3.0"), ("plane2", "Plane", "CC0 1.0"))))
            : Bytes(Webp));
        var maker = Maker(server, cache.Path, (_, _, _, _) => Task.FromResult<int?>(null));

        Assert.Null(await maker.MakeAsync(new PieceRequest("airliner", 6, 1, 1), CancellationToken.None));

        var index = JsonNode.Parse(await File.ReadAllTextAsync(cache.Index))!;
        Assert.True((bool)index["airliner"]!["none"]!);
        var until = DateTimeOffset.Parse((string)index["airliner"]!["until"]!);
        Assert.InRange(until, DateTimeOffset.UtcNow.AddDays(6), DateTimeOffset.UtcNow.AddDays(8));

        var searched = server.Asked.Count(url => url.Contains("/search/"));
        Assert.Equal(1, searched);
        // A new maker reads the index from disk: that word is not searched again within the week.
        var again = Maker(server, cache.Path, (_, _, _, _) => Task.FromResult<int?>(1));
        Assert.Null(await again.MakeAsync(new PieceRequest("airliner", 6, 1, 1), CancellationToken.None));
        Assert.Equal(searched, server.Asked.Count(url => url.Contains("/search/")));
    }

    [Fact]
    public void WithoutAKeyThereIsNoReadyMadeSearchAndNoneIsAskedFor()
    {
        var server = new FakePizza(_ => Failed());
        var api = new ClaudeAPI(() => "http://localhost/chat");
        var without = PieceMakers.For(new OpenClickyShellSettings(), api, () => null, new HttpClient(server));
        Assert.IsType<DrawingModelPieceMaker>(without);
        Assert.Empty(server.Asked);
        Assert.False(DrawStageLesson.OffersPieces(new OpenClickyShellSettings()));

        var withKey = PieceMakers.For(new OpenClickyShellSettings { PolyPizzaKey = "test-key" }, api, () => null, new HttpClient(server));
        Assert.IsType<FallingBackPieceMaker>(withKey);
        Assert.Equal("Poly Pizza", withKey.Name);
        Assert.Empty(server.Asked);
        Assert.True(DrawStageLesson.OffersPieces(new OpenClickyShellSettings { PolyPizzaKey = "test-key" }));
        Assert.Contains("find (optional)", DrawStageLesson.PiecesSection);
    }

    [Fact]
    public void SearchWordsAreTheEndContentWordsAndNeverAColour()
    {
        var words = PolyPizzaPieceMaker.SearchWords(Airliner);
        var colours = new[] { "white", "grey", "gray", "red", "blue", "green", "black" };
        Assert.DoesNotContain(words, word => colours.Contains(word, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["airliner", "passenger"], words);
        Assert.Equal(["heart"], PolyPizzaPieceMaker.SearchWords(new PieceRequest("a red human heart with its four chambers", 1, 1, 1)).Take(1));
        Assert.Equal(["plane", "airliner"], PolyPizzaPieceMaker.SearchWords(Airliner with { Find = ["plane", "airliner"] }));
    }

    [Fact]
    public void TheSettingsKeyRoundTrips()
    {
        var decoded = OpenClickyShellSettings.Decode("""{"polyPizzaKey":"pk_test"}""");
        Assert.Equal("pk_test", decoded.PolyPizzaKey);
        Assert.Contains("\"polyPizzaKey\":\"pk_test\"", decoded.Encode());
    }
}
