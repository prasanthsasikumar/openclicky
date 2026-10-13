// Port of macos/OpenClicky/OpenClicky/OpenAIChatAnswerProvider.swift
//
// Answers through an OpenAI-compatible Chat Completions endpoint: OpenAI, OpenRouter, Groq,
// Mistral and xAI all accept the same streamed request, so one provider serves the five of them
// (`AnswerSources.ChatCompletionsUrl` says where). The request is built and the stream is read by
// pure static functions, so both are unit-tested without the network (AnswerProviderHttpTests).
// The key goes straight to the vendor in an `Authorization: Bearer` header and is never logged.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

// MARK: - The HTTP seam both HTTP providers share

/// <summary>
/// A response as the providers read it: the status, then the body line by line (SSE is one event
/// per `data:` line, and an error body is read whole from the same lines).
/// </summary>
public sealed record AnswerHttpResponseLines(int StatusCode, IAsyncEnumerable<string> BodyLines);

/// <summary>Sends one request. Injected so tests feed canned SSE lines instead of the network.</summary>
public delegate Task<AnswerHttpResponseLines> AnswerHttpTransport(HttpRequestMessage request, CancellationToken cancellationToken);

public static class AnswerHttpTransports
{
    /// <summary>The Mac's `timeoutIntervalForRequest`: a stream that goes quiet this long has failed.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The real network, through the one shared HttpClient. The body is relayed line by line as it
    /// arrives so text can be shown and spoken while the answer is still being written; a consumer
    /// that stops early (`[DONE]`) disposes the response, which ends the download.
    /// </summary>
    public static AnswerHttpTransport Shared { get; } = async (request, cancellationToken) =>
    {
        var response = await HttpClients.Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return new AnswerHttpResponseLines((int)response.StatusCode, ResponseLines(response, IdleTimeout, cancellationToken));
    };

    private static async IAsyncEnumerable<string> ResponseLines(HttpResponseMessage response, TimeSpan idleTimeout,
                                                                [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var line in ReadLinesAsync(stream, idleTimeout, cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// The lines of `stream` as they arrive (\n, \r\n or \r end a line). A read that waits longer
    /// than `idleTimeout` throws TimeoutException; the caller's cancellation throws
    /// OperationCanceledException.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, TimeSpan idleTimeout,
                                                                 [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            string? line;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readTimeout.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"the answer stream went quiet for {(int)idleTimeout.TotalSeconds} s");
                }
            }
            if (line is null) yield break;
            yield return line;
        }
    }

    /// <summary>A transport that answers with canned lines (tests, and the smoke flags).</summary>
    public static AnswerHttpTransport Canned(int statusCode, IEnumerable<string> lines, Action<HttpRequestMessage>? record = null) =>
        (request, _) =>
        {
            record?.Invoke(request);
            return Task.FromResult(new AnswerHttpResponseLines(statusCode, lines.ToAsyncLines()));
        };

    private static async IAsyncEnumerable<string> ToAsyncLines(this IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }
}

/// <summary>Helpers the OpenAI-compatible and Gemini providers both need.</summary>
public static class AnswerProviderHttpSupport
{
    /// <summary>The whole body of a refused request, read so the vendor's own message can be shown.</summary>
    public static async Task<string> ReadWholeBodyAsync(IAsyncEnumerable<string> bodyLines, CancellationToken cancellationToken = default)
    {
        var collectedLines = new List<string>();
        try
        {
            await foreach (var line in bodyLines.WithCancellation(cancellationToken).ConfigureAwait(false)) collectedLines.Add(line);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A body that breaks off still says what it said up to there.
        }
        return string.Join("\n", collectedLines);
    }

    /// <summary>
    /// The vendor's own words from an error body. OpenAI, Groq, xAI and OpenRouter send
    /// `{"error":{"message":…}}`; Gemini sends the same, sometimes wrapped in an array on the
    /// streaming endpoint; Mistral sometimes sends a top-level `message` or `detail`. Anything else
    /// falls back to the start of the raw body, so the person still sees something specific.
    /// </summary>
    public static string VendorErrorMessage(string errorBody)
    {
        var trimmedErrorBody = errorBody.Trim();
        var parsedErrorBody = JsonHelpers.TryParse(trimmedErrorBody);
        var errorObject = parsedErrorBody as JsonObject ?? (parsedErrorBody as JsonArray)?.FirstOrDefault() as JsonObject;
        if (errorObject is not null)
        {
            if (errorObject["error"] is JsonObject nestedError && JsonHelpers.String(nestedError["message"]) is { Length: > 0 } nestedMessage) return nestedMessage;
            if (JsonHelpers.String(errorObject["error"]) is { Length: > 0 } plainError) return plainError;
            if (JsonHelpers.String(errorObject["message"]) is { Length: > 0 } topLevelMessage) return topLevelMessage;
            if (JsonHelpers.String(errorObject["detail"]) is { Length: > 0 } detail) return detail;
        }
        if (trimmedErrorBody.Length == 0) return "the request was refused with no explanation";
        return trimmedErrorBody.Length > 300 ? trimmedErrorBody[..300] : trimmedErrorBody;
    }

