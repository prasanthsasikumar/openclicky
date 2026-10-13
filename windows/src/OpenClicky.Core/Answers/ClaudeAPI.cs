// Port of macos/OpenClicky/OpenClicky/ClaudeAPI.swift
//
// Claude API helper with streaming for progressive text display. The person's own Anthropic key
// goes straight to Anthropic with the same Messages body /chat relays; without one, the backend's
// /chat on the account's token (ProviderRouter). Another answer source (an OpenAI-compatible key,
// Gemini, a Claude Code or Codex plan) gets the same question through its IAnswerProvider.
//
// Windows notes: the Mac's own URLSession (default config, no cache, TLS tickets kept) is the
// shared HttpClient (HttpClients.Shared); its 120 s request timeout is the per-line idle timeout of
// the answer stream, and its TLS warm-up is a HEAD to the host's root through the same client, so
// the pooled connection is already open when the first big screenshot goes out.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

/// <summary>
/// A failure inside ClaudeAPI that is not an HTTP refusal (Swift's NSError domain "ClaudeAPI"):
/// `Code` is -1 for a response that could not be read, `ClaudeAPI.MidStreamErrorCode` for an
/// `error` event inside a stream that had started.
/// </summary>
public sealed class ClaudeApiException(int code, string message) : Exception(message)
{
    public const string Domain = "ClaudeAPI";
    public int Code { get; } = code;
}

/// <summary>Builds the provider for a source the router picked (AnswerProviderFactory.MakeAsync by default).</summary>
public delegate Task<IAnswerProvider> AnswerProviderMaker(AnswerSource source, ProviderCredentials credentials, CancellationToken cancellationToken);

public sealed class ClaudeAPI
{
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary>The error code for an `error` event inside a stream that had started.</summary>
    public const int MidStreamErrorCode = -1002;

    private static readonly object TlsWarmupLock = new();
    private static bool hasStartedTlsWarmup;

    /// <summary>
    /// The /chat URL, read for every request so a backend changed in shell.json while the app runs
    /// is used by the next question (it was fixed when the client was built).
    /// </summary>
    private readonly Func<string> currentProxyUrl;
    private readonly Func<ProviderCredentials>? credentialsOverride;
    private readonly BackendSignIn.SignInRejectionHandling? signIn;
    private readonly AnswerProviderMaker makeAnswerProvider;

    public Uri ApiUrl => ResolvedApiUrl(currentProxyUrl());
    public string Model { get; set; }

