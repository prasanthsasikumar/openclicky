// Ports AccountTakePolisherTests.swift, AnswerKeyTakePolisherTests.swift and the "errors name the
// model that refused" part of DictationPipelineTests.swift: what each polisher sends, how a refusal
// reads, and which polisher the keys pick.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Tests.Providers;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class AccountTakePolisherTests
{
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };

    private static readonly BackendSignIn.SignInRejectionHandling NoSession = new(
        _ => Task.FromResult(OpenClickyAuthSession.RejectionOutcome.SignedOut), () => Task.FromResult<string?>(null), () => Task.CompletedTask);

    private static AccountTakePolisher Polisher(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new()
    {
        Purpose = "polish",
        Send = send,
        SignIn = NoSession,
        Authorize = request => request.SetHeader("Authorization", "Bearer test-token"),
        BackendBaseUrl = "https://x",
    };

    [Fact]
    public async Task SendsPurposeSystemAndTextAndNoModel()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var polisher = Polisher(request =>
        {
            captured = request;
            capturedBody = request.Content!.BodyText();
            return Task.FromResult(Response(200, """{"text":"See you at seven."}"""));
        });
        var text = await polisher.PolishAsync("fix punctuation", "see you at seven");
        Assert.Equal("See you at seven.", text);
        var body = JsonNode.Parse(capturedBody!)!.AsObject();
        Assert.Equal("polish", body["purpose"]!.GetValue<string>());
        Assert.Equal("see you at seven", body["text"]!.GetValue<string>());
        Assert.Equal("fix punctuation", body["system"]!.GetValue<string>());
        Assert.False(body.ContainsKey("model"));
        Assert.EndsWith("/v1/polish", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Polish402FallsBackToLocalFormatting()
    {
        var polisher = Polisher(_ => Task.FromResult(Response(402, """{"error":"daily_limit"}""")));
        var error = await Assert.ThrowsAsync<AccountPolishException>(() => polisher.PolishAsync("s", "words"));
        Assert.Equal(AccountLimitError.DailyLimit, error.Limit);
    }

    [Fact]
    public void PolishErrorsReadAsPlainSentences()
    {
        Assert.Equal(AccountLimitError.DailyLimit.Message(), AccountPolishException.ForLimit(AccountLimitError.DailyLimit).Message);
        Assert.Equal("couldn't reach openclicky right now.", AccountPolishException.Unavailable(0).Message);
        Assert.Equal(AccountLimitErrors.ServiceTroubleMessage, AccountPolishException.Unavailable(503).Message);
        Assert.Equal("couldn't reach openclicky right now.", AccountPolishException.Unavailable(500).Message);
        Assert.Equal("couldn't reach openclicky right now.", AccountPolishException.ForLimit(AccountLimitError.TtsBudget).Message);
    }

    [Fact]
    public void ALimitIsSaidOnceASessionAndPassingTroubleNotAtAll()
    {
        var shown = new HashSet<string>();
        var limit = AccountPolishException.ForLimit(AccountLimitError.DailyLimit);
        Assert.Equal(AccountLimitError.DailyLimit.Message(), PolishFailureNotice.SentenceToShow(limit, shown));
        Assert.Null(PolishFailureNotice.SentenceToShow(limit, shown));
        Assert.Null(PolishFailureNotice.Sentence(new TimeoutException()));
        Assert.Null(PolishFailureNotice.Sentence(new BackendSignInException(BackendSignInError.Expired)));
        Assert.Equal(AccountLimitErrors.ServiceTroubleMessage, PolishFailureNotice.Sentence(AccountPolishException.Unavailable(503)));
        Assert.Equal(PolishFailureNotice.OwnKeyRefusedSentence, PolishFailureNotice.Sentence(BackendPolishException.Refused(403, "bad key")));
        Assert.Null(PolishFailureNotice.Sentence(BackendPolishException.Refused(500, "boom")));
    }
}

public sealed class AnswerKeyTakePolisherTests
{
    [Fact]
    public void AGeminiKeyAlonePolishes() =>
        Assert.Equal(AnswerSource.Gemini, AnswerKeyTakePolisher.FirstAvailable(new ProviderCredentials(null, null, null, geminiKey: "g"))?.Source);

    [Fact]
    public void GroqIsTriedFirstBecauseItIsQuickest()
    {
        var credentials = new ProviderCredentials(null, null, null, openRouterKey: "o", geminiKey: "g", groqKey: "q", mistralKey: "m", xaiKey: "x");
        Assert.Equal(AnswerSource.Groq, AnswerKeyTakePolisher.FirstAvailable(credentials)?.Source);
    }

    [Fact]
    public void EveryKeyedAnswerSourceButTheBigTwoCanPolish()
    {
        foreach (var source in new[] { AnswerSource.OpenRouter, AnswerSource.Gemini, AnswerSource.Groq, AnswerSource.Mistral, AnswerSource.Xai })
        {
            var credentials = source switch
            {
                AnswerSource.OpenRouter => new ProviderCredentials(null, null, null, openRouterKey: "k"),
                AnswerSource.Gemini => new ProviderCredentials(null, null, null, geminiKey: "k"),
                AnswerSource.Groq => new ProviderCredentials(null, null, null, groqKey: "k"),
                AnswerSource.Mistral => new ProviderCredentials(null, null, null, mistralKey: "k"),
                _ => new ProviderCredentials(null, null, null, xaiKey: "k"),
            };
            Assert.Equal(source, AnswerKeyTakePolisher.FirstAvailable(credentials)?.Source);
        }
    }

    [Fact]
    public void NoKeysNoPolisher() => Assert.Null(AnswerKeyTakePolisher.FirstAvailable(new ProviderCredentials(null, null, null)));

    [Fact]
    public void SlowAnswerModelsGetAQuickOneForTidying()
    {
        Assert.Equal("openai/gpt-4.1-mini", AnswerKeyTakePolisher.PolishModel(AnswerSource.OpenRouter));
        Assert.Equal("mistral-small-latest", AnswerKeyTakePolisher.PolishModel(AnswerSource.Mistral));
        Assert.Null(AnswerKeyTakePolisher.PolishModel(AnswerSource.Groq));
    }

    private sealed class RecordingCompletion(Func<Task<string>> answer) : IAnswerKeyCompletion
    {
        public (AnswerSource Source, string? Model, string System, string User, int MaxTokens)? Asked { get; private set; }

        public Task<string> CompleteAsync(AnswerSource source, ProviderCredentials credentials, string? modelOverride, string system, string user,
                                          int maxTokens, CancellationToken cancellationToken)
        {
            Asked = (source, modelOverride, system, user, maxTokens);
            return answer();
        }
    }

    [Fact]
    public async Task ThePolishAsksTheQuickModelAndTrimsTheAnswer()
    {
        var completion = new RecordingCompletion(() => Task.FromResult("  Tidy words.\n"));
        var polisher = new AnswerKeyTakePolisher(AnswerSource.OpenRouter, new ProviderCredentials(null, null, null, openRouterKey: "o")) { Completion = completion };
        Assert.Equal("Tidy words.", await polisher.PolishAsync("s", "tidy words"));
        Assert.Equal((AnswerSource.OpenRouter, "openai/gpt-4.1-mini", "s", "tidy words", 1024), completion.Asked);
        Assert.Equal("OpenRouter (your key)", polisher.DisplayName);
    }

    [Fact]
    public async Task ARefusalNamesTheVendor()
    {
        var polisher = new AnswerKeyTakePolisher(AnswerSource.Groq, new ProviderCredentials(null, null, null, groqKey: "q"))
        {
            Completion = new RecordingCompletion(() => throw new AnswerKeyCompletionException(401, "invalid api key")),
        };
        var error = await Assert.ThrowsAsync<TakePolishException>(() => polisher.PolishAsync("s", "u"));
        Assert.Equal((TakePolishErrorKind.Refused, "groq", 401), (error.Kind, error.Vendor, error.Status));
        Assert.Equal(PolishFailureNotice.OwnKeyRefusedSentence, PolishFailureNotice.Sentence(error));

        var empty = new AnswerKeyTakePolisher(AnswerSource.Groq, new ProviderCredentials(null, null, null, groqKey: "q"))
        {
            Completion = new RecordingCompletion(() => Task.FromResult("  ")),
        };
        Assert.Equal(TakePolishErrorKind.EmptyAnswer, (await Assert.ThrowsAsync<TakePolishException>(() => empty.PolishAsync("s", "u"))).Kind);
    }

    [Fact]
    public void GeminiAnswersAreRead() =>
        Assert.Equal("Hello.", DirectAnswerKeyCompletion.GeminiText(Encoding.UTF8.GetBytes("""{"candidates":[{"content":{"parts":[{"text":"Hel"},{"text":"lo."}]}}]}""")));
}

public sealed class PolishErrorTests
{
    [Fact]
    public async Task AnOwnAnthropicKeyRefusalIsAnthropicsNotOpenClickys()
    {
        var polisher = new AnthropicTakePolisher("ak-user")
        {
            Send = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"invalid x-api-key"}}"""),
                RequestMessage = request,
            }),
        };
        var error = await Assert.ThrowsAsync<TakePolishException>(() => polisher.PolishAsync("tidy", "hello"));
        Assert.Equal(TakePolishErrorKind.Refused, error.Kind);
        Assert.Equal("anthropic", error.Vendor);
        Assert.Equal(401, error.Status);
        Assert.StartsWith("anthropic refused", error.Message);
        Assert.Equal(PolishFailureNotice.OwnKeyRefusedSentence, PolishFailureNotice.Sentence(error));
    }

    [Fact]
    public async Task TheAnthropicPolishIsHaikuWithTheKeyInItsHeader()
    {
        HttpRequestMessage? sent = null;
        var polisher = new AnthropicTakePolisher("ak-user")
        {
            Send = (request, _) =>
            {
                sent = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"content":[{"type":"text","text":"Hello."}],"stop_reason":"end_turn"}"""),
                });
            },
        };
        Assert.Equal("Hello.", await polisher.PolishAsync("tidy", "hello"));
        Assert.Equal("ak-user", sent!.HeaderValue("x-api-key"));
        Assert.Equal("Claude (your key)", polisher.DisplayName);
    }

    [Fact]
    public async Task AnEmptyHeyClickyEditNamesTheModelThatMadeIt()
    {
        var context = new TakeFormattingContext { Style = DictationSpace.Empty.Style(null) };
        var polisher = new TakeFormatterTests.FixedPolisher("   ", "Groq (your key)");
        var error = await Assert.ThrowsAsync<TakePolishException>(() => HeyClickyEditor.EditAsync("hello", "shorter", context, polisher));
        Assert.Equal((TakePolishErrorKind.EmptyAnswer, "Groq (your key)"), (error.Kind, error.Vendor));
        Assert.DoesNotContain("sarvam", error.Message);
    }

    [Fact]
    public async Task AHeyClickyEditIsUnwrappedAndCarriesTheStyle()
    {
        var context = new TakeFormattingContext { Style = DictationSpace.Empty.Style("slack.exe") };
        Assert.Equal("Shorter.", await HeyClickyEditor.EditAsync("a longer text", "shorter", context, new TakeFormatterTests.FixedPolisher("\"Shorter.\"")));
        Assert.Contains("Style for this app (work messaging): Clear and quick", HeyClickyEditor.SystemPrompt(context));
        Assert.Equal("Instruction: shorter\n\nText:\na longer text", HeyClickyEditor.UserPrompt("a longer text", "shorter"));
    }

    [Fact]
    public void RefusalsReadAsSentences()
    {
        Assert.Equal("anthropic refused the request (401): bad", TakePolishException.Refused("anthropic", 401, " bad ").Message);
        Assert.Equal("openai refused the request.", TakePolishException.Refused("openai", 0, "").Message);
        Assert.Equal("openai sent back no text.", TakePolishException.EmptyAnswer("openai").Message);
        Assert.Equal("openclicky refused the clean-up (500): boom", BackendPolishException.Refused(500, "boom").Message);
    }
}
