// Port of macos/OpenClicky/OpenClickyTests/AnswerProviderHTTPTests.swift.
//
// The two HTTP answer providers: the OpenAI-compatible chat request (images as data URLs, history,
// tools as functions, the OpenRouter headers, the token-limit field) and Gemini's (inlineData,
// user/model turns, functionDeclarations with the schema keywords Gemini rejects removed); the
// streams they read (text deltas, tool calls split over many chunks, mid-stream errors); and the
// vendor's own message on a refusal. Canned lines through an injected transport: no network, no
// files. One extra test runs the default transport through HttpClients.UseHandler.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Tests.Providers;

namespace OpenClicky.Core.Tests.Answers;

/// <summary>Tests that swap the shared HttpClient's handler run alone, after everything else.</summary>
[CollectionDefinition("HttpClients", DisableParallelization = true)]
public sealed class HttpClientsCollection;

/// <summary>Records the request a provider sent and answers with canned lines.</summary>
internal sealed class CannedTransport(int statusCode, params string[] bodyLines)
{
    private readonly object gate = new();
    private HttpRequestMessage? sentRequest;
    private string? sentBody;

    public HttpRequestMessage? SentRequest { get { lock (gate) return sentRequest; } }
    public string? SentBody { get { lock (gate) return sentBody; } }

    public AnswerHttpTransport Transport => AnswerHttpTransports.Canned(statusCode, bodyLines, request =>
    {
        lock (gate)
        {
            sentRequest = request;
            sentBody = request.Content?.BodyText();
        }
    });
}

/// <summary>Collects the text chunks a provider reports.</summary>
internal sealed class ChunkCollector
{
    private readonly object gate = new();
    private readonly List<string> collected = [];
    public IReadOnlyList<string> Chunks { get { lock (gate) return collected.ToList(); } }
    public void Append(string chunk) { lock (gate) collected.Add(chunk); }
}

