// 3D pieces made apart (f.piece): the graphics card's grade and the maker suggested for it, reading what makers send back
// (parts, an OBJ mesh, a GLB), LLaMA-Mesh and Hunyuan3D-2 against fake servers, falling back to the drawing model, the
// workshop making a piece once, and the stage asking for a piece and getting it.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Plugins.DrawStage;

namespace OpenClicky.Core.Tests.Plugins;

public sealed class PieceMakerTests
{
    private static readonly PieceRequest Wing = new("a thin swept trapezoid fighter wing", 3, 0.2, 1.6, "an F-35 fighter jet");

    // MARK: - which maker suits a PC

    [Theory]
    [InlineData(null, GraphicsGrade.Low, PieceMakerKind.DrawingModel)]
    [InlineData(4.0, GraphicsGrade.Low, PieceMakerKind.DrawingModel)]
    [InlineData(8.0, GraphicsGrade.Medium, PieceMakerKind.LlamaMeshOllama)]
    [InlineData(12.0, GraphicsGrade.Medium, PieceMakerKind.LlamaMeshOllama)]
    [InlineData(24.0, GraphicsGrade.High, PieceMakerKind.Hunyuan3D)]
    public void TheGraphicsCardsMemoryPicksTheGradeAndTheSuggestion(double? memory, GraphicsGrade grade, PieceMakerKind suggested)
    {
        Assert.Equal(grade, PieceMakers.GradeFor(memory));
        Assert.Equal(suggested, PieceMakers.Recommend(grade).Kind);
    }

    [Fact]
    public void EveryMakerIsOfferedAndKeptByItsName()
    {
        Assert.Equal(Enum.GetValues<PieceMakerKind>(), PieceMakers.Choices.Select(choice => choice.Kind));
        foreach (var kind in Enum.GetValues<PieceMakerKind>()) Assert.Equal(kind, PieceMakers.KindFrom(PieceMakers.RawValue(kind)));
        Assert.Equal(PieceMakerKind.DrawingModel, PieceMakers.KindFrom(null));
        Assert.Equal("http://localhost:11434", PieceMakers.DefaultUrl(PieceMakerKind.LlamaMeshOllama));
    }

    // MARK: - reading what makers send back

    [Fact]
    public void PartsAreTheKnownKindsInTheReplysArray()
    {
        var parts = PieceMakers.PartsIn("Here:\n```json\n[{\"kind\": \"box\", \"w\": 1}, {\"kind\": \"rocket\"}, \"x\", {\"kind\": \"cyl\", \"r\": 0.2}]\n```");
        var array = JsonNode.Parse(parts!)!.AsArray();
        Assert.Equal(["box", "cyl"], array.Select(part => (string)part!["kind"]!));
        Assert.Null(PieceMakers.PartsIn("I can't build that."));
        Assert.Null(PieceMakers.PartsIn("[{\"kind\": \"rocket\"}]"));
        // Cut off mid-part: the finished parts are kept.
        var cut = PieceMakers.PartsIn("[{\"kind\": \"box\", \"at\": [0, 1, 0]}, {\"kind\": \"sphere\", \"r\": 1}, {\"kind\": \"cyl\", \"r\": 0.");
        Assert.Equal(["box", "sphere"], JsonNode.Parse(cut!)!.AsArray().Select(part => (string)part!["kind"]!));
    }

    [Fact]
    public void AMeshIsItsVertexAndFaceLines()
    {
        const string reply = "Sure! Here is the model:\nv 0 0 0\nv 63 0 0\nv 63 0 63\nv 31 50 31\nf 1 2 4\nf 2 3 4\nf 1 4 3\nThat's a pyramid.";
        Assert.Equal("v 0 0 0\nv 63 0 0\nv 63 0 63\nv 31 50 31\nf 1 2 4\nf 2 3 4\nf 1 4 3\n", PieceMakers.ObjIn(reply));
        Assert.Null(PieceMakers.ObjIn("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 9\nf 1 2 3"));
        Assert.Null(PieceMakers.ObjIn("no mesh here"));
        Assert.True(PieceMakers.IsGlb([.. "glTF"u8, .. new byte[20]]));
        Assert.False(PieceMakers.IsGlb([.. "{\"error\"}"u8, .. new byte[20]]));
    }

    // MARK: - LLaMA-Mesh and Hunyuan3D-2 against fake servers

