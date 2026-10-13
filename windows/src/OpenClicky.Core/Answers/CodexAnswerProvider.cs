// Port of macos/OpenClicky/OpenClicky/CodexAnswerProvider.swift
//
// Answers on the person's ChatGPT plan through Codex, for one turn, with its tools switched off:
//
//   codex exec --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules
//              --sandbox read-only --color never --json
//              --disable shell_tool --disable unified_exec ... (see `DisabledFeatures`)
//              -c web_search="disabled" -c model_reasoning_effort="low"
//              -c developer_instructions="<the request's system prompt>"
//              -C <scratch dir> -i <image files...> -o <scratch dir>\last-message.txt
//              [-m <model>] -
//
// with the earlier turns, the image labels and the question on stdin. Verified on the Mac against
// codex-cli 0.152.1 (2026-10-11): a red square comes back as "red" in about 6 s.
//
// Why these flags:
//   - It uses the person's own login: CODEX_HOME is removed from the environment so the default
//     %USERPROFILE%\.codex is read, never OpenClicky's agent home.
//   - `--ignore-user-config` skips config.toml (auth still comes from CODEX_HOME). With the
//     person's config, plugins and skills loaded, a one-word answer took 34 s and ran shell
//     commands to read skill files; without it, 6 s and no commands.
//   - The disabled features and the read-only sandbox leave nothing for the agent to do but
//     answer. Low reasoning effort keeps a spoken answer quick.
//   - `developer_instructions` is Codex's slot for instructions on top of its own, the closest it
//     has to a system prompt; it is passed as a TOML string because `-c` values are parsed as TOML.
//   - `--output-last-message` writes exactly the final answer; `--json` events on stdout carry
//     the error message when a turn fails. Codex does not stream text here, so `onTextChunk`
//     gets the whole answer once.
//   - The request's quick-action tools have no CLI equivalent: they are described in the
//     developer instructions and asked for as [ACTION: …] lines (PlanToolProtocol), which are
//     taken out of the answer and returned as tool calls. MaxTokens is not sent.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public sealed class CodexAnswerProvider(
    string executablePath,
    string? model = null,
    IPlanCliProcessRunner? processRunner = null,
    double timeoutSeconds = 120) : IAnswerProvider
{
    public string ExecutablePath { get; } = executablePath;
    /// <summary>`-m`: e.g. "gpt-5.5"; null = Codex's default for a ChatGPT sign-in.</summary>
    public string? Model { get; } = model;
    public double TimeoutSeconds { get; } = timeoutSeconds;
    private readonly IPlanCliProcessRunner processRunner = processRunner ?? new PlanCliProcessRunner();

    /// <summary>Agent capabilities that are on by default and have no place in a one-turn answer.</summary>
    public static readonly IReadOnlyList<string> DisabledFeatures =
    [
        "shell_tool", "unified_exec", "plugins", "apps", "multi_agent", "image_generation",
        "browser_use", "computer_use", "skill_search", "hooks", "view_image",
    ];

    public const string OutputFileName = "last-message.txt";

    public async Task<AnswerResult> AnswerAsync(AnswerRequest request, Action<string> onTextChunk, CancellationToken cancellationToken = default)
    {
        string scratchWorkingDirectory;
        try
        {
            scratchWorkingDirectory = PlanCLIs.MakeScratchWorkingDirectory();
        }
        catch (Exception)
        {
            throw new AnswerProviderException(AnswerSource.CodexPlan, 0, "Couldn't make a scratch folder to run Codex in.");
        }
        // The screenshots and the answer file live in the scratch folder, so removing it cleans up all.
        try
        {
            IReadOnlyList<string> imageFilePaths;
            try
            {
                imageFilePaths = WriteImageFiles(request.Images, scratchWorkingDirectory);
            }
            catch (Exception)
            {
                throw new AnswerProviderException(AnswerSource.CodexPlan, 0, "Couldn't save the screenshot for Codex.");
            }
            var outputFilePath = Path.Combine(scratchWorkingDirectory, OutputFileName);

            PlanCliProcessOutput output;
            try
            {
                output = await processRunner.RunAsync(
                    ExecutablePath,
                    CommandLineArguments(Model, PlanToolProtocol.SystemPrompt(request.SystemPrompt, request.Tools),
                                         imageFilePaths, outputFilePath, scratchWorkingDirectory),
                    Encoding.UTF8.GetBytes(PromptText(request)),
                    scratchWorkingDirectory,
                    PlanCLIs.ProcessEnvironment(ExecutablePath),
                    TimeoutSeconds,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new AnswerProviderException(AnswerSource.CodexPlan, 0, $"Couldn't start Codex at {ExecutablePath}.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            string? outputFileContents;
            try { outputFileContents = File.ReadAllText(outputFilePath, Encoding.UTF8); }
            catch (Exception) { outputFileContents = null; }
            var answerText = AnswerText(outputFileContents, output, TimeoutSeconds);
            if (request.Tools.Count == 0)
            {
                onTextChunk(answerText);
                return new AnswerResult(answerText, []);
            }
            var (spokenText, toolCalls) = PlanToolProtocol.Parse(answerText, request.Tools.Select(tool => tool.Name).ToHashSet());
            if (spokenText.Length > 0) onTextChunk(spokenText);
            return new AnswerResult(spokenText, toolCalls);
        }
        finally
        {
            PlanCLIs.RemoveScratchWorkingDirectory(scratchWorkingDirectory);
        }
    }

    // MARK: building the call

    public static IReadOnlyList<string> CommandLineArguments(string? model, string systemPrompt, IReadOnlyList<string> imageFilePaths,
                                                             string outputFilePath, string workingDirectoryPath)
    {
        var arguments = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--sandbox", "read-only",
            "--color", "never",
            "--json",
        };
        foreach (var featureName in DisabledFeatures) arguments.AddRange(["--disable", featureName]);
        arguments.AddRange([
            "-c", "web_search=\"disabled\"",
            "-c", "model_reasoning_effort=\"low\"",
        ]);
        if (systemPrompt.Trim().Length > 0) arguments.AddRange(["-c", $"developer_instructions={TomlBasicString(systemPrompt)}"]);
        if (model?.Trim() is { Length: > 0 } trimmedModel) arguments.AddRange(["-m", trimmedModel]);
        arguments.AddRange(["-C", workingDirectoryPath]);
        foreach (var imageFilePath in imageFilePaths) arguments.AddRange(["-i", imageFilePath]);
        arguments.AddRange(["-o", outputFilePath]);
        // "-" reads the prompt from stdin: no length limit, and it never shows up in a process list.
        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    /// A TOML basic string ("..."), escaping what TOML requires: backslash, quote and control
    /// characters. Unescaped, a system prompt with a quote or a new line would fail to parse and
    /// Codex would take the raw text, quotes and all.
    /// </summary>
    public static string TomlBasicString(string text)
    {
        var escapedText = new StringBuilder("\"");
        foreach (var scalar in text.EnumerateRunes())
        {
            switch (scalar.Value)
            {
                case '\\': escapedText.Append("\\\\"); break;
                case '"': escapedText.Append("\\\""); break;
                case '\n': escapedText.Append("\\n"); break;
                case '\r': escapedText.Append("\\r"); break;
                case '\t': escapedText.Append("\\t"); break;
                default:
                    if (scalar.Value < 0x20 || scalar.Value == 0x7F) escapedText.Append("\\u").Append(scalar.Value.ToString("X4", CultureInfo.InvariantCulture));
                    else escapedText.Append(scalar.ToString());
                    break;
            }
        }
        return escapedText.Append('"').ToString();
    }

    /// <summary>
    /// The stdin prompt: earlier turns, what each attached image is (Codex attaches them in `-i`
    /// order without their labels), then the question.
    /// </summary>
    public static string PromptText(AnswerRequest request)
    {
        var sections = new List<string>();
        if (ClaudeCodeAnswerProvider.HistoryText(request.History) is { } historyText) sections.Add(historyText);
        if (request.Images.Count > 0)
        {
            var imageLines = new List<string> { "Attached images, in order:" };
            for (var imageIndex = 0; imageIndex < request.Images.Count; imageIndex++)
            {
                imageLines.Add($"{imageIndex + 1}. {request.Images[imageIndex].Label}");
            }
            sections.Add(string.Join("\n", imageLines));
        }
        sections.Add(request.UserPrompt);
        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// Writes each screenshot to the scratch folder with an extension matching its type, which is
    /// how Codex decides how to read it.
    /// </summary>
    public static IReadOnlyList<string> WriteImageFiles(IReadOnlyList<AnswerRequest.Image> images, string directory)
    {
        var imageFilePaths = new List<string>();
        for (var imageIndex = 0; imageIndex < images.Count; imageIndex++)
        {
            var image = images[imageIndex];
            var fileExtension = image.MediaType.ToLowerInvariant().Contains("png") ? "png" : "jpg";
            var imageFilePath = Path.Combine(directory, $"image-{imageIndex + 1}.{fileExtension}");
            File.WriteAllBytes(imageFilePath, image.Data);
            imageFilePaths.Add(imageFilePath);
        }
        return imageFilePaths;
    }

    // MARK: reading the result

    public static string AnswerText(string? outputFileContents, PlanCliProcessOutput output, double timeoutSeconds)
    {
        if (output.TimedOut)
        {
            throw new AnswerProviderException(AnswerSource.CodexPlan, 0, $"Codex took longer than {(int)timeoutSeconds} seconds to answer.");
        }
        if (output.ExitStatus != 0)
        {
            var reason = ErrorMessage(output.StandardOutputText) ?? PlanCLIs.LastNonEmptyLine(output.StandardErrorText);
            throw new AnswerProviderException(AnswerSource.CodexPlan, 0,
                reason is not null ? HumanSentence(reason) : $"Codex stopped with exit status {output.ExitStatus}.");
        }
        var fileAnswer = outputFileContents?.Trim() ?? "";
        var answerText = fileAnswer.Length == 0 ? LastAgentMessage(output.StandardOutputText) ?? "" : fileAnswer;
        if (answerText.Length == 0)
        {
            throw new AnswerProviderException(AnswerSource.CodexPlan, 0, "Codex finished without an answer.");
        }
        return answerText;
    }

    /// <summary>
    /// The last failure in `--json` output: a `turn.failed` event's error, else the last `error`
    /// event. The message is often the API's JSON error body itself
    /// (`{"type":"error","status":400,"error":{"message":"..."}}`), so the inner message is used.
    /// </summary>
    public static string? ErrorMessage(string jsonLines)
    {
        string? turnFailedMessage = null;
        string? lastErrorEventMessage = null;
        foreach (var codexEvent in JsonEvents(jsonLines))
        {
            switch (JsonHelpers.String(codexEvent["type"]))
            {
                case "turn.failed":
                    if (codexEvent["error"] is JsonObject error && JsonHelpers.String(error["message"]) is { } message) turnFailedMessage = message;
                    break;
                case "error":
                    if (JsonHelpers.String(codexEvent["message"]) is { } errorMessage) lastErrorEventMessage = errorMessage;
                    break;
            }
        }
        if ((turnFailedMessage ?? lastErrorEventMessage) is not { } rawMessage) return null;
        return InnerApiErrorMessage(rawMessage) ?? rawMessage;
    }

    public static string? LastAgentMessage(string jsonLines)
    {
        string? lastAgentText = null;
        foreach (var codexEvent in JsonEvents(jsonLines))
        {
            if (JsonHelpers.String(codexEvent["type"]) != "item.completed") continue;
            if (codexEvent["item"] is JsonObject item && JsonHelpers.String(item["type"]) == "agent_message"
                && JsonHelpers.String(item["text"]) is { Length: > 0 } text)
            {
                lastAgentText = text;
            }
        }
        return lastAgentText?.Trim();
    }

    private static IEnumerable<JsonObject> JsonEvents(string jsonLines) =>
        jsonLines.Split('\n', '\r').Where(line => line.Length > 0).Select(JsonHelpers.TryParse).OfType<JsonObject>();

    private static string? InnerApiErrorMessage(string rawMessage)
    {
        if (JsonHelpers.TryParse(rawMessage) is not JsonObject apiError) return null;
        if (apiError["error"] is JsonObject error && JsonHelpers.String(error["message"]) is { } message) return message;
        return JsonHelpers.String(apiError["message"]);
    }

    public static string HumanSentence(string failureText)
    {
        var trimmedFailureText = failureText.Trim();
        var lowercasedFailureText = trimmedFailureText.ToLowerInvariant();
        if (lowercasedFailureText.Contains("not logged in") || lowercasedFailureText.Contains("codex login")
            || lowercasedFailureText.Contains("401") || lowercasedFailureText.Contains("unauthorized"))
        {
            return "Codex is signed out. Open a terminal, run codex login, and sign in with ChatGPT.";
        }
        if (lowercasedFailureText.Contains("usage limit") || lowercasedFailureText.Contains("rate limit"))
        {
            return "Your ChatGPT plan has hit its Codex usage limit for now.";
        }
        return trimmedFailureText.Length == 0 ? "Codex couldn't answer." : $"Codex couldn't answer: {trimmedFailureText}";
    }
}
