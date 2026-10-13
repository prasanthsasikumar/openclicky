// Port of macos/OpenClicky/OpenClicky/ClaudeCodeAnswerProvider.swift
//
// Answers on the person's Claude plan through the Claude Code CLI, for one turn, with no tools:
//
//   claude -p --input-format stream-json --output-format stream-json --verbose
//          --include-partial-messages --tools "" --permission-mode dontAsk --permission-prompts none
//          --system-prompt <the request's system prompt> --no-session-persistence --safe-mode
//          --strict-mcp-config --disable-slash-commands [--model <model>]
//
// and one stream-json user message on stdin whose content is the earlier turns as text, each
// screenshot as its label plus a base64 image block, and the question. Verified on the Mac against
// Claude Code 2.1.296 (2026-10-11): a red square comes back as "red" in about 3.5 s.
//
// Why these flags:
//   - `--system-prompt` replaces Claude Code's own coding-agent prompt rather than appending to it
//     (`--append-system-prompt`): that prompt is several thousand tokens about repositories and
//     tools, which slows the answer and pulls it away from OpenClicky's voice.
//   - `--tools ""` removes every built-in tool, `--strict-mcp-config` (with no `--mcp-config`)
//     loads no MCP servers, `--safe-mode` skips the person's hooks, plugins and CLAUDE.md, and
//     `dontAsk` + `--permission-prompts none` deny anything that would still ask. The answer can
//     only come from the model.
//   - stream-json input is the one way `-p` accepts images; stream-json output with partial
//     messages gives `text_delta` events, so the answer can be spoken while it arrives.
//   - The request's quick-action tools have no CLI equivalent: they are described in the system
//     prompt and asked for as [ACTION: …] lines (PlanToolProtocol), which are kept out of the
//     streamed text and returned as tool calls. MaxTokens has no equivalent and is not sent.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public sealed class ClaudeCodeAnswerProvider(
    string executablePath,
    string? model = null,
    IPlanCliProcessRunner? processRunner = null,
    double timeoutSeconds = 90) : IAnswerProvider
{
    public string ExecutablePath { get; } = executablePath;
    /// <summary>`--model`: an alias ("sonnet", "opus", "haiku") or a full model name; null = the CLI's default.</summary>
    public string? Model { get; } = model;
    public double TimeoutSeconds { get; } = timeoutSeconds;
    private readonly IPlanCliProcessRunner processRunner = processRunner ?? new PlanCliProcessRunner();

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, Action<string> onTextChunk, CancellationToken cancellationToken = default)
    {
        string scratchWorkingDirectory;
        try
        {
            scratchWorkingDirectory = PlanCLIs.MakeScratchWorkingDirectory();
        }
        catch (Exception)
        {
            throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0, "Couldn't make a scratch folder to run Claude Code in.");
        }
        try
        {
            var streamParser = new ClaudeCodeStreamParser();
            // With quick actions offered, an [ACTION: …] line must never reach the voice.
            var actionLineFilter = request.Tools.Count == 0 ? null : new PlanActionLineFilter();
            void HandOnTextDelta(string textDelta)
            {
                if (actionLineFilter is null) { onTextChunk(textDelta); return; }
                var safeText = actionLineFilter.Consume(textDelta);
                if (safeText.Length > 0) onTextChunk(safeText);
            }
            PlanCliProcessOutput output;
            try
            {
                output = await processRunner.RunAsync(
                    ExecutablePath,
                    CommandLineArguments(Model, PlanToolProtocol.SystemPrompt(request.SystemPrompt, request.Tools)),
                    StandardInputMessage(request),
                    scratchWorkingDirectory,
                    PlanCLIs.ProcessEnvironment(ExecutablePath),
                    TimeoutSeconds,
                    chunk => { foreach (var textDelta in streamParser.Consume(chunk)) HandOnTextDelta(textDelta); },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0, $"Couldn't start Claude Code at {ExecutablePath}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var textDelta in streamParser.Finish()) HandOnTextDelta(textDelta);
            if (actionLineFilter is not null)
            {
                var heldBackText = actionLineFilter.Finish();
                if (heldBackText.Length > 0) onTextChunk(heldBackText);
            }

            var answerText = AnswerText(streamParser.Summary, output, TimeoutSeconds);
            if (request.Tools.Count == 0) return new AnswerResult(answerText, []);
            var (spokenText, toolCalls) = PlanToolProtocol.Parse(answerText, request.Tools.Select(tool => tool.Name).ToHashSet());
            return new AnswerResult(spokenText, toolCalls);
        }
        finally
        {
            PlanCLIs.RemoveScratchWorkingDirectory(scratchWorkingDirectory);
        }
    }

    // MARK: building the call

    public static IReadOnlyList<string> CommandLineArguments(string? model, string systemPrompt)
    {
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            // stream-json output requires --verbose in print mode.
            "--verbose",
            "--include-partial-messages",
            "--tools", "",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            "--system-prompt", systemPrompt,
            "--no-session-persistence",
            "--safe-mode",
            "--strict-mcp-config",
            "--disable-slash-commands",
        };
        if (model?.Trim() is { Length: > 0 } trimmedModel) arguments.AddRange(["--model", trimmedModel]);
        return arguments;
    }

    /// <summary>Earlier turns as plain text, because a one-turn `-p` run has no conversation to resume.</summary>
    public static string? HistoryText(IReadOnlyList<AnswerRequest.Turn> history)
    {
        if (history.Count == 0) return null;
        var lines = new List<string> { "Earlier in this conversation:" };
        foreach (var turn in history)
        {
            lines.Add($"User: {turn.User}");
            lines.Add($"You: {turn.Assistant}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The content blocks of the one user message, in the Anthropic Messages shape the CLI passes
    /// through: history text, then each screenshot's label followed by its image, then the question.
    /// Keys are written in sorted order (the Mac's `.sortedKeys`).
    /// </summary>
    public static JsonArray UserMessageContent(AnswerRequest request)
    {
        var contentBlocks = new JsonArray();
        if (HistoryText(request.History) is { } historyText)
        {
            contentBlocks.Add(new JsonObject { ["text"] = historyText, ["type"] = "text" });
        }
        foreach (var image in request.Images)
        {
            contentBlocks.Add(new JsonObject { ["text"] = image.Label, ["type"] = "text" });
            contentBlocks.Add(new JsonObject
            {
                ["source"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(image.Data),
                    ["media_type"] = image.MediaType,
                    ["type"] = "base64",
                },
                ["type"] = "image",
            });
        }
        contentBlocks.Add(new JsonObject { ["text"] = request.UserPrompt, ["type"] = "text" });
        return contentBlocks;
    }

    private static readonly JsonSerializerOptions StandardInputJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>One JSON line; the CLI answers it and exits when stdin closes.</summary>
    public static byte[] StandardInputMessage(AnswerRequest request)
    {
        var message = new JsonObject
        {
            ["message"] = new JsonObject { ["content"] = UserMessageContent(request), ["role"] = "user" },
            ["type"] = "user",
        };
        return Encoding.UTF8.GetBytes(message.ToJsonString(StandardInputJsonOptions) + "\n");
    }

    // MARK: reading the result

    /// <summary>The answer, or an AnswerProviderException with a sentence the ask lane can say.</summary>
    public static string AnswerText(ClaudeCodeStreamSummary summary, PlanCliProcessOutput output, double timeoutSeconds)
    {
        if (output.TimedOut)
        {
            throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0, $"Claude Code took longer than {(int)timeoutSeconds} seconds to answer.");
        }
        if (summary.FailureText is { } failureText)
        {
            throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0, HumanSentence(failureText));
        }
        if (output.ExitStatus != 0)
        {
            var reason = PlanCLIs.LastNonEmptyLine(output.StandardErrorText);
            throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0,
                reason is not null ? HumanSentence(reason) : $"Claude Code stopped with exit status {output.ExitStatus}.");
        }
        var answerText = (summary.ResultText ?? summary.StreamedText).Trim();
        if (answerText.Length == 0)
        {
            throw new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0, "Claude Code finished without an answer.");
        }
        return answerText;
    }

    /// <summary>
    /// The CLI's own failure text is usually fine to show; the sign-in one names a slash command
    /// the person cannot type here, so it gets a sentence that says what to do instead.
    /// </summary>
    public static string HumanSentence(string failureText)
    {
        var trimmedFailureText = failureText.Trim();
        var lowercasedFailureText = trimmedFailureText.ToLowerInvariant();
        if (lowercasedFailureText.Contains("not logged in") || lowercasedFailureText.Contains("/login")
            || lowercasedFailureText.Contains("authentication_failed"))
        {
            return "Claude Code is signed out. Open a terminal, run claude, and sign in with your Claude account.";
        }
        if (lowercasedFailureText.Contains("rate_limit") || lowercasedFailureText.Contains("usage limit"))
        {
            return "Your Claude plan has hit its usage limit for now.";
        }
        return trimmedFailureText.Length == 0 ? "Claude Code couldn't answer." : $"Claude Code couldn't answer: {trimmedFailureText}";
    }
}

