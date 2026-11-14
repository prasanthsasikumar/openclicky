// Pictures the drawing asks to generate (photo({generate})): the image model Settings picks (OpenAI, Gemini,
// OpenRouter) against fake servers, the picture it sends back, and a failed request making nothing.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Plugins;

public sealed class PictureMakersTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2, 3];

    private sealed class FakeServer(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Authorization, string? GeminiKey)> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Asked)
            {
                Asked.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("x-goog-api-key", out var keys) ? string.Join(",", keys) : null));
            }
            return answer(body);
        }
    }

    private static PictureMakers Maker(HttpMessageHandler handler, string? source, string? key = "test-key", string? model = null) =>
        new(() => new OpenClickyShellSettings { PictureSource = source, PictureModel = model },
            () => new ProviderCredentials(key, null, null, openRouterKey: key, geminiKey: key),
            new HttpClient(handler));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // MARK: - each source

    [Fact]
    public async Task OpenAIMakesThePictureTheDrawingDescribes()
    {
        var server = new FakeServer(_ => Json(new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(Png) }),
        }.ToJsonString()));
        var photo = await Maker(server, "openAI").MakeAsync("a volcano erupting at night", CancellationToken.None);

        Assert.NotNull(photo);
        Assert.Equal(Png, photo!.ImageData);
        Assert.Equal("image/png", photo.MediaType);
        Assert.Equal("Made with gpt-image-1", photo.Credit);

        var asked = Assert.Single(server.Asked);
        Assert.Equal("https://api.openai.com/v1/images/generations", asked.Url);
        Assert.Equal("Bearer test-key", asked.Authorization);
        var body = JsonNode.Parse(asked.Body)!;
        Assert.Equal("gpt-image-1", (string)body["model"]!);
        Assert.Equal("1024x1024", (string)body["size"]!);
        Assert.Contains("a volcano erupting at night", (string)body["prompt"]!);
        Assert.Contains("clear, well lit", (string)body["prompt"]!);
    }

    [Fact]
    public async Task GeminiMakesTheFirstImagePartItSendsBack()
    {
        var server = new FakeServer(_ => Json(new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject { ["text"] = "Here is your volcano." },
                        new JsonObject
                        {
                            ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(Png) },
                        }),
                },
            }),
        }.ToJsonString()));
        var photo = await Maker(server, "gemini").MakeAsync("a volcano erupting at night", CancellationToken.None);

        Assert.NotNull(photo);
        Assert.Equal(Png, photo!.ImageData);
        Assert.Equal("image/png", photo.MediaType);
        Assert.Equal("Made with gemini-2.5-flash-image", photo.Credit);

        var asked = Assert.Single(server.Asked);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-image:generateContent", asked.Url);
        Assert.Equal("test-key", asked.GeminiKey);
        var body = JsonNode.Parse(asked.Body)!;
        Assert.Contains("a volcano erupting at night", (string)body["contents"]![0]!["parts"]![0]!["text"]!);
        Assert.Equal(["IMAGE", "TEXT"], body["generationConfig"]!["responseModalities"]!.AsArray().Select(mode => (string)mode!));
    }

    [Fact]
    public async Task OpenRouterMakesTheImageDataUrlItSendsBack()
    {
        var server = new FakeServer(_ => Json(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["images"] = new JsonArray(new JsonObject
                    {
                        ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(Png) },
                    }),
                },
            }),
        }.ToJsonString()));
        var photo = await Maker(server, "openRouter").MakeAsync("a volcano erupting at night", CancellationToken.None);

        Assert.NotNull(photo);
        Assert.Equal(Png, photo!.ImageData);
        Assert.Equal("image/png", photo.MediaType);
        Assert.Equal("Made with google/gemini-2.5-flash-image", photo.Credit);

        var asked = Assert.Single(server.Asked);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", asked.Url);
        Assert.Equal("Bearer test-key", asked.Authorization);
        var body = JsonNode.Parse(asked.Body)!;
        Assert.Equal("google/gemini-2.5-flash-image", (string)body["model"]!);
        Assert.Equal("user", (string)body["messages"]![0]!["role"]!);
        Assert.Contains("a volcano erupting at night", (string)body["messages"]![0]!["content"]!);
        Assert.Equal(["image", "text"], body["modalities"]!.AsArray().Select(mode => (string)mode!));
    }

    [Fact]
    public async Task APickedModelReplacesTheSourcesDefault()
    {
        var server = new FakeServer(_ => Json(new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(Png) }),
        }.ToJsonString()));
        await Maker(server, "openAI", model: "gpt-image-1-mini").MakeAsync("a volcano", CancellationToken.None);
        Assert.Equal("gpt-image-1-mini", (string)JsonNode.Parse(server.Asked.Single().Body)!["model"]!);
    }

    // MARK: - nothing made

    [Theory]
    [InlineData("openAI")]
    [InlineData("gemini")]
    [InlineData("openRouter")]
    public async Task AFailedRequestMakesNoPicture(string source)
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad prompt"}}""", Encoding.UTF8, "application/json"),
        });
        Assert.Null(await Maker(server, source).MakeAsync("a volcano", CancellationToken.None));
    }

    [Fact]
    public async Task WithNoSourceNothingIsSent()
    {
        var server = new FakeServer(_ => Json("{}"));
        Assert.Null(await Maker(server, null).MakeAsync("a volcano", CancellationToken.None));
        Assert.Empty(server.Asked);
    }

    [Fact]
    public async Task WithoutItsKeyNothingIsSent()
    {
        var server = new FakeServer(_ => Json("{}"));
        Assert.Null(await Maker(server, "openAI", key: null).MakeAsync("a volcano", CancellationToken.None));
        Assert.Empty(server.Asked);
    }

    // MARK: - Settings

    [Fact]
    public void ThePictureSourceAndModelAreKeptInShellJson()
    {
        var decoded = OpenClickyShellSettings.Decode("""{"pictureSource":"gemini","pictureModel":"gemini-2.5-flash-image"}""");
        Assert.Equal("gemini", decoded.PictureSource);
        Assert.Equal("gemini-2.5-flash-image", decoded.PictureModel);
        var encoded = decoded.Encode();
        Assert.Contains("\"pictureSource\":\"gemini\"", encoded);
        Assert.Contains("\"pictureModel\":\"gemini-2.5-flash-image\"", encoded);
        // Off (the default) is left out of the file.
        Assert.DoesNotContain("pictureSource", new OpenClickyShellSettings().Encode());
    }
}
