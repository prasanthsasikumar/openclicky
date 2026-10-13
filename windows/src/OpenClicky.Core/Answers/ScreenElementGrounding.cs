// Wires Pointing/ScreenElementGrounder to ClaudeAPI, as macos/OpenClicky/OpenClicky/ScreenElementGrounder.swift
// does in `locate(label:text:userRequest:in:)`: the route ProviderRouter picks for Claude becomes a
// GroundingRoute (own Anthropic key: Claude at Anthropic directly; another answer key: its own
// provider; else the backend's /chat on the account), and the request goes through
// `ClaudeAPI("{backendBaseURL}/chat", model: ScreenElementGrounder.Model).AnalyzeImageAsync`.

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public static class ScreenElementGrounding
{
    /// <summary>The slice of a ProviderRoute grounding decides on.</summary>
    public static GroundingRoute GroundingRouteFor(ProviderRoute route) => route.Kind switch
    {
        ProviderRouteKind.Unavailable => new GroundingRoute.Unavailable(route.Sentence ?? ""),
        ProviderRouteKind.AnthropicDirect => new GroundingRoute.AnthropicDirect(),
        ProviderRouteKind.Backend => new GroundingRoute.Backend(),
        ProviderRouteKind.OpenAIDirect => new GroundingRoute.OpenAIDirect(),
        _ => new GroundingRoute.AnswerProvider(route.Source!.Value.DisplayName(),
                                               route.Source is AnswerSource.ClaudeCodePlan or AnswerSource.CodexPlan),
    };

    /// <summary>Why grounding is skipped on `route`; null when it can run (ScreenElementGrounder.ReasonGroundingCannotRun).</summary>
    public static string? ReasonGroundingCannotRun(ProviderRoute route) =>
        ScreenElementGrounder.ReasonGroundingCannotRun(GroundingRouteFor(route));

    /// <summary>ScreenElementGrounder's request sent through `api.AnalyzeImageAsync`; the reply's text.</summary>
    public static Func<GroundingRequest, Task<string>> AnalyzeImageThrough(ClaudeAPI api, CancellationToken cancellationToken = default) =>
        async request =>
        {
            var reply = await api.AnalyzeImageAsync([(request.ImageData, request.ImageLabel)], request.SystemPrompt, null,
                                                    request.UserPrompt, cancellationToken).ConfigureAwait(false);
            return reply.Text;
        };

    /// <summary>
    /// Asks Claude (or the answer source the router picks) where `label`/`text` is on the
    /// screenshot, on the route shell.json gives now. Null when nothing can answer (or only a
    /// plan), the request fails, or Claude answers [POINT:none]; app.log says when it could not ask.
    /// </summary>
    /// <param name="api">The client to ask; null builds one on the backend's /chat with the grounder's model.</param>
    public static Task<ScreenElementGrounder.Located?> LocateAsync(
        string label,
        string text,
        string? userRequest,
        byte[] imageData,
        int screenshotWidthInPixels,
        int screenshotHeightInPixels,
        ClaudeAPI? api = null,
        CancellationToken cancellationToken = default)
    {
        var route = GroundingRouteFor(ProviderRouter.Current(ProviderCapability.Claude));
        api ??= new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat", ScreenElementGrounder.Model);
        return ScreenElementGrounder.LocateAsync(label, text, userRequest, imageData, screenshotWidthInPixels, screenshotHeightInPixels,
                                                 route, AnalyzeImageThrough(api, cancellationToken), AppLog.Write);
    }
}