/// <summary>What the stream said once it ended.</summary>
public sealed record ClaudeCodeStreamSummary
{
    /// <summary>Every `text_delta` joined, in order.</summary>
    public string StreamedText { get; init; } = "";
    /// <summary>The final `result` event's text, when it succeeded.</summary>
    public string? ResultText { get; init; }
    /// <summary>
    /// Set when the run failed: the result event's text (`is_error`), or an assistant message's
    /// `error` code ("authentication_failed") when no result came.
    /// </summary>
    public string? FailureText { get; init; }
}

/// <summary>
/// Turns `claude -p --output-format stream-json --include-partial-messages` output into text
/// deltas and a summary. Fed from the runner's reader thread, so it locks its own state.
/// </summary>
public sealed class ClaudeCodeStreamParser
{
    private readonly object gate = new();
    private readonly List<byte> bufferedBytes = [];
    private ClaudeCodeStreamSummary state = new();
    private bool sawTextDelta;
    private string assistantMessageText = "";
    private string? assistantErrorCode;

    /// <summary>Consumes a piece of stdout and returns the text deltas from the complete lines in it.</summary>
    public IReadOnlyList<string> Consume(byte[] chunk)
    {
        lock (gate)
        {
            bufferedBytes.AddRange(chunk);
            var textDeltas = new List<string>();
            int newlineIndex;
            while ((newlineIndex = bufferedBytes.IndexOf((byte)'\n')) >= 0)
            {
                var lineData = bufferedBytes.GetRange(0, newlineIndex).ToArray();
                bufferedBytes.RemoveRange(0, newlineIndex + 1);
                if (HandleLine(lineData) is { } textDelta) textDeltas.Add(textDelta);
            }
            return textDeltas;
        }
    }