    /// <summary>
    /// `proxyUrl` is built from the user-configurable backend URL (shell.json or an environment
    /// override), not a compile-time literal, so a malformed value falls back to the hosted
    /// backend's own /chat endpoint instead of failing every question.
    /// </summary>
    public static Uri ResolvedApiUrl(string proxyUrl) =>
        Uri.TryCreate(proxyUrl, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : new Uri($"{OpenClickyConfiguration.HostedBackendUrl}/chat");

    public ClaudeAPI(string proxyUrl, string model = DefaultModel) : this(() => proxyUrl, model) { }

    /// <param name="currentProxyUrl">The backend's /chat URL, read for every request.</param>
    /// <param name="currentCredentials">The keys and token; null reads shell.json each time (and logs route changes).</param>
    /// <param name="signIn">How a refused sign-in is renewed; null is the live session.</param>
    /// <param name="makeAnswerProvider">The provider for another answer source; null is AnswerProviderFactory with shell.json's `answerModels`.</param>
    /// <param name="warmUpConnection">Open the connection to the next question's host now (once per app run).</param>
    public ClaudeAPI(Func<string> currentProxyUrl, string model = DefaultModel,
                     Func<ProviderCredentials>? currentCredentials = null,
                     BackendSignIn.SignInRejectionHandling? signIn = null,
                     AnswerProviderMaker? makeAnswerProvider = null,
                     bool warmUpConnection = true)
    {
        this.currentProxyUrl = currentProxyUrl;
        Model = model;
        credentialsOverride = currentCredentials;
        this.signIn = signIn;
        this.makeAnswerProvider = makeAnswerProvider ?? ((source, credentials, cancellationToken) =>
            AnswerProviderFactory.MakeAsync(source, credentials, OpenClickyConfiguration.Settings.AnswerModels ?? [], cancellationToken: cancellationToken));
        // Fire a lightweight HEAD request in the background to pre-establish the TLS connection,
        // so the first real API call (which carries a large image payload) doesn't need a cold
        // TLS handshake.
        if (warmUpConnection) WarmUpTlsConnectionIfNeeded();
    }

    private ProviderCredentials Credentials() => credentialsOverride?.Invoke() ?? ProviderRouter.CurrentCredentials();

    private ProviderRoute CurrentRoute() =>
        credentialsOverride is null ? ProviderRouter.Current(ProviderCapability.Claude) : ProviderRouter.Route(ProviderCapability.Claude, credentialsOverride());

    /// <summary>
    /// The person's own Anthropic key goes straight to Anthropic with the same Messages body /chat
    /// relays; without one, the backend's /chat on the account's token (`ProviderRouter`).
    /// `GoesToBackend` says the request carries the account's token, so a 401 is the sign-in's
    /// and goes through `BackendSignIn` (refresh once, send again).
    /// </summary>
    private (HttpRequestMessage Request, bool GoesToBackend) MakeApiRequest(ProviderRoute route, ProviderCredentials credentials, JsonObject body)
    {
        switch (route.Kind)
        {
            case ProviderRouteKind.AnthropicDirect:
                if (credentials.AnthropicKey is not { } anthropicKey)
                {
                    throw new ProviderRouteUnavailableException(ProviderCapability.Claude, ProviderRouter.NeedsAnthropicKeySentence);
                }
                return (DirectProviderRequests.AnthropicMessages(anthropicKey, body), false);
            case ProviderRouteKind.Unavailable:
                throw new ProviderRouteUnavailableException(ProviderCapability.Claude, route.Sentence ?? ProviderRouter.NeedsAnthropicKeySentence);
            case ProviderRouteKind.AnswerProvider:
                // AnalyzeImageStreamingAsync hands these to an IAnswerProvider before building a request.
                throw new ProviderRouteUnavailableException(ProviderCapability.Claude, $"{route.Source!.Value.DisplayName()} answers through its own provider");
            default:
                var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = DirectProviderRequests.JsonContent(body) };
                OpenClickyConfiguration.Authorize(request, credentials.AccountToken);
                return (request, true);
        }
    }

    /// <summary>
    /// The start of a streamed answer: the response of a 2xx answer (its stream unread), or a
    /// refusal whose body has been read to the end (BackendSignIn needs a 401's body to tell the
    /// sign-in's from an own key's).
    /// </summary>
    private sealed record StreamingAnswerStart(HttpResponseMessage? StreamingResponse, int Status, byte[] RefusalBody)
    {
        public (int Status, byte[] Body) StatusAndBody => (Status, RefusalBody);
    }

