// Ports Dictation/TakePolishers.swift: the models that tidy a take and make Hey Clicky edits.
// Sarvam's chat on a Sarvam key, OpenAI on an own OpenAI key or the backend's chat route, Claude
// Haiku on an own Anthropic key, any other answer key through its own vendor, and the backend's
// /v1/polish for accounts. (The Mac's first rung, Apple's on-device model, has no Windows twin.)
// `DictationEngineResolver.MakePolisher` picks one. Each says honestly who refused: an error
// blamed on the wrong vendor sends people to the wrong key.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Dictation;

public enum TakePolishErrorKind
{
    /// <summary>The vendor answered with a refusal (`Status` is its HTTP status, 0 when there was none).</summary>
    Refused,
    /// <summary>The vendor answered, but there was no text in it.</summary>
    EmptyAnswer,
    /// <summary>Nothing was sent: the route this polisher needs is not set up (the sentence says what is missing).</summary>
    NotSetUp,
}

/// <summary>
/// Why a vendor reached directly on the person's own key did not tidy or edit the words. Neutral
/// on purpose: a refusal names the vendor that refused.
/// </summary>
public sealed class TakePolishException : Exception
{
    public TakePolishErrorKind Kind { get; }
    public string Vendor { get; }
    public int Status { get; }
    public string Detail { get; }

    private TakePolishException(TakePolishErrorKind kind, string vendor, int status, string detail, string message) : base(message)
    {
        Kind = kind; Vendor = vendor; Status = status; Detail = detail;
    }

    public static TakePolishException Refused(string vendor, int status, string message)
    {
        var statusPart = status == 0 ? "" : $" ({status})";
        var trimmedMessage = message.Trim();
        return new(TakePolishErrorKind.Refused, vendor, status, message,
            $"{vendor} refused the request{statusPart}" + (trimmedMessage.Length == 0 ? "." : $": {trimmedMessage}"));
    }

    public static TakePolishException EmptyAnswer(string vendor) =>
        new(TakePolishErrorKind.EmptyAnswer, vendor, 0, "", $"{vendor} sent back no text.");

    public static TakePolishException NotSetUp(string sentence) =>
        new(TakePolishErrorKind.NotSetUp, "", 0, sentence, sentence);
}

/// <summary>Polishes through Sarvam's chat completions with the user's key.</summary>
public sealed class SarvamTakePolisher(SarvamSpeechClient client) : ITakePolisher
{
    public SarvamSpeechClient Client { get; } = client;
    public string DisplayName => "Sarvam";

    public Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default) =>
        Client.CompleteAsync(system, user, cancellationToken: cancellationToken);
}

public enum BackendPolishErrorKind { InvalidBackendUrl, Refused, Unreadable }

/// <summary>
/// Why the backend's chat route did not polish a take. Its own type: these are OpenClicky's
/// answers, and logging them as Sarvam's ("sarvam refused (401)") sent people to the wrong key.
/// </summary>
public sealed class BackendPolishException : Exception
{
    public BackendPolishErrorKind Kind { get; }
    public int Status { get; }
    public string Detail { get; }

    private BackendPolishException(BackendPolishErrorKind kind, int status, string detail, string message) : base(message)
    {
        Kind = kind; Status = status; Detail = detail;
    }

    public static BackendPolishException InvalidBackendUrl() =>
        new(BackendPolishErrorKind.InvalidBackendUrl, 0, "", "the openclicky backend address in settings isn't a valid url.");

    public static BackendPolishException Refused(int status, string message) =>
        new(BackendPolishErrorKind.Refused, status, message, $"openclicky refused the clean-up ({status}): {message}");

    public static BackendPolishException Unreadable() =>
        new(BackendPolishErrorKind.Unreadable, 0, "", "openclicky's clean-up answer had no text in it.");
}

/// <summary>
/// Polishes through OpenAI's chat completions: straight to OpenAI on the person's own OpenAI key,
/// else through the OpenClicky backend's OpenAI-shaped chat route.
/// </summary>
public sealed class BackendTakePolisher : ITakePolisher
{
    public const string Model = "gpt-4o-mini";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string DisplayName => "OpenClicky";