    public IReadOnlyList<string> Consume(string text) => Consume(Encoding.UTF8.GetBytes(text));

    /// <summary>Handles a last line with no newline after it.</summary>
    public IReadOnlyList<string> Finish()
    {
        lock (gate)
        {
            var textDeltas = new List<string>();
            if (bufferedBytes.Count > 0 && HandleLine(bufferedBytes.ToArray()) is { } textDelta) textDeltas.Add(textDelta);
            bufferedBytes.Clear();
            return textDeltas;
        }
    }

    public ClaudeCodeStreamSummary Summary
    {
        get
        {
            lock (gate)
            {
                var finishedSummary = state;
                // Without partial messages (or if they were dropped) the whole assistant message is
                // the only copy of the text.
                if (!sawTextDelta && finishedSummary.StreamedText.Length == 0) finishedSummary = finishedSummary with { StreamedText = assistantMessageText };
                if (finishedSummary.FailureText is null && finishedSummary.ResultText is null && assistantErrorCode is not null)
                {
                    finishedSummary = finishedSummary with { FailureText = assistantMessageText.Length == 0 ? assistantErrorCode : assistantMessageText };
                }
                return finishedSummary;
            }
        }
    }

    private string? HandleLine(byte[] lineData)
    {
        if (JsonHelpers.TryParse(lineData) is not JsonObject streamEvent || JsonHelpers.String(streamEvent["type"]) is not { } eventType) return null;
        // Subagent output would carry a parent tool id; with no tools there is none, but it is
        // never part of the answer.
        if (JsonHelpers.String(streamEvent["parent_tool_use_id"]) is { Length: > 0 }) return null;

        switch (eventType)
        {
            case "stream_event":
                if (streamEvent["event"] is not JsonObject innerEvent
                    || JsonHelpers.String(innerEvent["type"]) != "content_block_delta"
                    || innerEvent["delta"] is not JsonObject delta
                    || JsonHelpers.String(delta["type"]) != "text_delta"
                    || JsonHelpers.String(delta["text"]) is not { Length: > 0 } text) return null;
                sawTextDelta = true;
                state = state with { StreamedText = state.StreamedText + text };
                return text;
            case "assistant":
                if (JsonHelpers.String(streamEvent["error"]) is { Length: > 0 } errorCode) assistantErrorCode = errorCode;
                if (streamEvent["message"] is JsonObject message && message["content"] is JsonArray contentBlocks)
                {
                    foreach (var contentBlock in contentBlocks.OfType<JsonObject>())
                    {
                        if (JsonHelpers.String(contentBlock["type"]) == "text") assistantMessageText += JsonHelpers.String(contentBlock["text"]) ?? "";
                    }
                }
                return null;
            case "result":
                var resultText = JsonHelpers.String(streamEvent["result"]);
                var isError = JsonHelpers.Bool(streamEvent["is_error"]) ?? false;
                var subtype = JsonHelpers.String(streamEvent["subtype"]) ?? "";
                if (isError || subtype.StartsWith("error", StringComparison.Ordinal))
                {
                    var errorsText = streamEvent["errors"] is JsonArray errors && errors.All(error => JsonHelpers.String(error) is not null)
                        ? string.Join(" ", errors.Select(error => JsonHelpers.String(error)))
                        : null;
                    state = state with
                    {
                        FailureText = new[] { resultText, errorsText, subtype }.FirstOrDefault(text => !string.IsNullOrEmpty(text)) ?? "unknown error",
                    };
                }
                else
                {
                    state = state with { ResultText = resultText };
                }
                return null;
            default:
                return null;
        }
    }
}