public class AnswerProviderHttpTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    private const string PointAtToolSchema = """
        {"$schema":"http://json-schema.org/draft-07/schema#","type":"object","additionalProperties":false,
         "properties":{"target":{"type":"string","description":"what to point at"},
                       "examples":{"type":"object","additionalProperties":false,"properties":{"inner":{"type":"string"}}},
                       "steps":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"x":{"type":"integer"}}}}},
         "required":["target"]}
        """;

    private static AnswerRequest SampleRequest() => new(
        [new AnswerRequest.Image(PngBytes, "screen 1 of 1 (1512x982)", "image/png")],
        "You are Clicky.",
        [new AnswerRequest.Turn("earlier question", "earlier answer")],
        "what is this?",
        [new AnswerRequest.Tool("point_at", "Point at something", PointAtToolSchema)],
        maxTokens: 512);

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    private static JsonObject BodyOf(HttpRequestMessage request) => (JsonObject)JsonNode.Parse(request.Content!.BodyText())!;

    // MARK: OpenAI-compatible request

    [Fact]
    public void TheChatRequestCarriesSystemHistoryImagesAndPrompt()
    {
        using var request = OpenAIChatAnswerProvider.MakeHttpRequest(AnswerSource.Groq, "gsk-test", "llama", SampleRequest());
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer gsk-test", request.HeaderValue("Authorization"));
        Assert.Null(request.HeaderValue("HTTP-Referer"));
        Assert.Equal("application/json", request.HeaderValue("Content-Type"));

        var body = BodyOf(request);
        Assert.Equal("llama", Text(body["model"]));
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));

        var messages = body["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Equal("system", Text(messages[0]!["role"]));
        Assert.Equal("You are Clicky.", Text(messages[0]!["content"]));
        Assert.Equal("user", Text(messages[1]!["role"]));
        Assert.Equal("earlier question", Text(messages[1]!["content"]));
        Assert.Equal("assistant", Text(messages[2]!["role"]));
        Assert.Equal("earlier answer", Text(messages[2]!["content"]));

        var currentParts = messages[3]!["content"]!.AsArray();
        Assert.Equal(3, currentParts.Count);
        Assert.Equal("image_url", Text(currentParts[0]!["type"]));
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(PngBytes)}", Text(currentParts[0]!["image_url"]!["url"]));
        Assert.Equal("screen 1 of 1 (1512x982)", Text(currentParts[1]!["text"]));
        Assert.Equal("what is this?", Text(currentParts[2]!["text"]));
    }

    [Fact]
    public void ToolsBecomeFunctionsWithTheirSchemaUntouched()
    {
        var body = OpenAIChatAnswerProvider.MakeRequestBody(AnswerSource.OpenAI, "gpt-4.1", SampleRequest());
        var tools = body["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("function", Text(tools[0]!["type"]));
        var function = tools[0]!["function"]!;
        Assert.Equal("point_at", Text(function["name"]));
        Assert.Equal("Point at something", Text(function["description"]));
        var parameters = function["parameters"]!;
        Assert.False(parameters["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["target"], parameters["required"]!.AsArray().Select(Text));
    }

    [Fact]
    public void NoToolsMeansNoToolsField()
    {
        var withoutTools = SampleRequest();
        withoutTools.Tools = [];
        Assert.False(OpenAIChatAnswerProvider.MakeRequestBody(AnswerSource.Mistral, "m", withoutTools).ContainsKey("tools"));
        Assert.False(GeminiAnswerProvider.MakeRequestBody("gemini-2.5-flash", withoutTools).ContainsKey("tools"));
    }

    [Fact]
    public void OpenAIItselfGetsMaxCompletionTokens()
    {
        var body = OpenAIChatAnswerProvider.MakeRequestBody(AnswerSource.OpenAI, "gpt-5", SampleRequest());
        Assert.Equal(512, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        foreach (var compatibleSource in new[] { AnswerSource.OpenRouter, AnswerSource.Groq, AnswerSource.Mistral, AnswerSource.Xai })
        {
            Assert.Equal("max_tokens", OpenAIChatAnswerProvider.TokenLimitFieldName(compatibleSource));
        }
    }

    [Fact]
    public void OpenRouterIsToldWhoIsAsking()
    {
        using var request = OpenAIChatAnswerProvider.MakeHttpRequest(AnswerSource.OpenRouter, "or-test", "anthropic/claude-sonnet-4.6", SampleRequest());
        Assert.Equal("openrouter.ai", request.RequestUri!.Host);
        Assert.Equal("https://github.com/prasanthsasikumar/openclicky", request.HeaderValue("HTTP-Referer"));
        Assert.Equal("OpenClicky", request.HeaderValue("X-Title"));
        Assert.Equal("Bearer or-test", request.HeaderValue("Authorization"));
    }

    [Fact]
    public void ASourceWithoutAChatEndpointIsRefused()
    {
        Assert.Throws<AnswerProviderException>(() => OpenAIChatAnswerProvider.MakeHttpRequest(AnswerSource.Gemini, "k", "m", SampleRequest()));
    }

    // MARK: OpenAI-compatible stream

    [Fact]
    public async Task TextDeltasAreStreamedAndJoined()
    {
        var canned = new CannedTransport(200,
            ": OPENROUTER PROCESSING",
            """data: {"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""",
            "",
            """data: {"choices":[{"index":0,"delta":{"content":"Red"}}]}""",
            """data: {"choices":[{"index":0,"delta":{"content":", plainly."}}]}""",
            """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "data: [DONE]",
            """data: {"choices":[{"index":0,"delta":{"content":"after done"}}]}""");
        var provider = new OpenAIChatAnswerProvider(AnswerSource.OpenAI, "sk-test", "gpt-4.1", canned.Transport);
        var collector = new ChunkCollector();
        var result = await provider.AnswerAsync(SampleRequest(), collector.Append);
        Assert.Equal(["Red", ", plainly."], collector.Chunks);
        Assert.Equal("Red, plainly.", result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("Bearer sk-test", canned.SentRequest?.HeaderValue("Authorization"));
    }

    [Fact]
    public void ToolCallArgumentsSplitOverChunksAreReassembled()
    {
        var result = OpenAIChatAnswerProvider.ParseStream([
            """data: {"choices":[{"delta":{"content":"On it."}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"point_at","arguments":""}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tar"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"get\": \"Save"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_def","type":"function","function":{"name":"set_volume","arguments":"{\"level\":"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":" button\"}"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"40, \"loud\": true, \"nested\": {\"a\":1}}"}}]}}]}""",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "data: [DONE]",
        ]);
        Assert.Equal("On it.", result.Text);
        Assert.Equal([
            new ClaudeToolCall("call_abc", "point_at", new Dictionary<string, string> { ["target"] = "Save button" }),
            new ClaudeToolCall("call_def", "set_volume", new Dictionary<string, string> { ["level"] = "40", ["loud"] = "1" }),
        ], result.ToolCalls);
    }

    [Fact]
    public void AToolCallWithBrokenArgumentsIsDropped()
    {
        var result = OpenAIChatAnswerProvider.ParseStream([
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"point_at","arguments":"{\"target\": "}}]}}]}""",
            "data: [DONE]",
        ]);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task AnErrorInsideTheStreamIsThrownNotSwallowed()
    {
        var canned = new CannedTransport(200,
            """data: {"choices":[{"delta":{"content":"Half"}}]}""",
            """data: {"error":{"message":"Upstream provider overloaded","code":502}}""");
        var provider = new OpenAIChatAnswerProvider(AnswerSource.OpenRouter, "k", "m", canned.Transport);
        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(SampleRequest(), _ => { }));
        Assert.Equal(new AnswerProviderException(AnswerSource.OpenRouter, 200, "Upstream provider overloaded"), error);
    }

    [Fact]
    public async Task ARefusalCarriesTheVendorsMessage()
    {
        var canned = new CannedTransport(401,
            "{",
            """  "error": {"message": "Incorrect API key provided: sk-te**st.", "type": "invalid_request_error", "code": "invalid_api_key"}""",
            "}");
        var provider = new OpenAIChatAnswerProvider(AnswerSource.OpenAI, "sk-test", "gpt-4.1", canned.Transport);
        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(SampleRequest(), _ => { }));
        Assert.Equal(new AnswerProviderException(AnswerSource.OpenAI, 401, "Incorrect API key provided: sk-te**st."), error);
        Assert.Equal("OpenAI: Incorrect API key provided: sk-te**st.", error.Message);
    }

    [Fact]
    public void VendorErrorMessagesInTheirSeveralShapes()
    {
        Assert.Equal("nope", AnswerProviderHttpSupport.VendorErrorMessage("""{"error":{"message":"nope"}}"""));
        Assert.Equal("API key not valid.", AnswerProviderHttpSupport.VendorErrorMessage("""[{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}]"""));
        Assert.Equal("Unauthorized", AnswerProviderHttpSupport.VendorErrorMessage("""{"message":"Unauthorized","request_id":"x"}"""));
        Assert.Equal("Model not found", AnswerProviderHttpSupport.VendorErrorMessage("""{"detail":"Model not found"}"""));
        Assert.Equal("<html>Bad gateway</html>", AnswerProviderHttpSupport.VendorErrorMessage("<html>Bad gateway</html>"));
        Assert.Equal("the request was refused with no explanation", AnswerProviderHttpSupport.VendorErrorMessage("  "));
    }

    // MARK: Gemini request

    [Fact]
    public void TheGeminiRequestKeepsTheKeyInAHeader()
    {
        using var request = GeminiAnswerProvider.MakeHttpRequest("AIza-test", "gemini-2.5-flash", SampleRequest());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", request.RequestUri!.AbsoluteUri);
        Assert.Equal("AIza-test", request.HeaderValue("x-goog-api-key"));
        Assert.DoesNotContain("AIza", request.RequestUri.AbsoluteUri);
        Assert.Null(request.HeaderValue("Authorization"));
    }

    [Fact]
    public void TheGeminiBodyHasSystemTurnsInlineImagesAndPrompt()
    {
        var body = GeminiAnswerProvider.MakeRequestBody("gemini-2.5-flash", SampleRequest());
        Assert.Equal("You are Clicky.", Text(body["systemInstruction"]!["parts"]![0]!["text"]));
        Assert.Equal(512, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());

        var contents = body["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(content => Text(content!["role"])));
        Assert.Equal("earlier question", Text(contents[0]!["parts"]![0]!["text"]));
        Assert.Equal("earlier answer", Text(contents[1]!["parts"]![0]!["text"]));

        var currentParts = contents[2]!["parts"]!.AsArray();
        Assert.Equal(3, currentParts.Count);
        var inlineData = currentParts[0]!["inlineData"]!;
        Assert.Equal("image/png", Text(inlineData["mimeType"]));
        Assert.Equal(Convert.ToBase64String(PngBytes), Text(inlineData["data"]));
        Assert.Equal("screen 1 of 1 (1512x982)", Text(currentParts[1]!["text"]));
        Assert.Equal("what is this?", Text(currentParts[2]!["text"]));
    }

    [Fact]
    public void GeminiToolsAreFunctionDeclarationsWithoutRejectedKeywords()
    {
        var body = GeminiAnswerProvider.MakeRequestBody("gemini-2.5-flash", SampleRequest());
        var declarations = body["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.Single(declarations);
        Assert.Equal("point_at", Text(declarations[0]!["name"]));
        Assert.Equal("Point at something", Text(declarations[0]!["description"]));

        var parameters = declarations[0]!["parameters"]!.AsObject();
        Assert.False(parameters.ContainsKey("$schema"));
        Assert.False(parameters.ContainsKey("additionalProperties"));
        Assert.Equal("object", Text(parameters["type"]));
        Assert.Equal(["target"], parameters["required"]!.AsArray().Select(Text));

        var properties = parameters["properties"]!.AsObject();
        Assert.Equal("what to point at", Text(properties["target"]!["description"]));
        // An argument named like a rejected keyword is still an argument; its own schema is cleaned.
        var examplesArgument = properties["examples"]!.AsObject();
        Assert.False(examplesArgument.ContainsKey("additionalProperties"));
        Assert.NotNull(examplesArgument["properties"]!["inner"]);
        var stepItems = properties["steps"]!["items"]!.AsObject();
        Assert.False(stepItems.ContainsKey("additionalProperties"));
        Assert.NotNull(stepItems["properties"]!["x"]);
    }

    [Fact]
    public void GeminiThreeIsAskedToThinkLittleAndOlderModelsAreLeftAlone()
    {
        var geminiThreeConfig = GeminiAnswerProvider.MakeRequestBody("gemini-3.8-flash", SampleRequest())["generationConfig"]!.AsObject();
        Assert.Equal("low", Text(geminiThreeConfig["thinkingConfig"]!["thinkingLevel"]));
        Assert.Equal(512, geminiThreeConfig["maxOutputTokens"]!.GetValue<int>());
        var olderModelConfig = GeminiAnswerProvider.MakeRequestBody("gemini-2.5-flash", SampleRequest())["generationConfig"]!.AsObject();
        Assert.False(olderModelConfig.ContainsKey("thinkingConfig"));
    }

    // MARK: Gemini stream

    [Fact]
    public async Task GeminiTextIsStreamedAndThoughtsAreLeftOut()
    {
        var canned = new CannedTransport(200,
            """data: {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true}],"role":"model"}}]}""",
            "",
            """data: {"candidates":[{"content":{"parts":[{"text":"Red"}],"role":"model"}}]}""",
            "",
            """data: {"candidates":[{"content":{"parts":[{"text":"."}],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"totalTokenCount":9}}""");
        var provider = new GeminiAnswerProvider("AIza-test", "gemini-2.5-flash", canned.Transport);
        var collector = new ChunkCollector();
        var result = await provider.AnswerAsync(SampleRequest(), collector.Append);
        Assert.Equal(["Red", "."], collector.Chunks);
        Assert.Equal("Red.", result.Text);
        Assert.Empty(result.ToolCalls);
        Assert.Equal("AIza-test", canned.SentRequest?.HeaderValue("x-goog-api-key"));
    }

    [Fact]
    public void GeminiFunctionCallsBecomeToolCalls()
    {
        var result = GeminiAnswerProvider.ParseStream([
            """data: {"candidates":[{"content":{"parts":[{"text":"Sure. "},{"functionCall":{"name":"point_at","args":{"target":"Save","screen":2}}}],"role":"model"}}]}""",
            """data: {"candidates":[{"content":{"parts":[{"functionCall":{"name":"set_volume","args":{"level":40}}}],"role":"model"},"finishReason":"STOP"}]}""",
        ]);
        Assert.Equal("Sure. ", result.Text);
        Assert.Equal([
            new ClaudeToolCall("gemini-1", "point_at", new Dictionary<string, string> { ["target"] = "Save", ["screen"] = "2" }),
            new ClaudeToolCall("gemini-2", "set_volume", new Dictionary<string, string> { ["level"] = "40" }),
        ], result.ToolCalls);
    }

    [Fact]
    public void ABlockedGeminiQuestionSaysSo()
    {
        Assert.Throws<GeminiStreamAccumulator.StreamRefusalException>(() =>
            GeminiAnswerProvider.ParseStream(["""data: {"promptFeedback":{"blockReason":"SAFETY"}}"""]));
    }

    [Fact]
    public async Task AGeminiRefusalCarriesItsMessage()
    {
        var canned = new CannedTransport(400,
            """[{"error": {"code": 400, "message": "API key not valid. Please pass a valid API key.", "status": "INVALID_ARGUMENT"}}]""");
        var provider = new GeminiAnswerProvider("bad", "gemini-2.5-flash", canned.Transport);
        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(SampleRequest(), _ => { }));
        Assert.Equal(new AnswerProviderException(AnswerSource.Gemini, 400, "API key not valid. Please pass a valid API key."), error);
    }
}

