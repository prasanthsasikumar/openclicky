// Ports BackendSignIn.swift: one answer to "the backend said 401" for every caller. The rule:
// refresh once, send once more with the new token, and a second 401 signs out and throws
// `BackendSignInException` (Expired), whose sentence is the one the account form shows.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

/// <summary>Why a backend request the sign-in was refused for could not be saved by one refresh.</summary>
public enum BackendSignInError
{
    /// <summary>Signed out (or never signed in): the person has to sign in again.</summary>
    Expired,
    /// <summary>The refresh could not be tried (offline, sign-in server down); the session is kept.</summary>
    CouldNotRenew,
}

public sealed class BackendSignInException(BackendSignInError error) : Exception(error switch
{
    BackendSignInError.Expired => OpenClickyAuthSession.SessionExpiredMessage,
    _ => OpenClickyAuthSession.UnreachableMessage,
})
{
    public BackendSignInError Error { get; } = error;
}

/// <summary>
/// A refused answer to a model call, with the status and body exactly as they came: the account
/// notices (402 limits, 503 trouble), a refused sign-in (401) and an own key's refusal (a 401 whose
/// `error` is an object) are read from the body here. ClaudeAPI throws it for /chat and for
/// Anthropic on an own key; AnswerVoicePlayer for the free voice's /tts.
/// </summary>
public sealed class BackendHttpException : Exception
{
    /// <summary>Which call was refused.</summary>
    public enum ServiceKind
    {
        /// <summary>A question: ClaudeAPI (/chat, or Anthropic directly).</summary>
        Answers,
        /// <summary>The account's free voice: AnswerVoicePlayer (/tts).</summary>
        Voice,
    }

    public ServiceKind Service { get; }
    public int Status { get; }
    public byte[] Body { get; }
    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>The status, as NSError's code was (what log lines print).</summary>
    public int Code => Status;

    public BackendHttpException(ServiceKind service, int status, byte[] body)
        : base(Describe(service, status, body))
    {
        Service = service;
        Status = status;
        Body = body;
        HResult = status;
    }

    private static string Describe(ServiceKind service, int status, byte[] body)
    {
        var bodyText = Encoding.UTF8.GetString(body);
        return service == ServiceKind.Answers ? $"API Error ({status}): {bodyText}" : $"TTS API error ({status}): {bodyText}";
    }

    public override bool Equals(object? obj) =>
        obj is BackendHttpException other && other.Service == Service && other.Status == Status && other.Body.AsSpan().SequenceEqual(Body);

    public override int GetHashCode() => HashCode.Combine(Service, Status, Body.Length);
}

public static class BackendSignIn
{
    /// <summary>
    /// The session side of `Send`, injectable so tests never touch the real shell.json.
    /// </summary>
    public sealed record SignInRejectionHandling(
        Func<string?, Task<OpenClickyAuthSession.RejectionOutcome>> HandleRejection,
        Func<Task<string?>> CurrentToken,
        Func<Task> SignOutExpired)
    {
        public static SignInRejectionHandling Live { get; } = new(
            sentToken => OpenClickyAuthSession.Shared.HandleBackendRejectionAsync(sentToken),
            () => Task.FromResult(OpenClickyConfiguration.Token),
            () => { OpenClickyAuthSession.Shared.SignOut(becauseSignInExpired: true); return Task.CompletedTask; });
    }

    /// <summary>
    /// Sends a backend request authorised with the stored token. On a 401 about the sign-in it
    /// refreshes once and sends the request again with the new token; a second 401 signs out and
    /// throws Expired. Any other answer (including a 401 relayed from the person's own provider
    /// key) is returned as it is, for the caller to read. `transport` defaults to the shared client.
    /// </summary>
    public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                      Func<HttpRequestMessage, Task<HttpResponseMessage>>? transport = null,
                                                      SignInRejectionHandling? signIn = null)
    {
        transport ??= sent => HttpClients.Shared.SendAsync(sent);
        return SendAsync(request, transport, async answer =>
        {
            var status = (int)answer.StatusCode;
            // Only a refusal's body is read ahead (buffered, so the caller can still read it).
            if (status != 401) return (status, Array.Empty<byte>());
            await answer.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            return (status, await answer.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }, signIn);
    }

    /// <summary>
    /// `SendAsync` for an answer of any shape. `readingStatusAndBody` gives an answer's status and,
    /// for a refusal, its body. ClaudeAPI's streamed answers come this way: the stream of a 2xx
    /// answer must be left for the caller to read, so only a refusal's body is read ahead.
    /// </summary>
    public static async Task<TAnswer> SendAsync<TAnswer>(HttpRequestMessage request,
                                                         Func<HttpRequestMessage, Task<TAnswer>> transport,
                                                         Func<TAnswer, Task<(int Status, byte[] Body)>> readingStatusAndBody,
                                                         SignInRejectionHandling? signIn = null)
    {
        signIn ??= SignInRejectionHandling.Live;
        // A request goes out once; keep a copy for the retry before the first send.
        var retryCopy = await request.CloneAsync().ConfigureAwait(false);
        var firstAnswer = await transport(request).ConfigureAwait(false);
        var (firstStatus, firstBody) = await readingStatusAndBody(firstAnswer).ConfigureAwait(false);
        if (!IsSignInRejection(firstStatus, firstBody)) return firstAnswer;
        // Sent with no token (a PC on its own keys, no account): there is no sign-in to renew or
        // end, so nobody is signed out or told to sign in again; the caller reads the answer.
        var sentToken = BearerToken(request);
        if (string.IsNullOrEmpty(sentToken))
        {
            AppLog.Write("sign-in: 401 for a request sent with no token; no sign-in to renew, nothing signed out");
            return firstAnswer;
        }
        switch (await signIn.HandleRejection(sentToken).ConfigureAwait(false))
        {
            case OpenClickyAuthSession.RejectionOutcome.SignedOut:
                throw new BackendSignInException(BackendSignInError.Expired);
            case OpenClickyAuthSession.RejectionOutcome.CouldNotCheck:
                throw new BackendSignInException(BackendSignInError.CouldNotRenew);
        }
        var renewedToken = await signIn.CurrentToken().ConfigureAwait(false);
        if (renewedToken is null) throw new BackendSignInException(BackendSignInError.Expired);
        retryCopy.SetHeader("Authorization", $"Bearer {renewedToken}");
        var secondAnswer = await transport(retryCopy).ConfigureAwait(false);
        var (secondStatus, secondBody) = await readingStatusAndBody(secondAnswer).ConfigureAwait(false);
        if (!IsSignInRejection(secondStatus, secondBody)) return secondAnswer;
        // A token just renewed and refused as well: the sign-in itself is dead, not its token.
        AppLog.Write("sign-in: the renewed token was refused too; signing out");
        await signIn.SignOutExpired().ConfigureAwait(false);
        throw new BackendSignInException(BackendSignInError.Expired);
    }

    /// <summary>
    /// A 401 from the backend's own sign-in check carries `{"error": "&lt;sentence&gt;"}`. The proxy
    /// routes relay the provider's own 401 for a person's own key, whose `error` is an object; that
    /// one is about the key, and must never sign anybody out.
    /// </summary>
    public static bool IsSignInRejection(int status, byte[] body) => status == 401 && !IsOwnKeyRefusal(body);

    public static bool IsOwnKeyRefusal(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject json && json["error"] is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static int StatusCode(HttpResponseMessage? response) => response is null ? 0 : (int)response.StatusCode;

    public static string? BearerToken(HttpRequestMessage request)
    {
        var header = request.HeaderValue("Authorization");
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        return header["Bearer ".Length..];
    }
}