    private static async Task<StreamingAnswerStart> StartStreamingAnswerAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await HttpClients.Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is >= 200 and <= 299) return new StreamingAnswerStart(response, status, []);
        // A non-2xx status: read the full body as error text.
        using (response)
        {
            var errorBodyLines = new List<string>();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var line in AnswerHttpTransports.ReadLinesAsync(stream, AnswerHttpTransports.IdleTimeout, cancellationToken).ConfigureAwait(false))
            {
                errorBodyLines.Add(line);
            }
            return new StreamingAnswerStart(null, status, Encoding.UTF8.GetBytes(string.Join("\n", errorBodyLines)));
        }
    }

    /// <summary>
    /// Detects the MIME type of image data by inspecting the first bytes. Screen captures are JPEG,
    /// but pasted images from the clipboard are PNG. The API rejects requests where the declared
    /// media_type doesn't match the actual image format.
    /// </summary>
    public static string DetectImageMediaType(byte[] imageData)
    {
        // PNG files start with the 8-byte signature: 89 50 4E 47 0D 0A 1A 0A
        if (imageData.Length >= 4 && imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
        {
            return "image/png";
        }
        // Default to JPEG — screen captures use JPEG compression
        return "image/jpeg";
    }

    /// <summary>
    /// Sends a no-op HEAD request to the host the next question goes to, to open (and pool) the TLS
    /// connection. Failures are silently ignored — this is purely an optimization.
    /// </summary>
    private void WarmUpTlsConnectionIfNeeded()
    {
        lock (TlsWarmupLock)
        {
            if (hasStartedTlsWarmup) return;
            hasStartedTlsWarmup = true;
        }
        Uri warmupTarget;
        try
        {
            // Warm the host the next question will go to: Anthropic on an own key, else the backend.
            warmupTarget = Credentials().AnthropicKey is not null ? new Uri(DirectProviderRequests.AnthropicMessagesUrl) : ApiUrl;
        }
        catch (Exception)
        {
            return;
        }
        // The TLS session is host-scoped, so warming the root host is enough. Hitting the host
        // instead of `/v1/messages` avoids extra endpoint-specific noise.
        var warmupUrl = new UriBuilder(warmupTarget) { Path = "/", Query = "", Fragment = "" }.Uri;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var warmupRequest = new HttpRequestMessage(HttpMethod.Head, warmupUrl);
                using var _ = await HttpClients.Shared.SendAsync(warmupRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Response doesn't matter — the handshake is the goal.
            }
        });
    }

    /// <summary>For tests: lets the next ClaudeAPI warm the connection again.</summary>
    internal static void ResetTlsWarmup()
    {
        lock (TlsWarmupLock) hasStartedTlsWarmup = false;
    }

    private static JsonArray MessagesBody(IReadOnlyList<(byte[] Data, string Label)> images,
                                          IReadOnlyList<(string UserPlaceholder, string AssistantResponse)> conversationHistory,
                                          string userPrompt)
    {
        var messages = new JsonArray();
        foreach (var (userPlaceholder, assistantResponse) in conversationHistory)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = userPlaceholder });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantResponse });
        }

        // Build current message with all labeled images + prompt
        var contentBlocks = new JsonArray();
        foreach (var image in images)
        {
            contentBlocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = DetectImageMediaType(image.Data),
                    ["data"] = Convert.ToBase64String(image.Data),
                },
            });
            contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = image.Label });
        }
        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = contentBlocks });
        return messages;
    }

    private static string PayloadMegabytes(JsonObject body) =>
        (Encoding.UTF8.GetByteCount(body.ToJsonString()) / 1_048_576.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Send a vision request to Claude with streaming. Calls `onTextChunk` each time new text
    /// arrives, with the whole answer so far, so the UI updates progressively (on the caller's
    /// thread or a background one: the Windows side marshals to the dispatcher). Returns the full
    /// accumulated text, the tool calls and the total duration when the stream completes.
    /// </summary>
    public async Task<(string Text, IReadOnlyList<ClaudeToolCall> ToolCalls, TimeSpan Duration)> AnalyzeImageStreamingAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? conversationHistory,
        string userPrompt,
        IReadOnlyList<JsonObject>? tools,
        Action<string> onTextChunk,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        conversationHistory ??= [];
        tools ??= [];

        // Another source answers (an OpenAI-compatible key, Gemini, a Claude Code or Codex plan):
        // the same question goes to its AnswerProvider, and its text streams back the same way.
        var route = CurrentRoute();
        if (route.Kind == ProviderRouteKind.AnswerProvider)
        {
            var result = await AnswerThroughProviderAsync(route.Source!.Value, images, systemPrompt, conversationHistory,
                                                          userPrompt, tools, 1024, onTextChunk, cancellationToken).ConfigureAwait(false);
            return (result.Text, result.ToolCalls, stopwatch.Elapsed);
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["stream"] = true,
            ["system"] = systemPrompt,
            ["messages"] = MessagesBody(images, conversationHistory, userPrompt),
        };
        if (tools.Count > 0) body["tools"] = new JsonArray(tools.Select(tool => (JsonNode)tool.DeepClone()).ToArray());

        var (request, goesToBackend) = MakeApiRequest(route, Credentials(), body);
        using var _ = request;
        Debug.WriteLine($"🌐 Claude streaming request: {PayloadMegabytes(body)}MB, {images.Count} image(s)");

        // On the account's token a refused sign-in is renewed once and the question sent again,
        // like every other backend call.
        var streamingAnswerStart = goesToBackend
            ? await BackendSignIn.SendAsync(request,
                                            sent => StartStreamingAnswerAsync(sent, cancellationToken),
                                            start => Task.FromResult(start.StatusAndBody),
                                            signIn).ConfigureAwait(false)
            : await StartStreamingAnswerAsync(request, cancellationToken).ConfigureAwait(false);
        if (streamingAnswerStart.StreamingResponse is not { } response)
        {
            throw new BackendHttpException(BackendHttpException.ServiceKind.Answers, streamingAnswerStart.Status, streamingAnswerStart.RefusalBody);
        }

        using (response)
        {
            // Parse SSE stream — each event is "data: {json}\n\n"
            var accumulatedResponseText = new StringBuilder();
            var sseLines = new List<string>();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var line in AnswerHttpTransports.ReadLinesAsync(stream, AnswerHttpTransports.IdleTimeout, cancellationToken).ConfigureAwait(false))
            {
                sseLines.Add(line);
                // SSE lines look like: "data: {...}"
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var jsonString = line[6..];

                // End of stream marker
                if (jsonString == "[DONE]") break;

                if (JsonHelpers.TryParse(jsonString) is not JsonObject eventPayload || JsonHelpers.String(eventPayload["type"]) is not { } eventType) continue;

                // Anthropic reports a failure after the 200 (overloaded, a dropped upstream) as an
                // `error` event; ignored, it ended the answer in silence as if it were complete.
                if (StreamFailure(eventPayload) is { } streamFailure)
                {
                    AppLog.Write($"claude: stream error after {accumulatedResponseText.Length} chars: {streamFailure.Message}");
                    throw streamFailure;
                }
                // A cut-off answer is still spoken as it is; the log says why it ended early.
                if (StopReason(eventPayload) == "max_tokens")
                {
                    AppLog.Write($"claude: answer cut off at max_tokens ({accumulatedResponseText.Length} chars)");
                }

                // We care about content_block_delta events that contain text chunks
                if (eventType == "content_block_delta"
                    && eventPayload["delta"] is JsonObject delta
                    && JsonHelpers.String(delta["type"]) == "text_delta"
                    && JsonHelpers.String(delta["text"]) is { } textChunk)
                {
                    accumulatedResponseText.Append(textChunk);
                    // Send the accumulated text so far to the UI for progressive rendering
                    onTextChunk(accumulatedResponseText.ToString());
                }
            }

            return (accumulatedResponseText.ToString(), ParseToolCalls(sseLines), stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// The same question asked of another source's IAnswerProvider (an OpenAI-compatible key,
    /// Gemini, a Claude Code or Codex plan). Each piece is handed on as the whole answer so far,
    /// in order, like the Anthropic path. Cancelling the question cancels the provider (a CLI
    /// process is ended).
    /// </summary>
    private async Task<AnswerResult> AnswerThroughProviderAsync(
        AnswerSource source,
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)> conversationHistory,
        string userPrompt,
        IReadOnlyList<JsonObject> tools,
        int maxTokens,
        Action<string> onTextChunk,
        CancellationToken cancellationToken)
    {
        var provider = await makeAnswerProvider(source, Credentials(), cancellationToken).ConfigureAwait(false);
        var answerRequest = new AnswerRequest(
            images.Select(image => new AnswerRequest.Image(image.Data, image.Label, DetectImageMediaType(image.Data))),
            systemPrompt,
            conversationHistory.Select(turn => new AnswerRequest.Turn(turn.UserPlaceholder, turn.AssistantResponse)),
            userPrompt,
            tools.Select(AnswerTool).OfType<AnswerRequest.Tool>(),
            maxTokens);
        var answerSoFar = new StringBuilder();
        var piecesLock = new object();
        var result = await provider.AnswerAsync(answerRequest, piece =>
        {
            lock (piecesLock)
            {
                answerSoFar.Append(piece);
                onTextChunk(answerSoFar.ToString());
            }
        }, cancellationToken).ConfigureAwait(false);
        AppLog.Write($"answer: {source.DisplayName()} answered ({result.Text.Length} chars, {result.ToolCalls.Count} tool calls)");
        return result;
    }

    /// <summary>
    /// Non-streaming request for short answers nobody watches arrive (screen grounding). Another
    /// answer source gets the question through its IAnswerProvider, as the streaming call does.
    /// </summary>
    public async Task<(string Text, TimeSpan Duration)> AnalyzeImageAsync(
        IReadOnlyList<(byte[] Data, string Label)> images,
        string systemPrompt,
        IReadOnlyList<(string UserPlaceholder, string AssistantResponse)>? conversationHistory,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        conversationHistory ??= [];

        var route = CurrentRoute();
        if (route.Kind == ProviderRouteKind.AnswerProvider)
        {
            var result = await AnswerThroughProviderAsync(route.Source!.Value, images, systemPrompt, conversationHistory, userPrompt, [],
                                                          // Not the 256 Anthropic gets below: a reasoning model (a Gemini 3, an OpenAI
                                                          // reasoning model) spends part of its limit thinking before the short answer.
                                                          1024, _ => { }, cancellationToken).ConfigureAwait(false);
            return (result.Text, stopwatch.Elapsed);
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 256,
            ["system"] = systemPrompt,
            ["messages"] = MessagesBody(images, conversationHistory, userPrompt),
        };
        var (request, goesToBackend) = MakeApiRequest(route, Credentials(), body);
        using var _ = request;
        Debug.WriteLine($"🌐 Claude request: {PayloadMegabytes(body)}MB, {images.Count} image(s)");

        using var response = goesToBackend
            ? await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, cancellationToken), signIn).ConfigureAwait(false)
            : await HttpClients.Shared.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or > 299)
        {
            throw new BackendHttpException(BackendHttpException.ServiceKind.Answers, status, data);
        }

        var json = JsonHelpers.TryParse(data) as JsonObject;
        var textBlock = (json?["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(block => JsonHelpers.String(block["type"]) == "text");
        if (JsonHelpers.String(textBlock?["text"]) is not { } text)
        {
            throw new ClaudeApiException(-1, "Invalid response format");
        }
        return (text, stopwatch.Elapsed);
    }

    // MARK: - Tools and stream events

    /// <summary>An Anthropic tool definition (name, description, input_schema) as an AnswerRequest tool.</summary>
    public static AnswerRequest.Tool? AnswerTool(JsonObject tool)
    {
        if (JsonHelpers.String(tool["name"]) is not { } name) return null;
        var schema = tool["input_schema"] as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        return new AnswerRequest.Tool(name, JsonHelpers.String(tool["description"]) ?? "", schema.ToJsonString());
    }

    /// <summary>
    /// The failure an Anthropic `error` event reports ({"type":"error","error":{"type":…,"message":…}}),
    /// as a ClaudeApiException with `MidStreamErrorCode`; null for every other event.
    /// </summary>
    public static ClaudeApiException? StreamFailure(JsonObject eventPayload)
    {
        if (JsonHelpers.String(eventPayload["type"]) != "error") return null;
        var streamError = eventPayload["error"] as JsonObject;
        var errorType = JsonHelpers.String(streamError?["type"]) ?? "unknown";
        var errorMessage = JsonHelpers.String(streamError?["message"]) ?? "";
        return new ClaudeApiException(MidStreamErrorCode, $"Stream error ({errorType}): {errorMessage}");
    }

    /// <summary>The `stop_reason` a `message_delta` event carries ("end_turn", "max_tokens", "tool_use"…).</summary>
    public static string? StopReason(JsonObject eventPayload)
    {
        if (JsonHelpers.String(eventPayload["type"]) != "message_delta" || eventPayload["delta"] is not JsonObject delta) return null;
        return JsonHelpers.String(delta["stop_reason"]);
    }

    private sealed class OpenToolBlock(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Json { get; } = new();
    }

    /// <summary>
    /// Tool calls from an Anthropic SSE stream: `content_block_start` names the tool, `input_json_delta`
    /// pieces build its input, `content_block_stop` closes it. Input that is not valid JSON is dropped.
    /// </summary>
    public static IReadOnlyList<ClaudeToolCall> ParseToolCalls(IEnumerable<string> lines)
    {
        var open = new Dictionary<int, OpenToolBlock>();
        var calls = new List<ClaudeToolCall>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (JsonHelpers.TryParse(line[6..]) is not JsonObject streamEvent
                || JsonHelpers.String(streamEvent["type"]) is not { } type
                || JsonHelpers.Int(streamEvent["index"]) is not { } index) continue;
            switch (type)
            {
                case "content_block_start":
                    if (streamEvent["content_block"] is JsonObject block && JsonHelpers.String(block["type"]) == "tool_use")
                    {
                        open[index] = new OpenToolBlock(JsonHelpers.String(block["id"]) ?? "", JsonHelpers.String(block["name"]) ?? "");
                    }
                    break;
                case "content_block_delta":
                    if (streamEvent["delta"] is JsonObject delta && JsonHelpers.String(delta["type"]) == "input_json_delta"
                        && JsonHelpers.String(delta["partial_json"]) is { } piece && open.TryGetValue(index, out var openBlock))
                    {
                        openBlock.Json.Append(piece);
                    }
                    break;
                case "content_block_stop":
                    if (!open.Remove(index, out var tool)) continue;
                    var raw = tool.Json.Length == 0 ? "{}" : tool.Json.ToString();
                    if (JsonHelpers.TryParse(raw) is not JsonObject argumentsObject) continue;
                    calls.Add(new ClaudeToolCall(tool.Id, tool.Name, AnswerProviderHttpSupport.ToolArguments(argumentsObject)));
                    break;
            }
        }
        return calls;
    }
}
