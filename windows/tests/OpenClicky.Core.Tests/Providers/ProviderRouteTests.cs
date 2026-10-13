// Ports ProviderRouteTests.swift: every capability × own key / account / neither, the direct
// requests (shaped like the backend's own-key path), and the 401 rule for a PC with no account.
// Nothing here reads shell.json or any file.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Providers;

public sealed class ProviderRouteTests
{
    private static readonly ProviderCredentials OpenAIOnly = new("sk-user", null, null);
    private static readonly ProviderCredentials AnthropicOnly = new(null, "ak-user", null);
    private static readonly ProviderCredentials BothKeys = new("sk-user", "ak-user", null);
    private static readonly ProviderCredentials AccountOnly = new(null, null, "tok");
    private static readonly ProviderCredentials OpenAIAndAccount = new("sk-user", null, "tok");
    private static readonly ProviderCredentials Nothing = new(" ", "", "\n");

    private static readonly ProviderCapability[] OpenAICapabilities =
        [ProviderCapability.OpenAIChat, ProviderCapability.Speech, ProviderCapability.Transcription, ProviderCapability.Realtime];

    // MARK: the decision

    [Fact]
    public void AnOwnOpenAIKeyGoesStraightToOpenAI()
    {
        foreach (var capability in OpenAICapabilities)
        {
            Assert.Equal(ProviderRoute.OpenAIDirect, ProviderRouter.Route(capability, OpenAIOnly));
            Assert.Equal(ProviderRoute.OpenAIDirect, ProviderRouter.Route(capability, OpenAIAndAccount));
        }
    }

    [Fact]
    public void AnOwnAnthropicKeyGoesStraightToAnthropic()
    {
        Assert.Equal(ProviderRoute.AnthropicDirect, ProviderRouter.Route(ProviderCapability.Claude, AnthropicOnly));
        Assert.Equal(ProviderRoute.AnthropicDirect, ProviderRouter.Route(ProviderCapability.Claude, BothKeys));
    }