    /// <summary>
    /// A tool's JSON arguments as ClaudeToolCall carries them, the same way
    /// `ClaudeAPI.ParseToolCalls` reads Anthropic's: strings kept, numbers and booleans as their
    /// text (a JSON bool was an NSNumber on the Mac and reads "1" / "0"), anything nested dropped
    /// (no quick action takes a nested argument).
    /// </summary>
    public static Dictionary<string, string> ToolArguments(JsonElement argumentsObject)
    {
        var arguments = new Dictionary<string, string>();
        foreach (var property in argumentsObject.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String: arguments[property.Name] = property.Value.GetString()!; break;
                case JsonValueKind.Number: arguments[property.Name] = property.Value.GetRawText(); break;
                case JsonValueKind.True: arguments[property.Name] = "1"; break;
                case JsonValueKind.False: arguments[property.Name] = "0"; break;
            }
        }
        return arguments;
    }

    public static Dictionary<string, string> ToolArguments(JsonObject argumentsObject)
    {
        using var document = JsonDocument.Parse(argumentsObject.ToJsonString());
        return ToolArguments(document.RootElement);
    }

    /// <summary>
    /// The tool's input schema as a JSON object. A schema that does not parse becomes an empty
    /// object schema rather than failing the whole question.
    /// </summary>
    public static JsonObject InputSchemaObject(string inputSchemaJson) =>
        JsonHelpers.TryParse(inputSchemaJson) as JsonObject
        ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>
    /// The text after `data:` on an SSE line, or null for comments (OpenRouter sends
    /// `: OPENROUTER PROCESSING` keep-alives), `event:` lines and blanks.
    /// </summary>
    public static string? SseDataPayload(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        return line[5..].Trim(' ', '\t');
    }
}

/// <summary>Small System.Text.Json.Nodes readers matching Swift's `as? String` / `as? Int` / `as? Bool` casts.</summary>
internal static class JsonHelpers
{
    public static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static JsonNode? TryParse(byte[] data)
    {
        try { return JsonNode.Parse(data); }
        catch (JsonException) { return null; }
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
        try { return value.GetValue<int>(); }
        catch (FormatException) { return null; }
        catch (InvalidOperationException)
        {
            return double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                   && number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
        }
    }

    public static bool? Bool(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;
}

// MARK: - The provider

public sealed class OpenAIChatAnswerProvider(AnswerSource source, string apiKey, string model, AnswerHttpTransport? transport = null) : IAnswerProvider
{
    public AnswerSource Source { get; } = source;
    public string ApiKey { get; } = apiKey;
    public string Model { get; } = model;
    private readonly AnswerHttpTransport transport = transport ?? AnswerHttpTransports.Shared;

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, Action<string> onTextChunk, CancellationToken cancellationToken = default)
    {
        using var httpRequest = MakeHttpRequest(Source, ApiKey, Model, request);
        var response = await transport(httpRequest, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is < 200 or > 299)
        {
            var errorBody = await AnswerProviderHttpSupport.ReadWholeBodyAsync(response.BodyLines, cancellationToken).ConfigureAwait(false);
            var vendorMessage = AnswerProviderHttpSupport.VendorErrorMessage(errorBody);
            AppLog.Write($"answer: {Source.Id()} {Model} refused with {response.StatusCode}: {vendorMessage}");
            throw new AnswerProviderException(Source, response.StatusCode, vendorMessage);
        }

        var streamAccumulator = new OpenAIChatStreamAccumulator();
        await foreach (var line in response.BodyLines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            OpenAIChatStreamAccumulator.LineOutcome lineOutcome;
            try
            {
                lineOutcome = streamAccumulator.Consume(line);
            }
            catch (OpenAIChatStreamAccumulator.MidStreamException midStreamError)
            {
                AppLog.Write($"answer: {Source.Id()} {Model} stream error after {streamAccumulator.AccumulatedText.Length} chars: {midStreamError.Message}");
                throw new AnswerProviderException(Source, response.StatusCode, midStreamError.Message);
            }
            if (lineOutcome.Kind == OpenAIChatStreamAccumulator.LineOutcomeKind.StreamFinished) break;
            if (lineOutcome.Kind == OpenAIChatStreamAccumulator.LineOutcomeKind.TextChunk) onTextChunk(lineOutcome.Text!);
        }
        if (streamAccumulator.FinishReason == "length")
        {
            AppLog.Write($"answer: {Source.Id()} {Model} cut off at the token limit ({streamAccumulator.AccumulatedText.Length} chars)");
        }
        return streamAccumulator.Result();
    }