    public static JsonObject Body(string system, string user) => new()
    {
        ["model"] = Model,
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = user }),
        ["temperature"] = 0.2,
    };

    public async Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var body = Body(system, user);
        HttpResponseMessage response;
        // Which side answered decides whose error a refusal is: OpenAI's own (the person's key) or
        // OpenClicky's (the backend).
        bool wentStraightToOpenAI;
        var route = ProviderRouter.Current(ProviderCapability.OpenAIChat);
        switch (route.Kind)
        {
            case ProviderRouteKind.OpenAIDirect:
                // The person's own OpenAI key: the same chat completion, sent to OpenAI itself.
                var openAIKey = ProviderRouter.CurrentCredentials().OpenAIKey
                    ?? throw TakePolishException.NotSetUp(ProviderRouter.NeedsOpenAIKeySentence);
                response = await HttpClients.Shared.SendAsync(DirectProviderRequests.OpenAIChatCompletions(openAIKey, body), timeout.Token).ConfigureAwait(false);
                wentStraightToOpenAI = true;
                break;
            case ProviderRouteKind.Unavailable:
                throw TakePolishException.NotSetUp(route.Sentence ?? ProviderRouter.NeedsOpenAIKeySentence);
            default:
                if (!Uri.TryCreate($"{OpenClickyConfiguration.BackendBaseUrl}/v1/chat/completions", UriKind.Absolute, out var url))
                {
                    throw BackendPolishException.InvalidBackendUrl();
                }
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = DirectProviderRequests.JsonContent(body) };
                OpenClickyConfiguration.Authorize(request);
                // A refused sign-in is refreshed once and sent again (BackendSignIn); what is left here is the backend's answer.
                response = await BackendSignIn.SendAsync(request, sent => HttpClients.Shared.SendAsync(sent, timeout.Token)).ConfigureAwait(false);
                wentStraightToOpenAI = false;
                break;
        }
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                var message = Encoding.UTF8.GetString(data, 0, Math.Min(200, data.Length));
                if (wentStraightToOpenAI) throw TakePolishException.Refused("openai", status, message);
                throw BackendPolishException.Refused(status, message);
            }
            if (ChatCompletionContent(data) is { } content) return content;
            if (wentStraightToOpenAI) throw TakePolishException.EmptyAnswer("openai");
            throw BackendPolishException.Unreadable();
        }
    }

    /// <summary>`choices[0].message.content` of an OpenAI-shaped chat completion, null when absent.</summary>
    public static string? ChatCompletionContent(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is not JsonObject answer || answer["choices"] is not JsonArray choices || choices.Count == 0) return null;
            if (choices[0]?["message"]?["content"] is JsonValue value && value.TryGetValue<string>(out var content)) return content;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// MARK: - Accounts

/// <summary>
/// Polish and Hey Clicky edits for signed-in accounts failed: a limit (typed, so the take still
/// pastes with the local clean-up) or the backend unavailable. The message is a plain sentence for
/// the orb and Hey Clicky edits, never "error 0".
/// </summary>
public sealed class AccountPolishException : Exception
{
    public AccountLimitError? Limit { get; }
    /// <summary>The status the backend answered with when it was unavailable (0: no answer).</summary>
    public int UnavailableStatus { get; }

    private AccountPolishException(AccountLimitError? limit, int unavailableStatus, string message) : base(message)
    {
        Limit = limit;
        UnavailableStatus = unavailableStatus;
    }

    public static AccountPolishException ForLimit(AccountLimitError limit)
    {
        var message = limit.Message();
        return new(limit, 0, message.Length > 0 ? message : CouldNotReach);
    }

    public static AccountPolishException Unavailable(int status) =>
        new(null, status, status == 503 ? AccountLimitErrors.ServiceTroubleMessage : CouldNotReach);

    public const string CouldNotReach = "couldn't reach openclicky right now.";
}

/// <summary>
/// Polish and Hey Clicky edits for signed-in accounts: the app says what to do and sends the
/// words; the backend picks the model (a cheap one) and bills the grant.
/// </summary>
public sealed class AccountTakePolisher : ITakePolisher
{
    public string Purpose { get; init; } = "polish";
    /// <summary>The transport; the shared client by default.</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Send { get; init; } = request => HttpClients.Shared.SendAsync(request);
    /// <summary>What a 401 does to the session (refresh once, sign out on the second); injectable for tests.</summary>
    public BackendSignIn.SignInRejectionHandling SignIn { get; init; } = BackendSignIn.SignInRejectionHandling.Live;
    /// <summary>
    /// Puts the stored sign-in on the request; injectable so tests carry a token of their own
    /// (a request sent without one gets its 401 back, never a refresh).
    /// </summary>
    public Action<HttpRequestMessage> Authorize { get; init; } = OpenClickyConfiguration.Authorize;
    public string BackendBaseUrl { get; init; } = OpenClickyConfiguration.BackendBaseUrl;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string DisplayName => "OpenClicky";

