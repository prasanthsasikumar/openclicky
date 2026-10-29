//
//  ClaudeUsage.cs
//  OpenClicky (Windows port of Plugins/CodingAgents/ClaudeUsage.swift)
//
//  Claude's plan limits for the Coding agents plugin, read with the sign-in Claude Code already
//  keeps on this PC: two windows (5 hours, 7 days), each a fraction and a reset time. The Mac reads
//  the sign-in from the Keychain; on Windows Claude Code keeps the same JSON in
//  %CLAUDE_CONFIG_DIR% (default %USERPROFILE%\.claude)\.credentials.json. It is read at most every
//  30 minutes, only its access token is kept (in memory), and the endpoint is asked only while the
//  plugin is on screen.
//

using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.CodingAgents;

public sealed record UsageWindow(double Fraction, DateTimeOffset? ResetsAt);

public sealed record ClaudeUsage(UsageWindow FiveHour, UsageWindow SevenDay)
{
    /// <summary>The usage reply: <c>five_hour</c> / <c>seven_day</c> with <c>utilization</c> (0…100) and <c>resets_at</c>.</summary>
    public static ClaudeUsage? Parse(string json)
    {
        JsonObject? reply;
        try { reply = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (reply is null || Window(reply["five_hour"]) is not { } fiveHour || Window(reply["seven_day"]) is not { } sevenDay) return null;
        return new ClaudeUsage(fiveHour, sevenDay);
    }

    private static UsageWindow? Window(JsonNode? node)
    {
        if (node is not JsonObject window || window["utilization"] is not JsonValue utilizationValue
            || !utilizationValue.TryGetValue<double>(out var utilization)) return null;
        DateTimeOffset? resetsAt = window["resets_at"] is JsonValue resetValue && resetValue.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        return new UsageWindow(Math.Clamp(utilization / 100, 0, 1), resetsAt);
    }
}

/// <summary>What the limits side of the plugin shows.</summary>
public abstract record ClaudeUsageState
{
    public sealed record None : ClaudeUsageState;
    public sealed record SignInNeeded : ClaudeUsageState;
    /// <summary>The sign-in could not be read (Windows refused the file): remembered until the plugin is switched off.</summary>
    public sealed record CredentialsRefused : ClaudeUsageState;
    public sealed record Figures(ClaudeUsage Usage, bool IsStale) : ClaudeUsageState;
}

public abstract record ClaudeCredentialResult
{
    public sealed record Token(string Value) : ClaudeCredentialResult;
    public sealed record Missing : ClaudeCredentialResult;
    public sealed record Refused : ClaudeCredentialResult;
}

public interface IClaudeCredentialReader
{
    ClaudeCredentialResult ReadAccessToken();
}

/// <summary>Claude Code's <c>.credentials.json</c>: <c>claudeAiOauth.accessToken</c>, as in the Mac's Keychain item.</summary>
public sealed class FileClaudeCredentialReader(string? path = null) : IClaudeCredentialReader
{
    /// <summary>Where Claude Code keeps its sign-in on Windows.</summary>
    public static string StandardPath
    {
        get
        {
            var claudeConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(claudeConfig, ".credentials.json");
        }
    }

    public ClaudeCredentialResult ReadAccessToken()
    {
        string text;
        try { text = File.ReadAllText(path ?? StandardPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new ClaudeCredentialResult.Missing(); }
        catch (UnauthorizedAccessException) { return new ClaudeCredentialResult.Refused(); }
        catch (IOException) { return new ClaudeCredentialResult.Missing(); }
        return TokenFrom(text);
    }

    /// <summary>The access token in the credentials JSON, or Missing.</summary>
    public static ClaudeCredentialResult TokenFrom(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["claudeAiOauth"]?["accessToken"] is JsonValue value && value.TryGetValue<string>(out var token) && token.Length > 0)
                return new ClaudeCredentialResult.Token(token);
        }
        catch (System.Text.Json.JsonException) { }
        return new ClaudeCredentialResult.Missing();
    }
}

public static class ClaudeUsagePolicy
{
    public static readonly TimeSpan FetchInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan CredentialsInterval = TimeSpan.FromSeconds(1800);
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    public static bool ShouldReadCredentials(DateTimeOffset? lastRead, DateTimeOffset now) =>
        lastRead is not { } read || now - read > CredentialsInterval;