/// <summary>The default transport, through the shared HttpClient (new on Windows).</summary>
[Collection("HttpClients")]
public class AnswerHttpTransportTests : IDisposable
{
    public void Dispose() => HttpClients.Reset();

    [Fact]
    public async Task TheSharedTransportStreamsTheBodyLineByLine()
    {
        HttpRequestMessage? seenRequest = null;
        string? seenBody = null;
        HttpClients.UseHandler(new DelegatingTestHandler((request, _) =>
        {
            seenRequest = request;
            seenBody = request.Content!.BodyText();
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\r\n\r\n"
                      + "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n"
                      + "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        }));
        var provider = new OpenAIChatAnswerProvider(AnswerSource.Xai, "xai-test", "grok-4");
        var collector = new ChunkCollector();
        var result = await provider.AnswerAsync(new AnswerRequest([], "", [], "hi", []), collector.Append);
        Assert.Equal("Hello", result.Text);
        Assert.Equal(["Hel", "lo"], collector.Chunks);
        Assert.Equal("https://api.x.ai/v1/chat/completions", seenRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("text/event-stream", seenRequest.HeaderValue("Accept"));
        Assert.Contains("\"model\":\"grok-4\"", seenBody);
    }

    [Fact]
    public async Task AQuietStreamTimesOut()
    {
        var lines = AnswerHttpTransports.ReadLinesAsync(new NeverEndingStream(), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(async () => { await foreach (var _ in lines) { } });
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
