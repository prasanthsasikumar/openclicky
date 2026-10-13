// Port of macos/OpenClicky/OpenClickyTests/AnswerProviderPlanTests.swift.
//
// The plan CLIs (Claude Code, Codex): how each call is built, how their output is read, how
// failures become sentences, and how the status checks read `claude auth status` / `codex login
// status`. A fake runner stands in for every process; nothing here starts a CLI, uses the network,
// or reads files outside the temporary folder. Windows: the Mac's login-shell lookup is a PATH +
// PATHEXT search, and paths are Windows paths. MacAction's tools are stood in by
// QuickActionToolDefinitions until the Actions port lands.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Answers;

/// <summary>The six quick actions in Anthropic's tool shape (stand-ins for DesktopActions' definitions).</summary>
internal static class QuickActionToolDefinitions
{
    private static JsonObject Tool(string name, string description, string schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = JsonNode.Parse(schema),
    };

    public static IReadOnlyList<JsonObject> All() =>
    [
        Tool("open_app", "Open an app on the person's PC.", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
        Tool("open_url", "Open a web address.", """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        Tool("create_folder", "Make a folder.", """{"type":"object","properties":{"name":{"type":"string"},"location":{"type":"string","enum":["desktop","documents","downloads"]}},"required":["name"]}"""),
        Tool("reveal_in_explorer", "Show a file in File Explorer.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        Tool("set_volume", "Set the volume.", """{"type":"object","properties":{"level":{"type":"integer"}},"required":["level"]}"""),
        Tool("media_control", "Play, pause or skip.", """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
    ];
}

/// <summary>
/// Records each call and answers with canned output. For Codex it writes the canned last message
/// to the `-o` path, the way the real CLI does.
/// </summary>
internal sealed class FakePlanCliProcessRunner(
    string[]? standardOutputChunks = null,
    string standardError = "",
    int exitStatus = 0,
    bool timedOut = false,
    string? lastMessageFileContents = null) : IPlanCliProcessRunner
{
    public sealed record RecordedCall(string ExecutablePath, IReadOnlyList<string> Arguments, byte[]? StandardInput,
                                      string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, double TimeoutSeconds);

    private readonly object gate = new();
    private readonly List<RecordedCall> recordedCalls = [];
    private bool workingDirectoryExistedDuringRun;
    private readonly string[] standardOutputChunks = standardOutputChunks ?? [];

    public IReadOnlyList<RecordedCall> Calls { get { lock (gate) return recordedCalls.ToList(); } }
    public bool SawWorkingDirectoryDuringRun { get { lock (gate) return workingDirectoryExistedDuringRun; } }

    public Task<PlanCliProcessOutput> RunAsync(string executablePath, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory,
                                               IReadOnlyDictionary<string, string> environment, double timeoutSeconds,
                                               Action<byte[]>? onStandardOutputChunk, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            recordedCalls.Add(new RecordedCall(executablePath, arguments, standardInput, workingDirectory, environment, timeoutSeconds));
            workingDirectoryExistedDuringRun = Directory.Exists(workingDirectory);
        }
        var outputFlagIndex = arguments.ToList().IndexOf("-o");
        if (lastMessageFileContents is not null && outputFlagIndex >= 0 && outputFlagIndex + 1 < arguments.Count)
        {
            File.WriteAllText(arguments[outputFlagIndex + 1], lastMessageFileContents);
        }
        foreach (var chunk in standardOutputChunks) onStandardOutputChunk?.Invoke(Encoding.UTF8.GetBytes(chunk));
        return Task.FromResult(new PlanCliProcessOutput(exitStatus, Encoding.UTF8.GetBytes(string.Concat(standardOutputChunks)),
                                                        Encoding.UTF8.GetBytes(standardError), timedOut));
    }
}

public class AnswerProviderPlanTests
{
    private const string FakeClaude = @"C:\fake\bin\claude.exe";
    private const string FakeCodex = @"C:\fake\bin\codex.exe";

    private static AnswerRequest SampleRequest(IEnumerable<AnswerRequest.Turn>? history = null) => new(
        [new AnswerRequest.Image([0x89, 0x50, 0x4E, 0x47], "Screen 1 of 1 (cursor here)", "image/png")],
        "answer in lowercase",
        history ?? [],
        "what color is this square? answer in one word",
        []);

