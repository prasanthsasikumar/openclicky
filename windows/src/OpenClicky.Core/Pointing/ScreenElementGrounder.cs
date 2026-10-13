//
//  ScreenElementGrounder.cs
//  OpenClicky (Windows port of ScreenElementGrounder.swift)
//
//  `point_at` fallback for elements OCR cannot snap to (icons, symbols, captions the recognizer
//  missed). The Realtime model's own pixel guess is hundreds of pixels off for icons, but Claude
//  locates an element on the same 1280 px screenshot to within a few pixels (measured ≤ 8 px on
//  a "+" header button and a small "New" button, 2–4 s with Haiku 4.5), so the description the
//  Realtime model gave is handed to Claude (an own Anthropic key, or the backend's /chat proxy) or,
//  when another answer source is picked, to that source's AnswerProvider. The plans are skipped.
//
//  The HTTP call goes through an injected delegate (the Answers/Providers port supplies ClaudeAPI's
//  analyzeImage), so this file has no network code.
//

using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClicky.Core.Pointing;

/// <summary>What grounding needs to know about the provider route (a slice of ProviderRoute).</summary>
public abstract record GroundingRoute
{
    private GroundingRoute() { }

    public sealed record Unavailable(string Reason) : GroundingRoute;
    public sealed record AnthropicDirect : GroundingRoute;
    public sealed record Backend : GroundingRoute;
    public sealed record OpenAIDirect : GroundingRoute;
    /// <param name="DisplayName">The answer source's display name.</param>
    /// <param name="IsPlan">The Claude Code / Codex plans (answer through a slow CLI).</param>
    public sealed record AnswerProvider(string DisplayName, bool IsPlan) : GroundingRoute;
}

/// <summary>One image request: what ClaudeAPI.analyzeImage is called with on the Mac.</summary>
public sealed record GroundingRequest(string Model, byte[] ImageData, string ImageLabel, string SystemPrompt, string UserPrompt);

public static class ScreenElementGrounder
{
    /// <summary>Fast and as accurate as Sonnet on this task.</summary>
    public const string Model = "claude-haiku-4-5";

    public const string SystemPrompt =
        "You locate UI elements in screenshots and answer only with a [POINT:x,y:visible text] tag in the screenshot's pixel coordinates.";

    /// <summary>Where Claude put the element, and the text it says the element shows ("arXiv"), when it shows any. The
    /// text is what makes the answer precise: Claude is reliable about *which* element answers "how do I open this
    /// paper" and loose about its pixels — it put a paper's "arXiv" link 130 px too high, on the abstract — while OCR
    /// has that text's exact box.</summary>
    public sealed record Located(ScreenPoint Point, string? VisibleText);

    /// <summary>Asks Claude where <paramref name="label"/>/<paramref name="text"/> is on the screenshot. Null when nothing
    /// can answer (or only a plan), the request fails, or Claude answers [POINT:none]; the log says when it could not ask.</summary>
    /// <param name="analyzeImage">Sends the request and returns the reply text.
    /// TODO(integration): wire to the Answers port's ClaudeAPI(proxyURL: "{backendBaseURL}/chat", model: Model).AnalyzeImage.</param>
    public static async Task<Located?> LocateAsync(
        string label,
        string text,
        string? userRequest,
        byte[] imageData,
        int screenshotWidthInPixels,
        int screenshotHeightInPixels,
        GroundingRoute route,
        Func<GroundingRequest, Task<string>> analyzeImage,
        Action<string>? log = null)
    {
        if (ReasonGroundingCannotRun(route) is { } reason)
        {
            log?.Invoke($"point_at: no grounding ({reason})");
            return null;
        }
        var request = new GroundingRequest(
            Model,
            imageData,
            $"Screenshot of the user's screen, {screenshotWidthInPixels}×{screenshotHeightInPixels} px, origin top-left.",
            SystemPrompt,
            Prompt(label, text, userRequest, screenshotWidthInPixels, screenshotHeightInPixels));
        string reply;
        try
        {
            reply = await analyzeImage(request).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Only the type and HResult: a refusal's body could quote the request.
            log?.Invoke($"point_at: grounding failed ({error.GetType().Name} {error.HResult})");
            return null;
        }
        if (ParsePoint(reply, screenshotWidthInPixels, screenshotHeightInPixels) is not { } point) return null;
        return new Located(point, ParseVisibleText(reply));
    }

    /// <summary>Why grounding is skipped on <paramref name="route"/>; null when it can run. The plans answer through a CLI
    /// that takes several seconds to start, while the pointer is waiting mid-sentence, so they are skipped; the model's
    /// own guess (or OCR) stands instead.</summary>
    public static string? ReasonGroundingCannotRun(GroundingRoute route) => route switch
    {
        GroundingRoute.Unavailable => "nothing is set up to answer",
        GroundingRoute.AnswerProvider { IsPlan: true } provider => $"{provider.DisplayName} is too slow to locate an element mid-sentence",
        _ => null,
    };

    public static string Prompt(string label, string text, string? userRequest, int imageWidth, int imageHeight)
    {
        var lines = new List<string> { $"The screenshot is {imageWidth}×{imageHeight} px, origin top-left." };
        var element = $"Find the center of this UI element: {label}";
        if (text.Length > 0) element += $" (it shows the text \"{text}\")";
        lines.Add(element + ".");
        if (!string.IsNullOrEmpty(userRequest))
        {
            lines.Add($"The user asked: \"{userRequest}\", pick the element that answers that.");
        }
        lines.Add("Reply with only [POINT:x,y:visible text], where visible text is exactly what the element itself shows (a link that reads \"arXiv\" is arXiv, whatever it leads to), or [POINT:x,y] for an element with no text, or [POINT:none] if it is not visible.");
        return string.Join("\n", lines);
    }

    private static readonly Regex VisibleTextRegex = new(@"\[POINT:\s*\d+\s*,\s*\d+\s*:([^\]]*)\]", RegexOptions.CultureInvariant);
    private static readonly Regex PointRegex = new(@"\[POINT:\s*(\d+)\s*,\s*(\d+)\s*(?::[^\]]*)?\]", RegexOptions.CultureInvariant);

    /// <summary>The <c>:visible text</c> part of <c>[POINT:x,y:visible text]</c>; null when absent or blank.</summary>
    public static string? ParseVisibleText(string reply)
    {
        var match = VisibleTextRegex.Match(reply);
        if (!match.Success) return null;
        var visibleText = match.Groups[1].Value.Trim();
        return visibleText.Length == 0 ? null : visibleText;
    }

    /// <summary><c>[POINT:x,y]</c> (spaces allowed) inside the screenshot; null for <c>[POINT:none]</c>, no tag, or
    /// coordinates off the image.</summary>
    public static ScreenPoint? ParsePoint(string reply, double imageWidth, double imageHeight)
    {
        var match = PointRegex.Match(reply);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return null;
        if (x < 0 || y < 0 || x > imageWidth || y > imageHeight) return null;
        return new ScreenPoint(x, y);
    }
}