    [Fact]
    public void AnAccountCoversWhatNoOwnKeyDoes()
    {
        foreach (var capability in Enum.GetValues<ProviderCapability>())
            Assert.Equal(ProviderRoute.Backend, ProviderRouter.Route(capability, AccountOnly));
        // Signed in with an OpenAI key too: questions run on the key, not the credits.
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.OpenAI), ProviderRouter.Route(ProviderCapability.Claude, OpenAIAndAccount));
        Assert.Equal(ProviderRoute.Backend, ProviderRouter.Route(ProviderCapability.AssemblyAIStreaming, OpenAIAndAccount));
    }

    [Fact]
    public void WithNeitherTheRouteSaysWhichKeyWouldDo()
    {
        foreach (var capability in OpenAICapabilities)
        {
            Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.NeedsOpenAIKeySentence), ProviderRouter.Route(capability, Nothing));
            Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.NeedsOpenAIKeySentence), ProviderRouter.Route(capability, AnthropicOnly));
        }
        Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.NeedsAnthropicKeySentence), ProviderRouter.Route(ProviderCapability.Claude, Nothing));
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.OpenAI), ProviderRouter.Route(ProviderCapability.Claude, OpenAIOnly));
        Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.AssemblyAINeedsAccountSentence), ProviderRouter.Route(ProviderCapability.AssemblyAIStreaming, BothKeys));
    }

    [Fact]
    public void AutomaticAnswersWithTheFirstSourceThatCan()
    {
        const string account = "acct";
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.Gemini), ProviderRouter.Route(ProviderCapability.Claude, new(null, null, account, geminiKey: "g")));
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.OpenRouter), ProviderRouter.Route(ProviderCapability.Claude, new("o", null, null, openRouterKey: "r")));
        Assert.Equal(ProviderRoute.AnthropicDirect, ProviderRouter.Route(ProviderCapability.Claude, new(null, "a", null, openRouterKey: "r")));
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.CodexPlan), ProviderRouter.Route(ProviderCapability.Claude, new(null, null, account, codexPlanOn: true)));
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.ClaudeCodePlan), ProviderRouter.Route(ProviderCapability.Claude, new(null, null, null, claudeCodePlanOn: true, codexPlanOn: true)));
        Assert.Equal(ProviderRoute.Backend, ProviderRouter.Route(ProviderCapability.Claude, new(null, null, account)));
    }

    [Fact]
    public void APickedSourceWinsWhenItCanAnswer()
    {
        Assert.Equal(ProviderRoute.AnswerProvider(AnswerSource.Groq),
            ProviderRouter.Route(ProviderCapability.Claude, new(null, "a", null, groqKey: "q", preferredAnswerSource: AnswerSource.Groq)));
        Assert.Equal(ProviderRoute.Backend,
            ProviderRouter.Route(ProviderCapability.Claude, new(null, "a", "acct", preferredAnswerSource: AnswerSource.OpenClickyAccount)));
        Assert.Equal(ProviderRoute.AnthropicDirect,
            ProviderRouter.Route(ProviderCapability.Claude, new(null, "a", null, preferredAnswerSource: AnswerSource.Mistral)));
    }

    [Fact]
    public void OtherKeysOnlyAnswerQuestions()
    {
        var geminiOnly = new ProviderCredentials(null, null, null, geminiKey: "g");
        Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.NeedsOpenAIKeySentence), ProviderRouter.Route(ProviderCapability.Speech, geminiOnly));
        Assert.Equal(ProviderRoute.Unavailable(ProviderRouter.NeedsOpenAIKeySentence), ProviderRouter.Route(ProviderCapability.OpenAIChat, geminiOnly));
    }

    [Fact]
    public void EachAnsweringRouteNamesItsSource()
    {
        Assert.Equal("Claude", ProviderRoute.AnthropicDirect.AnsweringSource?.ShortName());
        Assert.Equal("Claude Code", ProviderRoute.AnswerProvider(AnswerSource.ClaudeCodePlan).AnsweringSource?.ShortName());
        Assert.Equal("free credits", ProviderRoute.Backend.AnsweringSource?.ShortName());
        Assert.Null(ProviderRoute.Unavailable("no").AnsweringSource);
        Assert.Null(ProviderRoute.OpenAIDirect.AnsweringSource);
    }

    [Fact]
    public void BlankValuesCountAsMissing()
    {
        Assert.True(Nothing.OpenAIKey is null && Nothing.AnthropicKey is null && Nothing.AccountToken is null);
        Assert.Equal("sk-user", new ProviderCredentials(" sk-user\n", null, null).OpenAIKey);
    }

    [Fact]
    public void TheSettingsBecomeCredentials()
    {
        var credentials = ProviderCredentials.FromSettings(new() { XaiApiKey = "x", UseCodexPlan = true, AnswerSource = "xai" });
        Assert.Equal("x", credentials.XaiKey);
        Assert.True(credentials.CodexPlanOn);
        Assert.Equal(AnswerSource.Xai, credentials.PreferredAnswerSource);
        Assert.Equal([AnswerSource.Xai, AnswerSource.CodexPlan], credentials.AnswerSourcesInOrder);
        Assert.Null(ProviderCredentials.FromSettings(new() { AnswerSource = "auto" }).PreferredAnswerSource);
    }

    // MARK: the direct requests

    private static JsonObject Json(HttpRequestMessage request) =>
        JsonNode.Parse(request.Content!.BodyText())!.AsObject();

    [Fact]
    public void ClaudeGoesToAnthropicWithTheKeyAndVersion()
    {
        var request = DirectProviderRequests.AnthropicMessages("ak-user");
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("ak-user", request.HeaderValue("x-api-key"));
        Assert.Equal("2023-06-01", request.HeaderValue("anthropic-version"));
        Assert.Equal("application/json", request.HeaderValue("Content-Type"));
        Assert.Null(request.HeaderValue("Authorization"));
    }

    [Fact]
    public void SpeechIsOpenAISpeechAsTheBackendAsksForIt()
    {
        var request = DirectProviderRequests.OpenAISpeech("sk-user", "hello");
        Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer sk-user", request.HeaderValue("Authorization"));
        Assert.Equal("audio/mpeg", request.HeaderValue("Accept"));
        var body = Json(request);
        Assert.Equal("gpt-4o-mini-tts", (string?)body["model"]);
        Assert.Equal("marin", (string?)body["voice"]);
        Assert.Equal("hello", (string?)body["input"]);
        Assert.Equal("mp3", (string?)body["response_format"]);
    }

    [Fact]
    public void TranscriptionIsTheMultipartUploadTheBackendBuilds()
    {
        var request = DirectProviderRequests.OpenAITranscription("sk-user", Encoding.UTF8.GetBytes("RIFFdata"), "en", "OpenClicky", "b");
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("multipart/form-data; boundary=b", request.HeaderValue("Content-Type"));
        var body = request.Content!.BodyText();
        Assert.Contains("name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\nRIFFdata\r\n", body);
        Assert.Contains("name=\"model\"\r\n\r\ngpt-4o-mini-transcribe\r\n", body);
        Assert.Contains("name=\"language\"\r\n\r\nen\r\n", body);
        Assert.Contains("name=\"prompt\"\r\n\r\nOpenClicky\r\n", body);
        Assert.EndsWith("--b--\r\n", body);
    }

    [Fact]
    public void RealtimeMintsTheClientSecretAtOpenAI()
    {
        var request = DirectProviderRequests.OpenAIRealtimeSecret("sk-user", "be brief", "marin");
        Assert.Equal("https://api.openai.com/v1/realtime/client_secrets", request.RequestUri!.AbsoluteUri);
        var body = Json(request);
        Assert.Equal(600, (int?)body["expires_after"]?["seconds"]);
        Assert.Equal("created_at", (string?)body["expires_after"]?["anchor"]);
        var session = body["session"]!;
        Assert.Equal("realtime", (string?)session["type"]);
        Assert.Equal("gpt-realtime", (string?)session["model"]);
        Assert.Equal("be brief", (string?)session["instructions"]);
        Assert.Equal("marin", (string?)session["audio"]?["output"]?["voice"]);
    }

    [Fact]
    public void PolishIsHaikuWithTheBackendsCeiling()
    {
        var body = Json(DirectProviderRequests.AnthropicPolish("ak-user", "tidy", "hi there"));
        Assert.Equal("claude-haiku-4-5", (string?)body["model"]);
        Assert.Equal(8 * 2 + 64, (int?)body["max_tokens"]);
        var system = body["system"]![0]!;
        Assert.Equal("tidy", (string?)system["text"]);
        Assert.Equal("ephemeral", (string?)system["cache_control"]?["type"]);
        Assert.Equal("hi there", (string?)body["messages"]![0]!["content"]);
    }

    [Fact]
    public void APolishCutOffAtMaxTokensIsNeverPasted()
    {
        var whole = Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":" Hi. "}],"stop_reason":"end_turn"}""");
        var cut = Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"Hi"}],"stop_reason":"max_tokens"}""");
        Assert.Equal("Hi.", DirectProviderRequests.AnthropicReplyText(whole));
        Assert.Null(DirectProviderRequests.AnthropicReplyText(cut));
        Assert.Null(DirectProviderRequests.AnthropicReplyText(Encoding.UTF8.GetBytes("not json")));
    }

    // MARK: a 401 on a PC with no account

    [Fact]
    public async Task A401ForARequestWithNoTokenSignsNobodyOut()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://backend.test/v1/messages");
        var refusal = Encoding.UTF8.GetBytes("""{"error":"missing Authorization: Bearer <token>"}""");
        var handling = new BackendSignIn.SignInRejectionHandling(
            _ => throw new InvalidOperationException("a request with no token has no sign-in to handle"),
            () => Task.FromResult<string?>(null),
            () => throw new InvalidOperationException("nobody may be signed out"));
        var answer = await BackendSignIn.SendAsync(request,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new ByteArrayContent(refusal) }),
            handling);
        Assert.Equal(401, (int)answer.StatusCode);
    }
}