    // MARK: request

    /// <summary>
    /// OpenAI's reasoning models (gpt-5…, o1/o3/o4…) refuse `max_tokens` and want
    /// `max_completion_tokens`, which OpenAI's other current models accept too; so every request to
    /// OpenAI itself sends `max_completion_tokens`. The compatible services (Groq, Mistral, xAI,
    /// OpenRouter) are sent the classic `max_tokens`, which all of them take.
    /// Note: on a reasoning model the limit also covers its hidden reasoning, so a small limit can
    /// leave no room for the visible answer.
    /// </summary>
    public static string TokenLimitFieldName(AnswerSource source) => source == AnswerSource.OpenAI ? "max_completion_tokens" : "max_tokens";

    /// <summary>The request body as a JSON object (kept separate from the HttpRequestMessage so tests can read it).</summary>
    public static JsonObject MakeRequestBody(AnswerSource source, string model, AnswerRequest answerRequest)
    {
        var messages = new JsonArray();
        if (answerRequest.SystemPrompt.Length > 0)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = answerRequest.SystemPrompt });
        }
        foreach (var earlierTurn in answerRequest.History)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = earlierTurn.User });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = earlierTurn.Assistant });
        }

        // Each screenshot, then its label (which says which display it is and its pixel size, what
        // the [POINT:…] coordinates are measured against), then the question itself.
        var currentUserContentParts = new JsonArray();
        foreach (var labelledImage in answerRequest.Images)
        {
            var dataUrl = $"data:{labelledImage.MediaType};base64,{Convert.ToBase64String(labelledImage.Data)}";
            currentUserContentParts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } });
            currentUserContentParts.Add(new JsonObject { ["type"] = "text", ["text"] = labelledImage.Label });
        }
        currentUserContentParts.Add(new JsonObject { ["type"] = "text", ["text"] = answerRequest.UserPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = currentUserContentParts });

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = messages,
            [TokenLimitFieldName(source)] = answerRequest.MaxTokens,
        };
        if (answerRequest.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(answerRequest.Tools.Select(quickActionTool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = quickActionTool.Name,
                    ["description"] = quickActionTool.Description,
                    ["parameters"] = AnswerProviderHttpSupport.InputSchemaObject(quickActionTool.InputSchemaJson),
                },
            }).ToArray());
        }
        return body;
    }

    public static HttpRequestMessage MakeHttpRequest(AnswerSource source, string apiKey, string model, AnswerRequest answerRequest)
    {
        if (source.ChatCompletionsUrl() is not { } chatCompletionsUrl)
        {
            throw new AnswerProviderException(source, 0, $"{source.DisplayName()} does not offer an OpenAI-compatible chat endpoint");
        }
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, chatCompletionsUrl)
        {
            Content = DirectProviderRequests.JsonContent(MakeRequestBody(source, model, answerRequest)),
        };
        httpRequest.SetHeader("Accept", "text/event-stream");
        httpRequest.SetHeader("Authorization", $"Bearer {apiKey}");
        if (source == AnswerSource.OpenRouter)
        {
            // OpenRouter's app attribution: optional, but it is how the requests show up as
            // OpenClicky's on their side instead of as an unnamed client.
            httpRequest.SetHeader("HTTP-Referer", "https://github.com/prasanthsasikumar/openclicky");
            httpRequest.SetHeader("X-Title", "OpenClicky");
        }
        return httpRequest;
    }

    // MARK: stream

    /// <summary>The answer in a whole list of SSE lines (what the streaming loop builds line by line).</summary>
    public static AnswerResult ParseStream(IEnumerable<string> lines)
    {
        var streamAccumulator = new OpenAIChatStreamAccumulator();
        foreach (var line in lines)
        {
            if (streamAccumulator.Consume(line).Kind == OpenAIChatStreamAccumulator.LineOutcomeKind.StreamFinished) break;
        }
        return streamAccumulator.Result();
    }
}

/// <summary>
/// Reads a Chat Completions stream one SSE line at a time: text deltas as they come, tool calls
/// gathered by their `index` (the name arrives in the first fragment, the JSON arguments spread
/// over many) and parsed once the stream is over.
/// </summary>
public sealed class OpenAIChatStreamAccumulator
{
    public enum LineOutcomeKind
    {
        TextChunk,
        StreamFinished,
        NothingToShow,
    }

