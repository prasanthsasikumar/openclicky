// The person's own models (Ollama, LM Studio, any OpenAI-compatible server): their addresses, when they can answer
// and in which order, the request without a key at their own address, asking again without tools a local model
// says it can't take, and only then, and reading the models a server lists.

using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Plugins.DrawStage;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Answers;

public class SelfHostedSourcesTests
{
    private static AnswerRequest Request(bool withTools) => new(
        [new AnswerRequest.Image([1, 2, 3], "screen", "image/jpeg")],
        "system", [], "what is this?",
        withTools ? [new AnswerRequest.Tool("point_at", "Point", """{"type":"object","properties":{}}""")] : []);

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData(" http://localhost:11434/ ", "http://localhost:11434")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234")]
    [InlineData("https://my.server/v1/", "https://my.server")]
    [InlineData("localhost:11434", null)]
    [InlineData("ftp://x", null)]
    [InlineData("", null)]
    public void AnAddressIsKeptWithoutItsSlashOrV1(string typed, string? kept) =>
        Assert.Equal(kept, ProviderCredentials.BaseUrl(typed));

    [Fact]
    public void ASelfHostedSourceAnswersOnceItsAddressIsSetAfterTheKeysAndBeforeThePlans()
    {
        var none = new ProviderCredentials(null, null, null, codexPlanOn: true);
        Assert.False(none.CanAnswer(AnswerSource.Ollama));

        var both = new ProviderCredentials(null, null, "account-token", groqKey: "gsk", codexPlanOn: true,
            ollamaUrl: "http://localhost:11434", lmStudioUrl: "http://localhost:1234/v1", customOpenAIUrl: "https://my.server", customOpenAIKey: " sk-mine ");
        Assert.Equal(
            [AnswerSource.Groq, AnswerSource.Ollama, AnswerSource.LMStudio, AnswerSource.CustomOpenAI, AnswerSource.CodexPlan, AnswerSource.OpenClickyAccount],
            both.AnswerSourcesInOrder);
        Assert.Equal("http://localhost:1234", both.SelfHostedUrl(AnswerSource.LMStudio));
        Assert.Equal("sk-mine", both.Key(AnswerSource.CustomOpenAI));
        Assert.Null(both.Key(AnswerSource.Ollama));
    }