    /// <summary>The sample request with the quick actions offered, as the ask lane sends it on a plan.</summary>
    private static AnswerRequest SampleRequestOfferingQuickActions()
    {
        var request = SampleRequest();
        request.UserPrompt = "make a folder called test-plan-actions on my desktop";
        request.Tools = QuickActionToolDefinitions.All().Select(ClaudeAPI.AnswerTool).OfType<AnswerRequest.Tool>().ToList();
        return request;
    }

    private static string JsonLine(JsonNode node) => node.ToJsonString() + "\n";

    private static string TextDeltaLine(string text) => JsonLine(new JsonObject
    {
        ["type"] = "stream_event",
        ["parent_tool_use_id"] = null,
        ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } },
    });

    private static string ResultLine(string result, bool isError = false) =>
        JsonLine(new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = isError, ["result"] = result });

    private static string? ValueAfter(string flag, IReadOnlyList<string> arguments)
    {
        var flagIndex = arguments.ToList().IndexOf(flag);
        return flagIndex >= 0 && flagIndex + 1 < arguments.Count ? arguments[flagIndex + 1] : null;
    }

    private static PlanCliProcessOutput Output(int exitStatus, string standardOutput = "", string standardError = "", bool timedOut = false) =>
        new(exitStatus, Encoding.UTF8.GetBytes(standardOutput), Encoding.UTF8.GetBytes(standardError), timedOut);

    private static void AssertProviderError(AnswerSource source, string message, Action action)
    {
        var error = Assert.Throws<AnswerProviderException>(action);
        Assert.Equal(new AnswerProviderException(source, 0, message), error);
    }

    // MARK: Claude Code — the call

    [Fact]
    public void ClaudeCodeRunsOneTurnWithNoToolsAndTheRequestsSystemPrompt()
    {
        var arguments = ClaudeCodeAnswerProvider.CommandLineArguments(null, "answer in lowercase");
        Assert.Equal("-p", arguments[0]);
        Assert.Equal("stream-json", ValueAfter("--input-format", arguments));
        Assert.Equal("stream-json", ValueAfter("--output-format", arguments));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("--include-partial-messages", arguments);
        Assert.Equal("", ValueAfter("--tools", arguments));
        Assert.Equal("dontAsk", ValueAfter("--permission-mode", arguments));
        Assert.Equal("none", ValueAfter("--permission-prompts", arguments));
        Assert.Equal("answer in lowercase", ValueAfter("--system-prompt", arguments));
        Assert.DoesNotContain("--append-system-prompt", arguments);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--safe-mode", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public void ClaudeCodePassesAModelOnlyWhenOneIsNamed()
    {
        Assert.Equal("sonnet", ValueAfter("--model", ClaudeCodeAnswerProvider.CommandLineArguments("sonnet", "")));
        Assert.DoesNotContain("--model", ClaudeCodeAnswerProvider.CommandLineArguments("  ", ""));
    }

    [Fact]
    public void ClaudeCodeSendsLabelledBase64ImagesAndHistoryInOneUserMessage()
    {
        var request = SampleRequest([new AnswerRequest.Turn("what is this app", "it's explorer")]);
        var messageData = ClaudeCodeAnswerProvider.StandardInputMessage(request);
        Assert.Equal((byte)'\n', messageData[^1]);
        var message = JsonNode.Parse(messageData)!.AsObject();
        Assert.Equal("user", message["type"]!.GetValue<string>());
        var innerMessage = message["message"]!.AsObject();
        Assert.Equal("user", innerMessage["role"]!.GetValue<string>());
        var contentBlocks = innerMessage["content"]!.AsArray();
        Assert.Equal(4, contentBlocks.Count);
        Assert.Contains("User: what is this app\nYou: it's explorer", contentBlocks[0]!["text"]!.GetValue<string>());
        Assert.Equal("Screen 1 of 1 (cursor here)", contentBlocks[1]!["text"]!.GetValue<string>());
        var imageSource = contentBlocks[2]!["source"]!;
        Assert.Equal("image", contentBlocks[2]!["type"]!.GetValue<string>());
        Assert.Equal("base64", imageSource["type"]!.GetValue<string>());
        Assert.Equal("image/png", imageSource["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), imageSource["data"]!.GetValue<string>());
        Assert.Equal("what color is this square? answer in one word", contentBlocks[3]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeCodeWithoutHistoryHasNoHistoryBlock()
    {
        var message = JsonNode.Parse(ClaudeCodeAnswerProvider.StandardInputMessage(SampleRequest()))!;
        Assert.Equal(3, message["message"]!["content"]!.AsArray().Count);
    }

    // MARK: Claude Code — reading the stream

    [Fact]
    public async Task ClaudeCodeStreamsTextDeltasAndReturnsTheResult()
    {
        var assistantLine = JsonLine(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "red square" }) },
        });
        var fullOutput = JsonLine(new JsonObject { ["type"] = "system", ["subtype"] = "init" }) + TextDeltaLine("red") + TextDeltaLine(" square")
                         + assistantLine + ResultLine("red square");
        // Split mid-line, as pipe reads do.
        var runner = new FakePlanCliProcessRunner([fullOutput[..70], fullOutput[70..]]);
        var provider = new ClaudeCodeAnswerProvider(FakeClaude, "haiku", runner);
        var recorder = new ChunkCollector();

        var result = await provider.AnswerAsync(SampleRequest(), recorder.Append);

        Assert.Equal(new AnswerResult("red square", []), result);
        Assert.Equal(["red", " square"], recorder.Chunks);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(FakeClaude, call.ExecutablePath);
        Assert.Equal(90, call.TimeoutSeconds);
        Assert.Equal("haiku", ValueAfter("--model", call.Arguments));
        Assert.Equal(ClaudeCodeAnswerProvider.StandardInputMessage(SampleRequest()), call.StandardInput);
        // A fresh scratch folder under the temporary folder, removed afterwards.
        Assert.StartsWith(Path.GetTempPath(), call.WorkingDirectory);
        Assert.True(runner.SawWorkingDirectoryDuringRun);
        Assert.False(Directory.Exists(call.WorkingDirectory));
        Assert.False(call.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.StartsWith(@"C:\fake\bin;", call.Environment["PATH"]);
    }

    [Fact]
    public async Task ClaudeCodeTurnsAnActionLineIntoAToolCallAndNeverStreamsIt()
    {
        const string actionLine = "[ACTION: create_folder {\"name\":\"test-plan-actions\",\"location\":\"desktop\"}]";
        const string fullAnswer = "making that folder now.\n" + actionLine;
        // The tag arrives split across deltas, as the model streams it.
        var output = TextDeltaLine("making that folder now.\n[ACT") + TextDeltaLine("ION: create_folder {\"name\":\"test-plan-")
                     + TextDeltaLine("actions\",\"location\":\"desktop\"}]") + ResultLine(fullAnswer);
        var runner = new FakePlanCliProcessRunner([output]);
        var provider = new ClaudeCodeAnswerProvider(FakeClaude, processRunner: runner);
        var recorder = new ChunkCollector();
        var request = SampleRequestOfferingQuickActions();

        var result = await provider.AnswerAsync(request, recorder.Append);

        Assert.Equal("making that folder now.", result.Text);
        Assert.Equal([new ClaudeToolCall("plan-action-1", "create_folder", new Dictionary<string, string> { ["name"] = "test-plan-actions", ["location"] = "desktop" })],
                     result.ToolCalls);
        Assert.Equal("making that folder now.\n", string.Concat(recorder.Chunks));
        Assert.DoesNotContain("ACT", string.Concat(recorder.Chunks));
        var call = Assert.Single(runner.Calls);
        var systemPrompt = ValueAfter("--system-prompt", call.Arguments)!;
        Assert.StartsWith("answer in lowercase\n\nQuick actions:", systemPrompt);
        foreach (var tool in request.Tools) Assert.Contains($"- {tool.Name}:", systemPrompt);
    }

    [Fact]
    public async Task ClaudeCodeWithoutToolsKeepsTheSystemPromptAndTheTextAsTheyAre()
    {
        const string answer = "[ACTION: open_app {\"name\":\"Edge\"}]";
        var runner = new FakePlanCliProcessRunner([TextDeltaLine(answer), ResultLine(answer)]);
        var provider = new ClaudeCodeAnswerProvider(FakeClaude, processRunner: runner);

        var result = await provider.AnswerAsync(SampleRequest(), _ => { });

        Assert.Equal(new AnswerResult(answer, []), result);
        Assert.Equal("answer in lowercase", ValueAfter("--system-prompt", Assert.Single(runner.Calls).Arguments));
    }

    [Fact]
    public void ClaudeCodeStreamParserFallsBackToStreamedTextAndToTheAssistantMessage()
    {
        var streamOnlyParser = new ClaudeCodeStreamParser();
        streamOnlyParser.Consume(TextDeltaLine("blue"));
        Assert.Equal("blue", streamOnlyParser.Summary.StreamedText);
        Assert.Null(streamOnlyParser.Summary.ResultText);

        var noPartialsParser = new ClaudeCodeStreamParser();
        noPartialsParser.Consume(JsonLine(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "green" }) },
        }));
        Assert.Equal("green", noPartialsParser.Summary.StreamedText);
    }

    [Fact]
    public void ClaudeCodeStreamParserHandlesALastLineWithoutANewline()
    {
        var parser = new ClaudeCodeStreamParser();
        var unterminatedLine = TextDeltaLine("tail")[..^1];
        Assert.Empty(parser.Consume(unterminatedLine));
        Assert.Equal(["tail"], parser.Finish());
    }

    [Fact]
    public void ClaudeCodeStreamParserIgnoresSubagentTextAndNonJsonLines()
    {
        var parser = new ClaudeCodeStreamParser();
        var subagentLine = JsonLine(new JsonObject
        {
            ["type"] = "stream_event",
            ["parent_tool_use_id"] = "toolu_1",
            ["event"] = new JsonObject { ["type"] = "content_block_delta", ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "inner" } },
        });
        Assert.Equal(["outer"], parser.Consume(subagentLine + "not json\n" + TextDeltaLine("outer")));
    }

    [Fact]
    public void ClaudeCodeStreamParserReadsWindowsLineEndings()
    {
        var parser = new ClaudeCodeStreamParser();
        Assert.Equal(["crlf"], parser.Consume(TextDeltaLine("crlf").Replace("\n", "\r\n")));
    }

    [Fact]
    public async Task ClaudeCodeErrorResultBecomesASignInSentence()
    {
        var output = JsonLine(new JsonObject
        {
            ["type"] = "assistant",
            ["error"] = "authentication_failed",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Not logged in · Please run /login" }) },
        }) + ResultLine("Not logged in · Please run /login", isError: true);
        var runner = new FakePlanCliProcessRunner([output], exitStatus: 1);
        var provider = new ClaudeCodeAnswerProvider(@"C:\fake\claude.exe", processRunner: runner);

        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(SampleRequest(), _ => { }));
        Assert.Equal(new AnswerProviderException(AnswerSource.ClaudeCodePlan, 0,
            "Claude Code is signed out. Open a terminal, run claude, and sign in with your Claude account."), error);
    }

    [Fact]
    public void ClaudeCodeErrorSubtypeKeepsTheClisReason()
    {
        var parser = new ClaudeCodeStreamParser();
        parser.Consume(JsonLine(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["errors"] = new JsonArray("overloaded"),
        }));
        AssertProviderError(AnswerSource.ClaudeCodePlan, "Claude Code couldn't answer: overloaded",
            () => ClaudeCodeAnswerProvider.AnswerText(parser.Summary, Output(1), 90));
    }

    [Fact]
    public void ClaudeCodeNonZeroExitWithoutEventsUsesStderrOrTheStatus()
    {
        var emptySummary = new ClaudeCodeStreamSummary();
        AssertProviderError(AnswerSource.ClaudeCodePlan, "Claude Code couldn't answer: [claude-code:unrecognized_model] bad model",
            () => ClaudeCodeAnswerProvider.AnswerText(emptySummary, Output(1, standardError: "warming up\r\n[claude-code:unrecognized_model] bad model\r\n"), 90));
        AssertProviderError(AnswerSource.ClaudeCodePlan, "Claude Code stopped with exit status 2.",
            () => ClaudeCodeAnswerProvider.AnswerText(emptySummary, Output(2), 90));
    }

    [Fact]
    public void ClaudeCodeTimeoutAndEmptyAnswerAreErrors()
    {
        AssertProviderError(AnswerSource.ClaudeCodePlan, "Claude Code took longer than 90 seconds to answer.",
            () => ClaudeCodeAnswerProvider.AnswerText(new ClaudeCodeStreamSummary(), Output(15, timedOut: true), 90));
        AssertProviderError(AnswerSource.ClaudeCodePlan, "Claude Code finished without an answer.",
            () => ClaudeCodeAnswerProvider.AnswerText(new ClaudeCodeStreamSummary(), Output(0), 90));
    }

    // MARK: Codex — the call

    [Fact]
    public void CodexRunsReadOnlyEphemeralWithToolsOffAndImagesAttached()
    {
        var arguments = CodexAnswerProvider.CommandLineArguments(null, "answer in lowercase",
            [@"C:\scratch\image-1.png", @"C:\scratch\image-2.jpg"], @"C:\scratch\last-message.txt", @"C:\scratch");
        Assert.Equal("exec", arguments[0]);
        Assert.Equal("-", arguments[^1]);
        foreach (var flag in new[] { "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--json" }) Assert.Contains(flag, arguments);
        Assert.Equal("read-only", ValueAfter("--sandbox", arguments));
        Assert.Equal(@"C:\scratch", ValueAfter("-C", arguments));
        Assert.Equal(@"C:\scratch\last-message.txt", ValueAfter("-o", arguments));
        IEnumerable<string> ValuesOf(string flag) => arguments.Select((argument, index) => (argument, index))
            .Where(pair => pair.argument == flag).Select(pair => arguments[pair.index + 1]);
        Assert.Equal([@"C:\scratch\image-1.png", @"C:\scratch\image-2.jpg"], ValuesOf("-i"));
        var disabledFeatures = ValuesOf("--disable").ToList();
        Assert.Contains("shell_tool", disabledFeatures);
        Assert.Contains("unified_exec", disabledFeatures);
        Assert.Contains("plugins", disabledFeatures);
        var configOverrides = ValuesOf("-c").ToList();
        Assert.Contains("web_search=\"disabled\"", configOverrides);
        Assert.Contains("developer_instructions=\"answer in lowercase\"", configOverrides);
        Assert.DoesNotContain("-m", arguments);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", arguments);
    }

    [Fact]
    public void CodexPassesAModelAndSkipsAnEmptySystemPrompt()
    {
        var arguments = CodexAnswerProvider.CommandLineArguments("gpt-5.5", " ", [], @"C:\o", @"C:\w");
        Assert.Equal("gpt-5.5", ValueAfter("-m", arguments));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("developer_instructions=", StringComparison.Ordinal));
        Assert.DoesNotContain("-i", arguments);
    }

    [Fact]
    public void CodexSystemPromptIsEscapedAsATomlString()
    {
        Assert.Equal("\"say \\\"hi\\\"\\nthen C:\\\\path\\tend\\u0001\"", CodexAnswerProvider.TomlBasicString("say \"hi\"\nthen C:\\path\tend\u0001"));
        Assert.Equal("\"café ✓\"", CodexAnswerProvider.TomlBasicString("café ✓"));
    }

    [Fact]
    public void CodexPromptFoldsHistoryAndNamesTheImagesInOrder()
    {
        var request = SampleRequest([new AnswerRequest.Turn("hi", "hello")]);
        request.Images.Add(new AnswerRequest.Image([1], "Screen 2 of 2", "image/jpeg"));
        Assert.Equal("""
            Earlier in this conversation:
            User: hi
            You: hello

            Attached images, in order:
            1. Screen 1 of 1 (cursor here)
            2. Screen 2 of 2

            what color is this square? answer in one word
            """.ReplaceLineEndings("\n"), CodexAnswerProvider.PromptText(request));
        Assert.Equal("just this", CodexAnswerProvider.PromptText(new AnswerRequest([], "", [], "just this", [])));
    }

    // MARK: Codex — reading the result

    [Fact]
    public async Task CodexReadsTheLastMessageFileAndCleansUp()
    {
        var runner = new FakePlanCliProcessRunner(
            [JsonLine(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = "red" } })],
            lastMessageFileContents: "red\n");
        var provider = new CodexAnswerProvider(FakeCodex, processRunner: runner);
        var recorder = new ChunkCollector();

        var result = await provider.AnswerAsync(SampleRequest(), recorder.Append);

        Assert.Equal(new AnswerResult("red", []), result);
        Assert.Equal(["red"], recorder.Chunks);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(120, call.TimeoutSeconds);
        Assert.EndsWith("what color is this square? answer in one word", Encoding.UTF8.GetString(call.StandardInput ?? []));
        var imagePath = ValueAfter("-i", call.Arguments)!;
        Assert.StartsWith(call.WorkingDirectory, imagePath);
        Assert.EndsWith(".png", imagePath);
        Assert.StartsWith(call.WorkingDirectory, ValueAfter("-o", call.Arguments));
        Assert.False(call.Environment.ContainsKey("CODEX_HOME"));
        Assert.False(call.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.True(runner.SawWorkingDirectoryDuringRun);
        Assert.False(Directory.Exists(call.WorkingDirectory));
    }

    [Fact]
    public async Task CodexTurnsAnActionLineIntoAToolCallAndSpeaksOnlyTheWords()
    {
        var runner = new FakePlanCliProcessRunner(lastMessageFileContents: "opening spotify.\n```\n[ACTION: open_app {\"name\":\"Spotify\"}]\n```\n");
        var provider = new CodexAnswerProvider(FakeCodex, processRunner: runner);
        var recorder = new ChunkCollector();
        var request = SampleRequestOfferingQuickActions();

        var result = await provider.AnswerAsync(request, recorder.Append);

        Assert.Equal(new AnswerResult("opening spotify.", [new ClaudeToolCall("plan-action-1", "open_app", new Dictionary<string, string> { ["name"] = "Spotify" })]), result);
        Assert.Equal(["opening spotify."], recorder.Chunks);
        var developerInstructions = Assert.Single(runner.Calls).Arguments.First(argument => argument.StartsWith("developer_instructions=", StringComparison.Ordinal));
        foreach (var tool in request.Tools) Assert.Contains($"- {tool.Name}:", developerInstructions);
    }

    [Fact]
    public void CodexFallsBackToTheLastAgentMessageWhenTheFileIsMissing()
    {
        var jsonLines = JsonLine(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = "i'll look" } })
                        + JsonLine(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "command_execution", ["command"] = "dir" } })
                        + JsonLine(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = " red " } });
        Assert.Equal("red", CodexAnswerProvider.AnswerText(null, Output(0, jsonLines), 120));
        AssertProviderError(AnswerSource.CodexPlan, "Codex finished without an answer.", () => CodexAnswerProvider.AnswerText("  ", Output(0), 120));
    }

    [Fact]
    public async Task CodexTurnFailureUsesTheApisInnerMessage()
    {
        const string apiErrorBody = "{\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'x' model is not supported when using Codex with a ChatGPT account.\"}}";
        var output = JsonLine(new JsonObject { ["type"] = "error", ["message"] = apiErrorBody })
                     + JsonLine(new JsonObject { ["type"] = "turn.failed", ["error"] = new JsonObject { ["message"] = apiErrorBody } });
        var runner = new FakePlanCliProcessRunner([output], exitStatus: 1);
        var provider = new CodexAnswerProvider(@"C:\fake\codex.exe", "x", runner);

        var error = await Assert.ThrowsAsync<AnswerProviderException>(() => provider.AnswerAsync(SampleRequest(), _ => { }));
        Assert.Equal(new AnswerProviderException(AnswerSource.CodexPlan, 0,
            "Codex couldn't answer: The 'x' model is not supported when using Codex with a ChatGPT account."), error);
    }

    [Fact]
    public void CodexSignedOutTimeoutAndSilentExitMessages()
    {
        AssertProviderError(AnswerSource.CodexPlan, "Codex is signed out. Open a terminal, run codex login, and sign in with ChatGPT.",
            () => CodexAnswerProvider.AnswerText(null, Output(1, standardError: "Error: Not logged in\n"), 120));
        AssertProviderError(AnswerSource.CodexPlan, "Codex took longer than 120 seconds to answer.",
            () => CodexAnswerProvider.AnswerText("late", Output(15, timedOut: true), 120));
        AssertProviderError(AnswerSource.CodexPlan, "Codex stopped with exit status 127.",
            () => CodexAnswerProvider.AnswerText(null, Output(127), 120));
    }

    // MARK: status and locating

    [Fact]
    public void ClaudeCodeStatusReadsAuthStatusJson()
    {
        Assert.Equal(PlanCliStatus.Ready, PlanCLIs.ClaudeCodeStatus(Output(0, "{\"loggedIn\": true, \"authMethod\": \"claude.ai\"}")));
        Assert.Equal(PlanCliStatus.InstalledSignedOut, PlanCLIs.ClaudeCodeStatus(Output(1, "{\"loggedIn\": false, \"authMethod\": \"none\"}")));
        Assert.Equal(PlanCliStatus.InstalledSignedOut, PlanCLIs.ClaudeCodeStatus(Output(0, "error: unknown command")));
        Assert.Equal(PlanCliStatus.InstalledSignedOut, PlanCLIs.ClaudeCodeStatus(Output(15, "{\"loggedIn\": true}", timedOut: true)));
    }

    [Fact]
    public void CodexStatusReadsLoginStatus()
    {
        Assert.Equal(PlanCliStatus.Ready, PlanCLIs.CodexStatus(Output(0, standardError: "Logged in using ChatGPT\n")));
        Assert.Equal(PlanCliStatus.InstalledSignedOut, PlanCLIs.CodexStatus(Output(1, standardError: "Not logged in\n")));
        Assert.Equal(PlanCliStatus.InstalledSignedOut, PlanCLIs.CodexStatus(Output(0, standardError: "Logged in using ChatGPT", timedOut: true)));
    }

    [Fact]
    public async Task StatusChecksRunTheRightSubcommandWithATimeout()
    {
        var claudeRunner = new FakePlanCliProcessRunner(["{\"loggedIn\": true}"]);
        Assert.Equal(PlanCliStatus.Ready, await PlanCLIs.ClaudeCodeStatusAsync(@"C:\fake\claude.exe", claudeRunner));
        var claudeCall = Assert.Single(claudeRunner.Calls);
        Assert.Equal(["auth", "status", "--json"], claudeCall.Arguments);
        Assert.Equal(PlanCLIs.StatusCheckTimeoutSeconds, claudeCall.TimeoutSeconds);

        var codexRunner = new FakePlanCliProcessRunner(standardError: "Logged in using ChatGPT", exitStatus: 0);
        Assert.Equal(PlanCliStatus.Ready, await PlanCLIs.CodexStatusAsync(@"C:\fake\codex.cmd", codexRunner));
        Assert.Equal(["login", "status"], Assert.Single(codexRunner.Calls).Arguments);

        Assert.Equal(PlanCliStatus.NotInstalled, await PlanCLIs.ClaudeCodeStatusAsync(null, claudeRunner));
        Assert.Equal(PlanCliStatus.NotInstalled, await PlanCLIs.CodexStatusAsync(null, codexRunner));
    }

    [Fact]
    public void LocatingTriesCommonDirectoriesInOrder()
    {
        const string home = @"C:\Users\test";
        var installedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\test\AppData\Roaming\npm\codex.cmd",
            @"C:\Users\test\.bun\bin\codex.exe",
            @"C:\Users\test\.claude\local\claude.exe",
            @"C:\Users\test\AppData\Local\Programs\gemini\bin\gemini.exe",
        };
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm\codex.cmd",
            PlanCLIs.PathInCommonInstallDirectories("codex", home, installedPaths.Contains, ".COM;.EXE;.BAT;.CMD"));
        Assert.Equal(@"C:\Users\test\.claude\local\claude.exe",
            PlanCLIs.PathInCommonInstallDirectories("claude", home, installedPaths.Contains, ".COM;.EXE;.BAT;.CMD"));
        Assert.Equal(@"C:\Users\test\AppData\Local\Programs\gemini\bin\gemini.exe",
            PlanCLIs.PathInCommonInstallDirectories("gemini", home, installedPaths.Contains, ".COM;.EXE;.BAT;.CMD"));
        Assert.Null(PlanCLIs.PathInCommonInstallDirectories("grok", home, installedPaths.Contains, ".COM;.EXE;.BAT;.CMD"));
        Assert.Equal(@"C:\Users\test\.local\bin", PlanCLIs.CommonInstallDirectories(home)[0]);
    }

    [Fact]
    public void ThePathIsSearchedInOrderWithPathExtAndAnExeBeforeACmd()
    {
        var installedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\nvm\v22\codex.cmd",
            @"C:\nvm\v22\codex.exe",
            @"D:\tools\codex.bat",
        };
        Assert.Equal([@"C:\nvm\v22\codex.exe", @"D:\tools\codex.bat"],
            PlanCLIs.PathsOnSearchPath("codex", @"C:\Windows\System32;C:\nvm\v22;""D:\tools"";;", ".COM;.EXE;.BAT;.CMD", installedPaths.Contains));
        Assert.Empty(PlanCLIs.PathsOnSearchPath("codex", @"C:\Windows", null, installedPaths.Contains));
        Assert.Equal([".COM", ".EXE", ".BAT", ".CMD"], PlanCLIs.ExecutableExtensions(null));
    }

    [Fact]
    public void ProcessEnvironmentDropsPlanOverridesAndPutsTheExecutablesFolderFirst()
    {
        var baseEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = @"C:\Windows\System32;C:\Windows", ["USERPROFILE"] = @"C:\Users\test", ["USERNAME"] = "test",
            ["ANTHROPIC_API_KEY"] = "ak", ["OPENAI_API_KEY"] = "sk", ["CODEX_HOME"] = @"C:\Users\test\.openclicky\codex-home",
            ["CLAUDECODE"] = "1", ["CLAUDE_CODE_SESSION_ID"] = "abc",
        };
        var environment = PlanCLIs.ProcessEnvironment(@"C:\Users\test\AppData\Roaming\npm\codex.cmd", baseEnvironment, @"C:\Users\test");
        foreach (var removedName in new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "CODEX_HOME", "CLAUDECODE", "CLAUDE_CODE_SESSION_ID" })
        {
            Assert.False(environment.ContainsKey(removedName), removedName);
        }
        // USERNAME and USERPROFILE stay: the CLIs find their sign-in through them.
        Assert.Equal("test", environment["USERNAME"]);
        Assert.Equal(@"C:\Users\test", environment["USERPROFILE"]);
        var pathEntries = environment["PATH"].Split(';');
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm", pathEntries[0]);
        Assert.Contains(@"C:\Users\test\.local\bin", pathEntries);
        Assert.Equal([@"C:\Windows\System32", @"C:\Windows"], pathEntries[^2..]);
        Assert.Equal(pathEntries.Length, pathEntries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Environment names are case-insensitive on Windows.
        Assert.Equal(environment["PATH"], environment["Path"]);
    }

    /// <summary>Two Claude Code installs (an old npm one): the newest one answers.</summary>
    [Fact]
    public void TheNewestClaudeCodeIsPicked()
    {
        var versions = new List<(string Path, string Version)>
        {
            (@"C:\Users\someone\AppData\Roaming\npm\claude.cmd", "2.1.197 (Claude Code)\r\n"),
            (@"C:\Users\someone\.local\bin\claude.exe", "2.1.296 (Claude Code)\r\n"),
            (@"C:\tools\claude.exe", "2.1.30 (Claude Code)\r\n"),
        };
        Assert.Equal(@"C:\Users\someone\.local\bin\claude.exe", PlanCLIs.NewestPath(versions));
        Assert.Null(PlanCLIs.NewestPath([]));
    }
}