    public readonly record struct LineOutcome(LineOutcomeKind Kind, string? Text = null)
    {
        public static LineOutcome Chunk(string text) => new(LineOutcomeKind.TextChunk, text);
        public static LineOutcome Finished => new(LineOutcomeKind.StreamFinished);
        public static LineOutcome Nothing => new(LineOutcomeKind.NothingToShow);
    }

    /// <summary>
    /// An `{"error":…}` event after the 200 (OpenRouter reports an upstream failure this way);
    /// ignored, it would end the answer in silence as if it were complete.
    /// </summary>
    public sealed class MidStreamException(string message) : Exception(message);

    private sealed class PartialToolCall
    {
        public string Id = "";
        public string FunctionName = "";
        public readonly StringBuilder ArgumentsJsonText = new();
    }

    private readonly StringBuilder accumulatedText = new();
    private readonly SortedDictionary<int, PartialToolCall> partialToolCallsByIndex = new();

    public string AccumulatedText => accumulatedText.ToString();
    public string? FinishReason { get; private set; }

    public LineOutcome Consume(string line)
    {
        if (AnswerProviderHttpSupport.SseDataPayload(line) is not { } dataPayload) return LineOutcome.Nothing;
        if (dataPayload == "[DONE]") return LineOutcome.Finished;
        if (JsonHelpers.TryParse(dataPayload) is not JsonObject streamEvent) return LineOutcome.Nothing;
        if (streamEvent.TryGetPropertyValue("error", out var streamError) && streamError is not null)
        {
            var errorMessage = JsonHelpers.String((streamError as JsonObject)?["message"]) ?? JsonHelpers.String(streamError) ?? "the stream broke off";
            throw new MidStreamException(errorMessage);
        }
        if ((streamEvent["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject firstChoice) return LineOutcome.Nothing;
        if (JsonHelpers.String(firstChoice["finish_reason"]) is { } choiceFinishReason) FinishReason = choiceFinishReason;
        if (firstChoice["delta"] is not JsonObject delta) return LineOutcome.Nothing;

        if (delta["tool_calls"] is JsonArray toolCallFragments)
        {
            foreach (var toolCallFragment in toolCallFragments.OfType<JsonObject>())
            {
                // Some compatible services leave out `index` when there is only one call.
                var toolCallIndex = JsonHelpers.Int(toolCallFragment["index"]) ?? 0;
                if (!partialToolCallsByIndex.TryGetValue(toolCallIndex, out var partialToolCall))
                {
                    partialToolCall = new PartialToolCall();
                    partialToolCallsByIndex[toolCallIndex] = partialToolCall;
                }
                if (JsonHelpers.String(toolCallFragment["id"]) is { Length: > 0 } toolCallId) partialToolCall.Id = toolCallId;
                if (toolCallFragment["function"] is JsonObject function)
                {
                    if (JsonHelpers.String(function["name"]) is { Length: > 0 } functionName) partialToolCall.FunctionName = functionName;
                    if (JsonHelpers.String(function["arguments"]) is { } argumentsFragment) partialToolCall.ArgumentsJsonText.Append(argumentsFragment);
                }
            }
        }

        if (JsonHelpers.String(delta["content"]) is { Length: > 0 } textChunk)
        {
            accumulatedText.Append(textChunk);
            return LineOutcome.Chunk(textChunk);
        }
        return LineOutcome.Nothing;
    }

    /// <summary>
    /// The text and the tool calls, in the order the model made them. A call whose arguments are
    /// not valid JSON is dropped, as `ClaudeAPI.ParseToolCalls` drops Anthropic's.
    /// </summary>
    public AnswerResult Result()
    {
        var toolCalls = new List<ClaudeToolCall>();
        foreach (var (toolCallIndex, partialToolCall) in partialToolCallsByIndex)
        {
            if (partialToolCall.FunctionName.Length == 0) continue;
            var argumentsJsonText = partialToolCall.ArgumentsJsonText.Length == 0 ? "{}" : partialToolCall.ArgumentsJsonText.ToString();
            if (JsonHelpers.TryParse(argumentsJsonText) is not JsonObject argumentsObject) continue;
            toolCalls.Add(new ClaudeToolCall(
                partialToolCall.Id.Length == 0 ? $"call-{toolCallIndex}" : partialToolCall.Id,
                partialToolCall.FunctionName,
                AnswerProviderHttpSupport.ToolArguments(argumentsObject)));
        }
        return new AnswerResult(AccumulatedText, toolCalls);
    }
}
