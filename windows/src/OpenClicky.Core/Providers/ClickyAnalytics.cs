// Ports ClickyAnalytics.swift: every analytics event name and property in one place, so the
// instrumentation is consistent and easy to audit. Analytics are opt-in: nothing is sent unless a
// PostHog key is configured (AppBundleConfiguration "PostHogAPIKey", empty by default); then each
// event is one POST to PostHog's /capture/ endpoint (no SDK). Without a key every call is a no-op.
//
// Never send verbatim user speech or AI replies here — only non-content signals (that a turn
// happened, its length in characters).

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Providers;

public static class ClickyAnalytics
{
    public const string DefaultHost = "https://us.i.posthog.com";
    private const string DistinctIdKey = "openclicky.analytics.distinctId";

    private static readonly object ConfigurationLock = new();
    private static string? apiKey;
    private static string host = DefaultHost;
    private static string? distinctId;

    public static bool IsEnabled
    {
        get { lock (ConfigurationLock) return apiKey is not null; }
    }

    /// <summary>Sets up PostHog when a key is configured; otherwise analytics stay off.</summary>
    public static void Configure(Preferences? preferences = null)
    {
        var configuredKey = AppBundleConfiguration.StringValue("PostHogAPIKey");
        if (configuredKey is null)
        {
            AppLog.Write("analytics: disabled (no PostHogAPIKey configured)");
            return;
        }
        preferences ??= Preferences.Shared;
        var storedId = preferences.GetString(DistinctIdKey);
        if (string.IsNullOrEmpty(storedId))
        {
            storedId = Guid.NewGuid().ToString();
            preferences.Set(DistinctIdKey, storedId);
        }
        lock (ConfigurationLock)
        {
            apiKey = configuredKey;
            host = (AppBundleConfiguration.StringValue("PostHogHost") ?? DefaultHost).TrimEnd('/');
            distinctId = storedId;
        }
    }

    /// <summary>The body PostHog's capture endpoint takes; null while analytics are off.</summary>
    internal static JsonObject? CaptureBody(string eventName, IReadOnlyDictionary<string, object?>? properties)
    {
        string? key, id;
        lock (ConfigurationLock) { key = apiKey; id = distinctId; }
        if (key is null || id is null) return null;
        var propertyObject = new JsonObject { ["$lib"] = "openclicky-windows" };
        if (properties is not null)
        {
            foreach (var (name, value) in properties) propertyObject[name] = JsonSerializer.SerializeToNode(value);
        }
        return new JsonObject
        {
            ["api_key"] = key,
            ["event"] = eventName,
            ["distinct_id"] = id,
            ["properties"] = propertyObject,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>Sends one event, fire and forget. A no-op until `Configure` found a key.</summary>
    public static void Capture(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var body = CaptureBody(eventName, properties);
        if (body is null) return;
        string captureHost;
        lock (ConfigurationLock) captureHost = host;
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{captureHost}/capture/")
                {
                    Content = DirectProviderRequests.JsonContent(body),
                };
                using var response = await HttpClients.Shared.SendAsync(request).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                AppLog.Write($"analytics: capture failed: {error.Message}");
            }
        });
    }

    // MARK: - App lifecycle

    /// <summary>Fired once on every app launch.</summary>
    public static void TrackAppOpened() =>
        Capture("app_opened", new Dictionary<string, object?> { ["app_version"] = AppBundleConfiguration.ShortVersionString });

    // MARK: - Onboarding

    public static void TrackOnboardingStarted() => Capture("onboarding_started");
    public static void TrackOnboardingReplayed() => Capture("onboarding_replayed");
    public static void TrackOnboardingVideoCompleted() => Capture("onboarding_video_completed");
    public static void TrackOnboardingDemoTriggered() => Capture("onboarding_demo_triggered");

    // MARK: - Permissions

    public static void TrackAllPermissionsGranted() => Capture("all_permissions_granted");

    public static void TrackPermissionGranted(string permission) =>
        Capture("permission_granted", new Dictionary<string, object?> { ["permission"] = permission });

    // MARK: - Voice interaction

    /// <summary>The push-to-talk shortcut (ctrl + windows) was pressed.</summary>
    public static void TrackPushToTalkStarted() => Capture("push_to_talk_started");

    /// <summary>The shortcut was released — the transcript is being finalized.</summary>
    public static void TrackPushToTalkReleased() => Capture("push_to_talk_released");

    /// <summary>The user's message is being sent. Only the character count, never the transcript.</summary>
    public static void TrackUserMessageSent(int characterCount) =>
        Capture("user_message_sent", UserMessageSentProperties(characterCount));

    /// <summary>The answer arrived. Only the character count, never the response text.</summary>
    public static void TrackAIResponseReceived(int characterCount) =>
        Capture("ai_response_received", AIResponseReceivedProperties(characterCount));

    /// <summary>A pure function so a test can assert no key here ever carries message content.</summary>
    public static IReadOnlyDictionary<string, object?> UserMessageSentProperties(int characterCount) =>
        new Dictionary<string, object?> { ["character_count"] = characterCount };

    /// <summary>See `UserMessageSentProperties`: the same, for the AI's side of the turn.</summary>
    public static IReadOnlyDictionary<string, object?> AIResponseReceivedProperties(int characterCount) =>
        new Dictionary<string, object?> { ["character_count"] = characterCount };

    /// <summary>The answer pointed at a UI element.</summary>
    public static void TrackElementPointed(string? elementLabel) =>
        Capture("element_pointed", new Dictionary<string, object?> { ["element_label"] = elementLabel ?? "unknown" });

    // MARK: - Errors

    public static void TrackResponseError(string error) =>
        Capture("response_error", new Dictionary<string, object?> { ["error"] = error });

    public static void TrackTTSError(string error) =>
        Capture("tts_error", new Dictionary<string, object?> { ["error"] = error });
}