    public async Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate($"{BackendBaseUrl}/v1/polish", UriKind.Absolute, out var url)) throw AccountPolishException.Unavailable(0);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = DirectProviderRequests.JsonContent(new JsonObject { ["purpose"] = Purpose, ["system"] = system, ["text"] = user }),
        };
        Authorize(request);
        // A refused sign-in is refreshed once and sent again; a second refusal throws
        // BackendSignInException(Expired), whose sentence is the sign-in one, not "couldn't reach".
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await BackendSignIn.SendAsync(request, Send, SignIn).WaitAsync(timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (AccountLimitErrors.From(status, data) is { } limit) throw AccountPolishException.ForLimit(limit);
        if (status is >= 200 and < 300)
        {
            try
            {
                if (JsonNode.Parse(data) is JsonObject json && json["text"] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
            }
            catch (JsonException) { }
        }
        throw AccountPolishException.Unavailable(status);
    }
}

// MARK: - Own keys

/// <summary>
/// Polish and edits on the person's own Anthropic key, straight to Anthropic: the request the
/// backend's /v1/polish sends upstream (polish.ts), on Claude Haiku. No account involved.
/// </summary>
public sealed class AnthropicTakePolisher(string anthropicKey) : ITakePolisher
{
    public string AnthropicKey { get; } = anthropicKey;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; init; } =
        (request, token) => HttpClients.Shared.SendAsync(request, token);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string DisplayName => "Claude (your key)";

    public async Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await Send(DirectProviderRequests.AnthropicPolish(AnthropicKey, system, user), timeout.Token).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            AppLog.Write($"polish (own anthropic key): anthropic answered {status}");
            throw TakePolishException.Refused("anthropic", status, Encoding.UTF8.GetString(data, 0, Math.Min(200, data.Length)));
        }
        if (DirectProviderRequests.AnthropicReplyText(data) is not { } text)
        {
            AppLog.Write("polish (own anthropic key): the reply had no whole text");
            throw TakePolishException.EmptyAnswer("anthropic");
        }
        return text;
    }
}

/// <summary>An answer key's vendor refused (the Mac's AnswerProviderError: status and message).</summary>
public sealed class AnswerKeyCompletionException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}

/// <summary>
/// One non-streamed completion on an answer key: the seam AnswerKeyTakePolisher asks.
/// TODO(integration): once Answers/AnswerProviderFactory is ported, implement this over
/// `AnswerProviderFactory.Make(source, credentials, modelOverrides)` + `provider.Answer(...)`
/// (Swift: AnswerKeyTakePolisher.polish), and drop `DirectAnswerKeyCompletion`.
/// </summary>
public interface IAnswerKeyCompletion
{
    Task<string> CompleteAsync(AnswerSource source, ProviderCredentials credentials, string? modelOverride,
                               string system, string user, int maxTokens, CancellationToken cancellationToken);
}

/// <summary>
/// The stand-in until the answer providers are ported: the OpenAI-compatible sources' chat
/// completions (max_tokens, as the Mac sends the compatible services) and Gemini's generateContent
/// (key in `x-goog-api-key`, never in the URL), not streamed.
/// </summary>
public sealed class DirectAnswerKeyCompletion : IAnswerKeyCompletion
{
    public static DirectAnswerKeyCompletion Shared { get; } = new();