    private sealed class FakeServer(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Asked) Asked.Add((request.RequestUri!.ToString(), body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LlamaMeshIsAskedForTheModelAndItsMeshComesBack()
    {
        var reply = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = "v 0 0 0\nv 9 0 0\nv 0 9 0\nv 0 0 9\nf 1 2 3\nf 1 2 4" } }) };
        var server = new FakeServer((_, _) => Json(reply.ToJsonString()));
        var maker = new LlamaMeshPieceMaker("http://ai-pc:1234", "llama-mesh", ollama: false, new HttpClient(server));
        var piece = await maker.MakeAsync(Wing, CancellationToken.None);
        Assert.Equal(DrawStagePieceKind.Obj, piece!.Kind);
        Assert.StartsWith("v 0 0 0", piece.Text);
        var (url, body) = Assert.Single(server.Asked);
        Assert.Equal("http://ai-pc:1234/v1/chat/completions", url);
        Assert.Contains("Create a 3D model of a thin swept trapezoid fighter wing, a piece of an F-35 fighter jet.", body);
        Assert.Contains("\"llama-mesh\"", body);
    }

    [Fact]
    public async Task OllamaIsSentTheLlamaPromptRawSoItsGuardTemplateIsSkipped()
    {
        var reply = new JsonObject { ["response"] = "Here is the generated mesh.\n```obj\nv 0 0 0\nv 9 0 0\nv 0 9 0\nv 0 0 9\nf 1 2 3\nf 1 2 4\nf 2 3" };
        var server = new FakeServer((_, _) => Json(reply.ToJsonString()));
        var maker = new LlamaMeshPieceMaker("http://ai-pc:11434", PieceMakers.DefaultLlamaMeshModel, ollama: true, new HttpClient(server));
        var piece = await maker.MakeAsync(Wing, CancellationToken.None);
        // The face cut off mid-line is left out.
        Assert.Equal("v 0 0 0\nv 9 0 0\nv 0 9 0\nv 0 0 9\nf 1 2 3\nf 1 2 4\n", piece!.Text);
        var (url, body) = Assert.Single(server.Asked);
        Assert.Equal("http://ai-pc:11434/api/generate", url);
        var sent = JsonNode.Parse(body)!;
        Assert.True(sent["raw"]!.GetValue<bool>());
        Assert.Equal(PieceMakers.DefaultLlamaMeshModel, sent["model"]!.GetValue<string>());
        Assert.Equal("<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\nCreate a 3D model of a thin swept trapezoid fighter wing, a piece of an F-35 fighter jet.<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n",
            sent["prompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASlowMakerIsGivenUpOnInTimeForTheFallback()
    {
        var made = new DrawStagePiece(DrawStagePieceKind.Parts, null, "[]", "ready");
        using var whole = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var maker = new FallingBackPieceMaker(new Stuck(), new Ready(made), primaryLimit: TimeSpan.FromMilliseconds(50));
        Assert.Same(made, await maker.MakeAsync(Wing, whole.Token));
        Assert.False(whole.IsCancellationRequested);
    }

    private sealed class Stuck : IPieceMaker
    {
        public string Name => "stuck";

        public async Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class Ready(DrawStagePiece piece) : IPieceMaker
    {
        public string Name => "ready";

        public Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken) => Task.FromResult<DrawStagePiece?>(piece);
    }

    [Fact]
    public async Task HunyuanGetsAPictureFromOpenAIFirstAndRetriesWithoutTextures()
    {
        var glb = "glTF"u8.ToArray().Concat(new byte[40]).ToArray();
        var server = new FakeServer((request, body) =>
            request.RequestUri!.Host == "api.openai.com" ? Json("""{"data":[{"b64_json":"UElDVFVSRQ=="}]}""")
            : body.Contains("\"texture\":true") ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(glb) });
        var maker = new Hunyuan3DPieceMaker("http://ai-pc:8081", () => "sk-test", new HttpClient(server));
        var piece = await maker.MakeAsync(Wing, CancellationToken.None);
        Assert.Equal(DrawStagePieceKind.Glb, piece!.Kind);
        Assert.Equal(glb, piece.Data);
        Assert.Contains("no texture", piece.MadeBy);
        Assert.Equal(["https://api.openai.com/v1/images/generations", "http://ai-pc:8081/generate", "http://ai-pc:8081/generate"], server.Asked.Select(asked => asked.Url));
        Assert.Contains("\"image\":\"UElDVFVSRQ==\"", server.Asked[1].Body);
    }

    [Fact]
    public async Task WithoutAnOpenAIKeyHunyuanIsSentTheWords()
    {
        var server = new FakeServer((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. "glTF"u8, .. new byte[40]]) });
        await new Hunyuan3DPieceMaker("http://localhost:8081", () => null, new HttpClient(server)).MakeAsync(Wing, CancellationToken.None);
        var (_, body) = Assert.Single(server.Asked);
        Assert.Contains("\"text\":\"a thin swept trapezoid fighter wing", body);
        Assert.DoesNotContain("\"image\"", body);
    }

    private sealed class Maker(Func<PieceRequest, DrawStagePiece?> make, string name = "fake") : IPieceMaker
    {
        public int Calls;
        public string Name => name;

        public Task<DrawStagePiece?> MakeAsync(PieceRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(make(request));
        }
    }

    [Fact]
    public async Task AMakerThatFailsHandsThePieceToTheFallback()
    {
        var failing = new Maker(_ => null);
        var fallback = new Maker(_ => new DrawStagePiece(DrawStagePieceKind.Parts, null, "[]", "the drawing model"));
        var piece = await new FallingBackPieceMaker(failing, fallback).MakeAsync(Wing, CancellationToken.None);
        Assert.Equal("the drawing model", piece!.MadeBy);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task TheWorkshopMakesTheSamePieceOnceAndTriesAFailedOneAgain()
    {
        var made = new Maker(_ => new DrawStagePiece(DrawStagePieceKind.Obj, null, "v", "fake"));
        var workshop = new PieceWorkshop(() => made);
        await Task.WhenAll(workshop.MakeAsync(Wing), workshop.MakeAsync(Wing with { Make = "  A thin swept trapezoid fighter WING " }));
        Assert.Equal(1, made.Calls);

        var failing = new Maker(_ => null);
        var again = new PieceWorkshop(() => failing);
        Assert.Null(await again.MakeAsync(Wing));
        // Straight away, with no wait: a failed piece is forgotten before its caller hears (a 50 ms wait here was flaky).
        Assert.Null(await again.MakeAsync(Wing));
        Assert.Equal(2, failing.Calls);
    }

    [Fact]
    public void SettingsPickTheMakerAndItsAddress()
    {
        var api = new OpenClicky.Core.Answers.ClaudeAPI(() => "http://localhost/chat");
        Assert.IsType<DrawingModelPieceMaker>(PieceMakers.For(new OpenClickyShellSettings(), api, () => null));
        var ollama = PieceMakers.For(new OpenClickyShellSettings { PieceMaker = "llamaMeshOllama", OllamaUrl = "http://ai-pc:11434" }, api, () => null);
        Assert.Equal("LLaMA-Mesh at http://ai-pc:11434", ollama.Name);
        var hunyuan = PieceMakers.For(new OpenClickyShellSettings { PieceMaker = "hunyuan3d", PieceMakerUrl = "http://ai-pc:8081/" }, api, () => null);
        Assert.Equal("Hunyuan3D-2 at http://ai-pc:8081", hunyuan.Name);
    }

    // MARK: - the stage asks, the app answers

    private sealed class Page : IDrawStageSurface
    {
        public readonly List<string> Posted = [];
        public Action<string>? MessageReceived { get; set; }
        public void Present(PixelRect frame, double scale) => MessageReceived?.Invoke("""{"type":"ready"}""");
        public void Hide() { }
        public void Prepare() { }
        public void Post(string json) { lock (Posted) Posted.Add(json); }
    }

    [Fact]
    public async Task APieceThePageWantsIsMadeAndSentBack()
    {
        var page = new Page();
        var stage = new DrawStageController { Surface = page };
        page.Present(new PixelRect(0, 0, 10, 10), 1);
        PieceRequest? asked = null;
        stage.MakePiece = request => { asked = request; return Task.FromResult<DrawStagePiece?>(new DrawStagePiece(DrawStagePieceKind.Glb, [1, 2, 3], null, "fake")); };
        page.MessageReceived!("""{"type":"pieceWanted","id":"part7","text":"a swept wing","of":"an F-35","size":[3,-0.2,1.6]}""");
        for (var i = 0; i < 50 && page.Posted.Count == 0; i++) await Task.Delay(10);
        Assert.Equal(new PieceRequest("a swept wing", 3, 0.2, 1.6, "an F-35"), asked);
        var sent = JsonNode.Parse(Assert.Single(page.Posted))!;
        Assert.Equal("piece", (string)sent["type"]!);
        Assert.Equal("part7", (string)sent["id"]!);
        Assert.Equal("glb", (string)sent["kind"]!);
        Assert.Equal("data:model/gltf-binary;base64,AQID", (string)sent["data"]!);
        Assert.True((bool)JsonNode.Parse(DrawStageMessages.Piece("p", null))!["failed"]!);
        Assert.Equal("box", (string)JsonNode.Parse(DrawStageMessages.Piece("p", new DrawStagePiece(DrawStagePieceKind.Parts, null, "[{\"kind\":\"box\"}]", "m")))!["parts"]![0]!["kind"]!);
    }

    [Fact]
    public void PiecesAreOfferedWhenAMakerIsSetUpOrDrawingsAreVeryDetailed()
    {
        Assert.False(DrawStageLesson.OffersPieces(new OpenClickyShellSettings()));
        Assert.True(DrawStageLesson.OffersPieces(new OpenClickyShellSettings { PieceMaker = "hunyuan3d" }));
        Assert.True(DrawStageLesson.OffersPieces(new OpenClickyShellSettings { DrawingDetail = "veryDetailed" }));
        Assert.Contains("f.piece({make, of, size", DrawStageLesson.SystemPrompt(1280, 720, pieces: true));
        Assert.DoesNotContain("f.piece(", DrawStageLesson.SystemPrompt(1280, 720));
    }
}
