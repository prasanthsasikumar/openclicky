// A question, or its answer, that the provider's own safety filter stopped. Asking again gets the same
// block, so it is not "something went wrong, please ask again": the ask lane says plainly that a filter
// stopped it and offers to explain the ideas behind it (AskLaneFailures.SafetyBlockSentence). Mirrors
// macos/OpenClicky/OpenClicky/SafetyBlock.swift.

namespace OpenClicky.Core.Answers;

/// <summary>
/// The provider's safety filter stopped the question or its answer. <see cref="Reason"/> is the
/// provider's own word for it ("Gemini SAFETY", "content_filter", "refusal"), for the log only.
/// </summary>
public sealed class SafetyBlockException(string reason) : Exception($"a safety filter stopped the answer ({reason})")
{
    public string Reason { get; } = reason;
}

public static class SafetyBlock
{
    /// <summary>
    /// Gemini's `finishReason`s that mean its filters ended the answer. RECITATION (it would have quoted
    /// a source word for word) and MAX_TOKENS are not safety, and neither is a plain STOP.
    /// </summary>
    private static readonly HashSet<string> GeminiFinishReasons =
        ["SAFETY", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "IMAGE_SAFETY"];

    /// <summary>Gemini's `finishReason` stopped the answer for safety.</summary>
    public static bool IsGeminiSafetyFinish(string finishReason) => GeminiFinishReasons.Contains(finishReason);

    /// <summary>Anthropic's `stop_reason` when its safety classifier stopped the answer.</summary>
    public const string AnthropicStopReason = "refusal";

    /// <summary>OpenAI's (and compatible services') `finish_reason` when its content filter stopped the answer.</summary>
    public const string OpenAIFinishReason = "content_filter";
}