    public async Task<string> CompleteAsync(AnswerSource source, ProviderCredentials credentials, string? modelOverride,
                                            string system, string user, int maxTokens, CancellationToken cancellationToken)
    {
        var key = credentials.Key(source) ?? throw new AnswerKeyCompletionException(0, $"no {source.ShortName().ToLowerInvariant()} key");
        var model = modelOverride ?? source.DefaultModel() ?? throw new AnswerKeyCompletionException(0, "no model");
        HttpRequestMessage request;
        if (source == AnswerSource.Gemini)
        {
            var pathSafeModel = Uri.EscapeDataString(model);
            request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{pathSafeModel}:generateContent")
            {
                Content = DirectProviderRequests.JsonContent(new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = user }),
                    }),
                    ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = maxTokens },
                    ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                }),
            };
            request.SetHeader("x-goog-api-key", key);
        }
        else
        {
            var url = source.ChatCompletionsUrl() ?? throw new AnswerKeyCompletionException(0, "no chat completions endpoint");
            request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = DirectProviderRequests.JsonContent(new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = user }),
                    [source == AnswerSource.OpenAI ? "max_completion_tokens" : "max_tokens"] = maxTokens,
                }),
            };
            request.SetHeader("Authorization", $"Bearer {key}");
        }
        using var response = await HttpClients.Shared.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300) throw new AnswerKeyCompletionException(status, Encoding.UTF8.GetString(data, 0, Math.Min(300, data.Length)));
        return (source == AnswerSource.Gemini ? GeminiText(data) : BackendTakePolisher.ChatCompletionContent(data)) ?? "";
    }

    internal static string? GeminiText(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data)?["candidates"] is not JsonArray candidates || candidates.Count == 0) return null;
            if (candidates[0]?["content"]?["parts"] is not JsonArray parts) return null;
            return string.Concat(parts.Select(part => part?["text"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : ""));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Polish and edits on any other answer key the person added (OpenRouter, Gemini, Groq, Mistral,
/// xAI), straight to that vendor. A quick model where the default answer model is a slow one: the
/// clean-up gives up after `TakeFormatter.ModelTimeout`. The two plans are left out: starting
/// their CLI alone takes longer than that.
/// </summary>
public sealed class AnswerKeyTakePolisher(AnswerSource source, ProviderCredentials credentials) : ITakePolisher
{
    public AnswerSource Source { get; } = source;
    public ProviderCredentials Credentials { get; } = credentials;
    public IAnswerKeyCompletion Completion { get; init; } = DirectAnswerKeyCompletion.Shared;

    /// <summary>The keys that can polish, in the order they are tried; the fast ones first.</summary>
    public static IReadOnlyList<AnswerSource> SourcesInOrder { get; } =
        [AnswerSource.Groq, AnswerSource.Gemini, AnswerSource.Mistral, AnswerSource.OpenRouter, AnswerSource.Xai];

    /// <summary>The model asked to tidy a take, where it differs from the answer model.</summary>
    public static string? PolishModel(AnswerSource source) => source switch
    {
        AnswerSource.OpenRouter => "openai/gpt-4.1-mini",
        AnswerSource.Mistral => "mistral-small-latest",
        _ => null,
    };

    /// <summary>The first source with a key, null without any.</summary>
    public static AnswerKeyTakePolisher? FirstAvailable(ProviderCredentials credentials)
    {
        foreach (var candidate in SourcesInOrder)
        {
            if (credentials.Key(candidate) is not null) return new AnswerKeyTakePolisher(candidate, credentials);
        }
        return null;
    }

    public string DisplayName => $"{Source.ShortName()} (your key)";

    public async Task<string> PolishAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        var vendorName = Source.ShortName().ToLowerInvariant();
        try
        {
            var text = (await Completion.CompleteAsync(Source, Credentials, PolishModel(Source), system, user, 1024, cancellationToken).ConfigureAwait(false)).Trim();
            if (text.Length == 0) throw TakePolishException.EmptyAnswer(vendorName);
            return text;
        }
        catch (AnswerKeyCompletionException error)
        {
            AppLog.Write($"polish (own {Source.RawValue()} key): {error.Status}");
            throw TakePolishException.Refused(vendorName, error.Status, error.Detail);
        }
    }
}

// MARK: - Saying why the clean-up was skipped

/// <summary>
/// Why a take was pasted with only the local clean-up, said once per session (every take hitting
/// the same limit would otherwise repeat it). The orb still says "cleaned up locally"; this is the
/// reason behind it, shown as the companion's quiet caption. Null for a reason not worth saying
/// (a timeout, a passing server error): the log has those.
/// </summary>
public static class PolishFailureNotice
{
    public const string OwnKeyRefusedSentence = "your own api key was refused, so your words were cleaned up locally. check the key in settings, keys & plans.";

    public static string? Sentence(Exception error) => error switch
    {
        // Signing out already said so, on the account page and in a caption.
        BackendSignInException { Error: BackendSignInError.Expired } => null,
        AccountPolishException { Limit: { } limit } => limit.Message().Length == 0 ? null : limit.Message(),
        AccountPolishException { Limit: null, UnavailableStatus: 503 } => AccountLimitErrors.ServiceTroubleMessage,
        // The backend relays a refusal of the person's own provider key with its status.
        BackendPolishException { Kind: BackendPolishErrorKind.Refused, Status: 401 or 403 } => OwnKeyRefusedSentence,
        // A vendor reached directly is only ever reached on the person's own key.
        TakePolishException { Kind: TakePolishErrorKind.Refused, Status: 401 or 403 } => OwnKeyRefusedSentence,
        _ => null,
    };

    private static readonly object ShownLock = new();
    /// <summary>Sentences already shown this session.</summary>
    private static readonly HashSet<string> ShownSentences = [];

    /// <summary>Returns the sentence when it is new to `alreadyShown` (and marks it shown), null otherwise.</summary>
    public static string? SentenceToShow(Exception error, ISet<string> alreadyShown)
    {
        if (Sentence(error) is not { } sentence || alreadyShown.Contains(sentence)) return null;
        alreadyShown.Add(sentence);
        return sentence;
    }

    /// <summary>Where a new sentence is shown: the companion's quiet caption. Replaceable (tests).</summary>
    public static Action<string> ShowNotice { get; set; } = sentence => AccountProfileStore.Shared.ShowNotice?.Invoke(sentence);

    public static void Note(Exception error)
    {
        string? sentence;
        lock (ShownLock) sentence = SentenceToShow(error, ShownSentences);
        if (sentence is null) return;
        ShowNotice(sentence);
    }
}
