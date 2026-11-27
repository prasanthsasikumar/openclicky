// What the ask lane says (or does not say) when an answer does not arrive normally: the logic
// parts of macos/OpenClicky/OpenClicky/CompanionManager.swift that AskLaneFailureTests covers.
//
// Windows: URLError's codes become .NET's exceptions. A cancelled question is an
// OperationCanceledException (TaskCanceledException included) that is not HttpClient's own
// timeout; the network failures are HttpRequestException (no route, no name, connection lost) and
// timeouts (TimeoutException, or a TaskCanceledException carrying one).

using System.Net.Http;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

/// <summary>
/// The screenshot for a question could not be taken, as opposed to the backend failing
/// (Swift `CompanionManager.AskScreenCaptureError`).
/// </summary>
public sealed class AskScreenCaptureException(Exception underlyingError)
    : Exception("couldn't capture the screen for a question", underlyingError)
{
    public Exception UnderlyingError { get; } = underlyingError;
}

public static class AskLaneFailures
{
    public const string EmptyAnswerSentence = "i didn't get an answer. please ask again.";

    /// <summary>Said when the AI that answers is down or overloaded (after one quiet retry): what happened, in a child's
    /// words, instead of "something went wrong" or the provider's own message.</summary>
    public const string OutageSentence = "the ai that answers me is having trouble right now. please ask again in a minute.";

    /// <summary>How long the ask lane waits before its one retry after an outage.</summary>
    public static readonly TimeSpan OutageRetryDelay = TimeSpan.FromSeconds(2);

    private static readonly int[] OutageStatuses = [500, 502, 503, 504, 529];

    /// <summary>
    /// The AI that answers is down or overloaded, not refusing this question or this person: a server error or
    /// "overloaded" (529) from the backend or an own key's provider, Anthropic's overloaded or api error partway into the
    /// stream, or a provider's mid-stream "overloaded" / "unavailable". A rate limit (429) is the person's own limit, not an
    /// outage.
    /// </summary>
    public static bool IsOutage(Exception error) => error switch
    {
        BackendHttpException { Service: BackendHttpException.ServiceKind.Answers } refusal => OutageStatuses.Contains(refusal.Status),
        AnswerProviderException { Status: 0 } providerError => SaysOverloaded(providerError.VendorMessage),
        AnswerProviderException providerError => OutageStatuses.Contains(providerError.Status),
        ClaudeApiException { Code: ClaudeAPI.MidStreamErrorCode } midStream =>
            midStream.Message.Contains("overloaded_error", StringComparison.Ordinal) || midStream.Message.Contains("(api_error)", StringComparison.Ordinal),
        _ => false,
    };

    private static bool SaysOverloaded(string message) =>
        message.Contains("overloaded", StringComparison.OrdinalIgnoreCase) || message.Contains("UNAVAILABLE", StringComparison.Ordinal)
        || message.Contains("high demand", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs <paramref name="attempt"/>, and once more after <paramref name="delay"/> when it fails with an outage before any
    /// of the answer arrived (<paramref name="answerArrived"/>; a retry then would say it twice). Anything else, a second
    /// failure, or a cancellation is thrown as it came.
    /// </summary>
    public static async Task<T> RetryOnceAfterOutageAsync<T>(Func<Task<T>> attempt, Func<bool> answerArrived, TimeSpan delay,
        Action<Exception> onRetry, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        try
        {
            return await attempt();
        }
        catch (Exception error) when (IsOutage(error) && !answerArrived() && !cancellationToken.IsCancellationRequested)
        {
            onRetry(error);
        }
        await (wait ?? Task.Delay)(delay, cancellationToken);
        return await attempt();
    }

    /// <summary>
    /// An interrupted request: the caller's cancellation, or the HTTP read cancelled because a new
    /// question replaced this one. HttpClient's own timeout (a TaskCanceledException wrapping a
    /// TimeoutException) is a failure, not an interruption.
    /// </summary>
    public static bool IsCancellation(Exception error) =>
        error is OperationCanceledException && error.InnerException is not TimeoutException;

    /// <summary>The network did not carry the question: no connection, no name, a dropped connection, or a timeout.</summary>
    public static bool IsUnreachable(Exception error) => error switch
    {
        TimeoutException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        HttpRequestException httpError => httpError.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
            || httpError.InnerException is System.Net.Sockets.SocketException or IOException,
        IOException { InnerException: System.Net.Sockets.SocketException } => true,
        _ => false,
    };

    /// <summary>The refusal of a question (ClaudeAPI's /chat, or Anthropic on an own key), as it came; null for anything else.</summary>
    public static BackendHttpException? RefusedQuestion(Exception error) =>
        error is BackendHttpException { Service: BackendHttpException.ServiceKind.Answers } refusal ? refusal : null;

    /// <summary>
    /// What the companion says when a question fails for a reason that is not the account's
    /// (sign-in and limits have their own sentences). The token sentence is kept for the backend
    /// refusing the token; a capture failure or a dropped connection says what actually happened.
    /// </summary>
    /// <param name="screenRecordingAllowed">
    /// The Mac's Screen Recording permission. Windows has no such switch for a desktop app's
    /// capture, so the Windows caller passes true; false keeps the Mac's sentence.
    /// </param>
    public static string AskFailureSentence(Exception error, bool screenRecordingAllowed = true)
    {
        // No key for Claude and no account: say which key would do.
        if (error is ProviderRouteUnavailableException unavailable) return unavailable.Sentence;
        if (error is AskScreenCaptureException)
        {
            return screenRecordingAllowed
                ? "i couldn't see your screen just now. please ask again."
                : "screen recording is off for openclicky. turn it on in settings, privacy and security, screen recording.";
        }
        if (IsUnreachable(error)) return "i couldn't reach openclicky. check your internet and ask again.";
        if (RefusedQuestion(error) is { Status: 401 or 403 })
        {
            return "something went wrong talking to the openclicky backend. check the backend and your token in the settings file.";
        }
        // Not an HTTP refusal: the stream had started when Anthropic reported the failure (as the Mac says it, outage or not).
        if (error is ClaudeApiException { Code: ClaudeAPI.MidStreamErrorCode }) return "the answer stopped partway. please ask again.";
        // The AI itself is down or overloaded (already tried twice): say so plainly.
        if (IsOutage(error)) return OutageSentence;
        return "something went wrong getting your answer. please ask again.";
    }

    /// <summary>What the companion says: the answer, then what each quick action did.</summary>
    public static string SpokenReply(string text, IEnumerable<string> toolOutcomes)
    {
        var answer = text.Trim();
        return string.Join(" ", new[] { answer }.Concat(toolOutcomes).Where(part => part.Length > 0));
    }
}