    /// <summary>"resets 2 h 10 m", "resets 35 m", "resets soon"; nothing without a date.</summary>
    public static string ResetText(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until is not { } resetsAt) return "";
        var seconds = (int)Math.Floor((resetsAt - now).TotalSeconds);
        if (seconds < 60) return "resets soon";
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return hours > 0 ? $"resets {hours} h {minutes} m" : $"resets {minutes} m";
    }
}

/// <summary>
/// One per app. <c>transport</c> performs the GET with the bearer token and returns the body and the HTTP status, or
/// null when the network failed. Start and stop it on the UI thread.
/// </summary>
public sealed class ClaudeUsageStore(IClaudeCredentialReader credentials, Func<string, CancellationToken, Task<(string Body, int Status)?>> transport,
                                     Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.Now);
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private bool isEnabled;
    private int watchers;
    private CancellationTokenSource? fetching;
    private string? token;
    private DateTimeOffset? credentialsReadAt;
    private bool credentialsRefused;
    private ClaudeUsage? usage;

    public ClaudeUsageState State { get; private set; } = new ClaudeUsageState.None();

    public event Action? Changed;

    /// <summary>The real one: Claude Code's credentials file and a plain GET.</summary>
    public static ClaudeUsageStore Live() => new(new FileClaudeCredentialReader(), async (accessToken, cancellationToken) =>
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeUsagePolicy.Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await HttpClients.Shared.SendAsync(request, timeout.Token);
            return (await response.Content.ReadAsStringAsync(timeout.Token), (int)response.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    });

    /// <summary>The plugin's switch: off forgets the token, the refusal and the figures.</summary>
    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled) return;
        isEnabled = enabled;
        if (!enabled)
        {
            token = null;
            credentialsReadAt = null;
            credentialsRefused = false;
            usage = null;
            SetState(new ClaudeUsageState.None());
        }
        RestartFetching();
    }

    public void BeginWatching()
    {
        watchers++;
        if (watchers != 1) return;
        if (isEnabled) _ = RefreshNowAsync();
        RestartFetching();
    }

    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers == 0) RestartFetching();
    }

    /// <summary>One fetch: the credentials when due, then the endpoint.</summary>
    public async Task RefreshNowAsync()
    {
        if (!isEnabled) return;
        if (credentialsRefused)
        {
            SetState(new ClaudeUsageState.CredentialsRefused());
            return;
        }
        if (ClaudeUsagePolicy.ShouldReadCredentials(credentialsReadAt, now()))
        {
            credentialsReadAt = now();
            // Off the UI thread: the profile may sit on a slow or roaming drive.
            var result = await Task.Run(credentials.ReadAccessToken);
            switch (result)
            {
                case ClaudeCredentialResult.Token found:
                    token = found.Value;
                    break;
                case ClaudeCredentialResult.Refused:
                    token = null;
                    credentialsRefused = true;
                    AppLog.Write("coding agents: Claude Code's sign-in could not be read");
                    SetState(new ClaudeUsageState.CredentialsRefused());
                    return;
                default:
                    token = null;
                    break;
            }
        }
        if (token is not { } accessToken)
        {
            SetState(new ClaudeUsageState.SignInNeeded());
            return;
        }
        if (await transport(accessToken, CancellationToken.None) is not var (body, status))
        {
            SetState(StaleOrNone());
            return;
        }
        switch (status)
        {
            case 200:
                if (ClaudeUsage.Parse(body) is { } parsed)
                {
                    usage = parsed;
                    SetState(new ClaudeUsageState.Figures(parsed, IsStale: false));
                }
                else
                {
                    SetState(StaleOrNone());
                }
                break;
            case 401:
                token = null;
                SetState(new ClaudeUsageState.SignInNeeded());
                break;
            default:
                SetState(StaleOrNone());
                break;
        }
    }

    private ClaudeUsageState StaleOrNone() => usage is { } last ? new ClaudeUsageState.Figures(last, IsStale: true) : new ClaudeUsageState.None();

    private void SetState(ClaudeUsageState state)
    {
        if (State == state) return;
        State = state;
        Changed?.Invoke();
    }

    private void RestartFetching()
    {
        fetching?.Cancel();
        fetching = null;
        if (!isEnabled || watchers == 0) return;
        var cancellation = new CancellationTokenSource();
        fetching = cancellation;
        _ = FetchLoopAsync(cancellation.Token);
    }

    private async Task FetchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(ClaudeUsagePolicy.FetchInterval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            await RefreshNowAsync();
        }
    }
}