    [Fact]
    public void ThePersonCanPickALocalModelToAnswer()
    {
        var credentials = new ProviderCredentials("sk-openai", null, null, ollamaUrl: "http://localhost:11434", preferredAnswerSource: AnswerSource.Ollama);
        var route = ProviderRouter.Route(ProviderCapability.Claude, credentials);
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.Ollama), route);
    }

    [Fact]
    public void TheRawValuesReadBackFromShellJson()
    {
        Assert.Equal("ollama", AnswerSource.Ollama.Id());
        Assert.Equal("lmStudio", AnswerSource.LMStudio.Id());
        Assert.Equal("customOpenAI", AnswerSource.CustomOpenAI.Id());
        Assert.Equal(AnswerSource.LMStudio, AnswerSources.FromRawValue("lmStudio"));
        Assert.True(AnswerSource.CustomOpenAI.IsSelfHosted());
        Assert.False(AnswerSource.OpenAI.IsSelfHosted());
    }

    [Fact]
    public void ALocalRequestGoesToItsOwnAddressWithNoKey()
    {
        using var request = OpenAIChatAnswerProvider.MakeHttpRequest(AnswerSource.Ollama, "", "gemma3", Request(withTools: false),
            new Uri("http://localhost:11434/v1/chat/completions"));
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.RequestUri!.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
        Assert.Equal("gemma3", (string)body["model"]!);

        using var withKey = OpenAIChatAnswerProvider.MakeHttpRequest(AnswerSource.CustomOpenAI, "sk-mine", "m", Request(withTools: false),
            new Uri("https://my.server/v1/chat/completions"));
        Assert.Equal("Bearer sk-mine", withKey.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ALocalModelThatRefusesToolsIsAskedAgainWithoutThem()
    {
        var bodies = new List<JsonNode>();
        var calls = 0;
        AnswerHttpTransport transport = (request, _) =>
        {
            bodies.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!);
            return Task.FromResult(++calls == 1
                ? new AnswerHttpResponseLines(400, Lines("""{"error":{"message":"registry.ollama.ai/library/gemma3 does not support tools"}}"""))
                : new AnswerHttpResponseLines(200, Lines("""data: {"choices":[{"delta":{"content":"a red square"}}]}""", "data: [DONE]")));
        };
        var provider = new OpenAIChatAnswerProvider(AnswerSource.Ollama, "", "gemma3", transport, new Uri("http://localhost:11434/v1/chat/completions"));
        var result = await provider.AnswerAsync(Request(withTools: true), _ => { });
        Assert.Equal("a red square", result.Text);
        Assert.Equal(2, calls);
        Assert.NotNull(bodies[0]["tools"]);
        Assert.Null(bodies[1]["tools"]);
    }

    [Theory]
    [InlineData(400, "messages: the image is too large")]
    [InlineData(401, "invalid api key")]
    [InlineData(413, "request entity too large")]
    [InlineData(429, "rate limited, try again later")]
    public async Task ALocalModelThatRefusesForAnotherReasonIsNotAskedAgain(int status, string message)
    {
        var calls = 0;
        AnswerHttpTransport transport = (_, _) =>
        {
            calls++;
            return Task.FromResult(new AnswerHttpResponseLines(status, Lines(new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString())));
        };
        var provider = new OpenAIChatAnswerProvider(AnswerSource.Ollama, "", "gemma3", transport, new Uri("http://localhost:11434/v1/chat/completions"));
        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(Request(withTools: true), _ => { }));
        Assert.Equal(1, calls);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task AVendorThatRefusesIsNotAskedAgain()
    {
        var calls = 0;
        AnswerHttpTransport transport = (_, _) =>
        {
            calls++;
            return Task.FromResult(new AnswerHttpResponseLines(400, Lines("""{"error":{"message":"bad"}}""")));
        };
        var provider = new OpenAIChatAnswerProvider(AnswerSource.Groq, "gsk", "llama", transport);
        await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(Request(withTools: true), _ => { }));
        Assert.Equal(1, calls);
    }

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }

    // MARK: - the models a server lists

    [Fact]
    public void TheModelListIsReadAndAModelThatSeesImagesIsPreferred()
    {
        var models = LocalModels.ParseModelList("""{"object":"list","data":[{"id":"llama3.1:8b"},{"id":"gemma3:12b"},{"id":"qwen2.5-vl:7b"}]}""");
        Assert.Equal(["llama3.1:8b", "gemma3:12b", "qwen2.5-vl:7b"], models);
        Assert.Equal("gemma3:12b", LocalModels.DefaultModel(models));
        Assert.Equal("llama3.1:8b", LocalModels.DefaultModel(["llama3.1:8b", "phi4"]));
        Assert.Null(LocalModels.DefaultModel([]));
        Assert.Empty(LocalModels.ParseModelList("not json"));
        Assert.Empty(LocalModels.ParseModelList("""{"models":[]}"""));
    }

    [Fact]
    public async Task APickedLocalModelAnswersAtItsAddress()
    {
        var credentials = new ProviderCredentials(null, null, null, ollamaUrl: "http://127.0.0.1:11434");
        var provider = await AnswerProviderFactory.MakeAsync(AnswerSource.Ollama, credentials, new Dictionary<string, string> { ["ollama"] = "llava:7b" });
        var chat = Assert.IsType<OpenAIChatAnswerProvider>(provider);
        Assert.Equal("llava:7b", chat.Model);
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", chat.ChatCompletionsUrl!.ToString());
    }

    [Fact]
    public async Task ASourceThatIsNotOnSaysWhereToTurnItOn()
    {
        var error = await Assert.ThrowsAsync<AnswerProviderException>(() =>
            AnswerProviderFactory.MakeAsync(AnswerSource.LMStudio, new ProviderCredentials(null, null, null), new Dictionary<string, string>()));
        Assert.Contains("settings, models", error.Message);
    }

    // MARK: - who writes drawings

    [Fact]
    public void DrawingsAreWrittenByTheAnswerModelUnlessAnotherIsPicked()
    {
        var answers = new ClaudeAPI(() => "http://localhost/chat", warmUpConnection: false);
        Assert.Same(answers, DrawStageLesson.WriterFor(new OpenClickyShellSettings(), answers));
        var writer = DrawStageLesson.WriterFor(new OpenClickyShellSettings { DrawingSource = "anthropic", DrawingModel = "claude-opus-5-5" }, answers);
        Assert.NotSame(answers, writer);
        Assert.Equal("claude-opus-5-5", writer.Model);
    }
}
