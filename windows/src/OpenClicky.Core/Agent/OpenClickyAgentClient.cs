// Port of macos/OpenClicky/OpenClicky/OpenClickyAgentClient.swift.
//
// Runs the `openclicky` CLI as a subprocess and turns its `--events` JSON Lines output into a
// result the companion can speak. This is the shell side of HeyClicky's two-tier routing: the gate
// decides "ask" (the teacher lane) versus "agent" (a Codex thread that does real work).
//
// Windows: the process goes through IProcessRunner instead of /usr/bin/env. The configured
// `cliCommand` (default `openclicky`) resolves through PATH + PATHEXT and %APPDATA%\npm, so npm's
// `openclicky.cmd` shim is found (the runner then starts its Node script directly). Cancelling
// kills the process tree: there is no SIGINT to send a console child that has no console.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Agent;

/// <summary>What a `do --gate-only` or `run --events` call ended with.</summary>
public sealed class OpenClickyAgentRunResult
{
    public string? Lane { get; set; }
    public string Text { get; set; } = "";
    public IReadOnlyList<string> Artifacts { get; set; } = [];
    public string? ThreadId { get; set; }
    public string Status { get; set; } = "unknown";
    public string? ErrorMessage { get; set; }
}

/// <summary>One Codex thread as reported by `openclicky threads list --json`.</summary>
public sealed record OpenClickyThreadSummary(
    string Id,
    string Preview,
    string Cwd,
    double CreatedAt,
    double UpdatedAt,
    string Status,
    string ModelProvider)
{
    public DateTimeOffset UpdatedDate => DateTimeOffset.FromUnixTimeMilliseconds((long)(UpdatedAt * 1000));

    internal static OpenClickyThreadSummary? From(JsonNode? node)
    {
        if (node is not JsonObject json || AgentJson.String(json["id"]) is not { } id) return null;
        return new OpenClickyThreadSummary(
            id,
            AgentJson.String(json["preview"]) ?? "",
            AgentJson.String(json["cwd"]) ?? "",
            AgentJson.Number(json["createdAt"]) ?? 0,
            AgentJson.Number(json["updatedAt"]) ?? 0,
            AgentJson.Status(json["status"]) ?? "unknown",
            AgentJson.String(json["modelProvider"]) ?? "");
    }
}

/// <summary>One turn of `openclicky threads show &lt;id&gt; --json`.</summary>
public sealed record OpenClickyThreadTurn(
    string Id,
    string Status,
    double? StartedAt,
    double? CompletedAt,
    IReadOnlyList<string> User,
    IReadOnlyList<string> Agent,
    IReadOnlyList<string> Commands)
{
    internal static OpenClickyThreadTurn? From(JsonNode? node)
    {
        if (node is not JsonObject json || AgentJson.String(json["id"]) is not { } id) return null;
        return new OpenClickyThreadTurn(
            id,
            AgentJson.Status(json["status"]) ?? "unknown",
            AgentJson.Number(json["startedAt"]),
            AgentJson.Number(json["completedAt"]),
            AgentJson.Strings(json["user"]),
            AgentJson.Strings(json["agent"]),
            AgentJson.Strings(json["commands"]));
    }
}

/// <summary>`openclicky threads show &lt;id&gt; --json`.</summary>
public sealed record OpenClickyThreadDetail(OpenClickyThreadSummary Thread, IReadOnlyList<OpenClickyThreadTurn> Turns)
{
    /// <summary>The last thing the agent said on this thread, for cards and the result panel.</summary>
    public string? LastAgentMessage => Turns.Reverse().Select(turn => turn.Agent.LastOrDefault()).FirstOrDefault(message => message is not null);

    /// <summary>Decodes the CLI's JSON; throws <see cref="OpenClickyAgentException"/> when it isn't a thread.</summary>
    public static OpenClickyThreadDetail Decode(string json)
    {
        var root = AgentJson.Parse(json) as JsonObject;
        var thread = OpenClickyThreadSummary.From(root?["thread"])
            ?? throw new OpenClickyAgentException("openclicky threads show printed no thread");
        var turns = (root?["turns"] as JsonArray ?? []).Select(OpenClickyThreadTurn.From).OfType<OpenClickyThreadTurn>().ToList();
        return new OpenClickyThreadDetail(thread, turns);
    }

    /// <summary>Decodes `threads list --json` (an array; entries without an id are skipped).</summary>
    public static IReadOnlyList<OpenClickyThreadSummary> DecodeList(string json) =>
        AgentJson.Parse(json) is JsonArray array
            ? array.Select(OpenClickyThreadSummary.From).OfType<OpenClickyThreadSummary>().ToList()
            : throw new OpenClickyAgentException("openclicky threads list printed no list");
}

/// <summary>`openclicky integrations status --json`.</summary>
public sealed record OpenClickyIntegrationsStatus(bool Configured, bool LoggedIn)
{
    public static OpenClickyIntegrationsStatus Decode(string json)
    {
        if (AgentJson.Parse(json) is not JsonObject root) throw new OpenClickyAgentException("openclicky integrations status printed no status");
        return new OpenClickyIntegrationsStatus(AgentJson.Bool(root["configured"]) ?? false, AgentJson.Bool(root["loggedIn"]) ?? false);
    }
}

public sealed class OpenClickyAgentException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Said instead of "not found on PATH" when the agent tool isn't on this PC (the installed app
    /// doesn't carry it).
    /// </summary>
    public const string ToolMissingMessage =
        "agents run through the openclicky command-line tool, which isn't on this pc. questions, pointing and quick actions still work.";
}

/// <summary>
/// Parses the CLI's `--events` JSON Lines (lane / event / delta / answer / result / error). Feed it
/// stdout lines (or raw chunks) and stderr lines, then <see cref="Flush"/> once the process ended.
/// </summary>
public sealed class OpenClickyAgentEventCollector(Action<string>? onEvent = null)
{
    private readonly StringBuilder standardOutputBuffer = new();
    private readonly StringBuilder streamedText = new();

    public OpenClickyAgentRunResult Result { get; } = new();

    /// <summary>The last non-empty stderr line (the CLI's own error when it failed outside --events).</summary>
    public string? LastStandardErrorLine { get; private set; }

    /// <summary>A piece of stdout, split on newlines; a partial last line waits for the rest.</summary>
    public void ConsumeStandardOutput(string chunk)
    {
        standardOutputBuffer.Append(chunk);
        while (true)
        {
            var buffered = standardOutputBuffer.ToString();
            var newlineIndex = buffered.IndexOf('\n');
            if (newlineIndex < 0) break;
            standardOutputBuffer.Remove(0, newlineIndex + 1);
            HandleLine(buffered[..newlineIndex].TrimEnd('\r'));
        }
    }

    /// <summary>One whole stdout line (the process runner's line channel).</summary>
    public void ConsumeStandardOutputLine(string line) => HandleLine(line.TrimEnd('\r'));

    public void ConsumeStandardError(string chunk)
    {
        foreach (var line in chunk.Split('\n'))
        {
            var trimmedLine = line.Trim(' ', '\t', '\r');
            if (trimmedLine.Length == 0) continue;
            LastStandardErrorLine = trimmedLine;
            onEvent?.Invoke(trimmedLine.Replace("▸ ", ""));
        }
    }

    public void Flush()
    {
        if (standardOutputBuffer.ToString().Trim().Length > 0)
        {
            HandleLine(standardOutputBuffer.ToString());
            standardOutputBuffer.Clear();
        }
        if (Result.Text.Length == 0) Result.Text = streamedText.ToString();
    }

    private void HandleLine(string line)
    {
        if (AgentJson.TryParseObject(line) is not { } json || AgentJson.String(json["type"]) is not { } type)
        {
            if (line.Trim().Length > 0) streamedText.Append(line).Append('\n');
            return;
        }
        switch (type)
        {
            case "lane":
                Result.Lane = AgentJson.String(json["lane"]);
                break;
            case "event":
                if (AgentJson.String(json["line"]) is { } eventLine) onEvent?.Invoke(eventLine);
                break;
            case "delta":
                streamedText.Append(AgentJson.String(json["text"]) ?? "");
                break;
            case "answer":
                Result.Text = AgentJson.String(json["text"]) ?? streamedText.ToString();
                Result.Status = "completed";
                break;
            case "result":
                Result.ThreadId = AgentJson.String(json["threadId"]);
                Result.Artifacts = json["artifacts"] is JsonArray ? AgentJson.Strings(json["artifacts"]) : [];
                Result.Status = AgentJson.String(json["status"]) ?? "completed";
                Result.ErrorMessage = AgentJson.String(json["error"]);
                var finalMessage = AgentJson.String(json["finalMessage"]) ?? "";
                Result.Text = finalMessage.Length == 0 ? streamedText.ToString() : finalMessage;
                break;
            case "error":
                Result.ErrorMessage = AgentJson.String(json["message"]) ?? "unknown error";
                break;
        }
    }
}

/// <summary>Drives the `openclicky` CLI: the gate, agent runs, threads, and integrations.</summary>
public sealed class OpenClickyAgentClient
{
    private readonly IProcessRunner processRunner;
    private readonly Func<IReadOnlyList<string>> cliCommand;
    private readonly Func<IReadOnlyDictionary<string, string>> cliEnvironment;
    private readonly Func<string> workspacePath;
    private readonly Func<string?> agentModelOverride;
    private readonly object runLock = new();
    private CancellationTokenSource? runningCancellation;

    /// <summary>The settings-backed client; the optional providers replace shell.json's values (tests).</summary>
    public OpenClickyAgentClient(
        IProcessRunner processRunner,
        Func<IReadOnlyList<string>>? cliCommand = null,
        Func<IReadOnlyDictionary<string, string>>? cliEnvironment = null,
        Func<string>? workspacePath = null,
        Func<string?>? agentModelOverride = null)
    {
        this.processRunner = processRunner;
        this.cliCommand = cliCommand ?? (() => OpenClickyConfiguration.CliCommand);
        this.cliEnvironment = cliEnvironment ?? (() => OpenClickyConfiguration.CliProcessEnvironment);
        this.workspacePath = workspacePath ?? (() => OpenClickyConfiguration.WorkspacePath);
        this.agentModelOverride = agentModelOverride ?? (() => OpenClickyConfiguration.AgentModelOverride);
    }

    /// <summary>Whether the openclicky tool the agent lane runs is on this PC.</summary>
    public bool IsToolInstalled
    {
        get
        {
            var executableName = cliCommand().FirstOrDefault();
            return !string.IsNullOrEmpty(executableName) && processRunner.ResolveExecutable(executableName) is not null;
        }
    }

    /// <summary>
    /// Ask the gate which lane this request belongs to. Falls back to "agent" only if the CLI ran
    /// but printed no lane; when the CLI itself cannot run, the teacher lane ("ask") answers. The
    /// CLI already falls back to a local heuristic when the gate model is unavailable.
    /// </summary>
    public async Task<string> ClassifyLaneAsync(string transcript, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteAsync(["do", transcript, "--gate-only", "--events"], onEvent: null, cancellationToken).ConfigureAwait(false);
            return result.Lane ?? "agent";
        }
        catch (Exception exception) when (exception is OpenClickyAgentException or ProcessRunnerException or IOException)
        {
            AppLog.Write($"agent: gate failed ({exception.Message}); defaulting to the teacher lane");
            return "ask";
        }
    }

    /// <summary>The arguments of a full agent run (exposed for tests).</summary>
    public IReadOnlyList<string> RunAgentArguments(string task, string? screenshotPath, string? threadId)
    {
        var arguments = new List<string> { "run", task, "--events", "--cwd", workspacePath() };
        if (screenshotPath is not null) arguments.AddRange(["--image", screenshotPath]);
        if (threadId is not null) arguments.AddRange(["--thread", threadId]);
        if (agentModelOverride() is { } model) arguments.AddRange(["--model", model]);
        return arguments;
    }

    /// <summary>
    /// Full agent run on a (possibly resumed) Codex thread. The screenshot is attached as image
    /// context. <paramref name="onEvent"/> gets each progress line on a background thread.
    /// </summary>
    public Task<OpenClickyAgentRunResult> RunAgentAsync(string task, string? screenshotPath, string? threadId,
                                                        Action<string>? onEvent, CancellationToken cancellationToken = default) =>
        ExecuteAsync(RunAgentArguments(task, screenshotPath, threadId), onEvent, cancellationToken);

    /// <summary>Recent Codex threads (newest first) for the Agents tab.</summary>
    public async Task<IReadOnlyList<OpenClickyThreadSummary>> ListThreadsAsync(int limit = 30, CancellationToken cancellationToken = default)
    {
        var output = await CaptureStandardOutputAsync(["threads", "list", "--limit", limit.ToString(CultureInfo.InvariantCulture), "--json"], cancellationToken).ConfigureAwait(false);
        return OpenClickyThreadDetail.DecodeList(output);
    }

    /// <summary>Full turn history of one thread.</summary>
    public async Task<OpenClickyThreadDetail> ReadThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var output = await CaptureStandardOutputAsync(["threads", "show", threadId, "--json"], cancellationToken).ConfigureAwait(false);
        return OpenClickyThreadDetail.Decode(output);
    }

    /// <summary>Whether the agent's Composio MCP server is configured and Codex is logged into it.</summary>
    public async Task<OpenClickyIntegrationsStatus> IntegrationsStatusAsync(CancellationToken cancellationToken = default)
    {
        var output = await CaptureStandardOutputAsync(["integrations", "status", "--json"], cancellationToken).ConfigureAwait(false);
        return OpenClickyIntegrationsStatus.Decode(output);
    }

    /// <summary>
    /// Logs Codex into the Composio MCP server: opens Composio's authorization page in the browser
    /// and returns once the user has finished there (or fails after the CLI's own timeout).
    /// </summary>
    public async Task LoginIntegrationAsync(string server = "composio", CancellationToken cancellationToken = default) =>
        await CaptureStandardOutputAsync(["integrations", "login", server], cancellationToken).ConfigureAwait(false);

    /// <summary>Interrupt the current run (the user started talking again): kills the CLI and its Codex child.</summary>
    public void Cancel()
    {
        lock (runLock)
        {
            runningCancellation?.Cancel();
        }
    }

    // MARK: - Process plumbing

    private (string Executable, ProcessLaunchRequest Request) Launch(IReadOnlyList<string> arguments)
    {
        var command = cliCommand();
        var executableName = command.FirstOrDefault();
        if (string.IsNullOrEmpty(executableName))
        {
            throw new OpenClickyAgentException($"cliCommand is empty in {OpenClickyConfiguration.SettingsFilePath}");
        }
        var executable = processRunner.ResolveExecutable(executableName)
            ?? throw new OpenClickyAgentException(OpenClickyAgentException.ToolMissingMessage);
        var workingDirectory = workspacePath();
        try { Directory.CreateDirectory(workingDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        var environment = cliEnvironment().ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase);
        return (executable, new ProcessLaunchRequest
        {
            Command = executable,
            Arguments = [.. command.Skip(1), .. arguments],
            WorkingDirectory = workingDirectory,
            Environment = environment,
        });
    }

    /// <summary>Runs a CLI command that prints a single JSON document and returns its stdout.</summary>
    private async Task<string> CaptureStandardOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (_, request) = Launch(arguments);
        ProcessRunResult result;
        try
        {
            result = await processRunner.RunAsync(request, standardInput: null, timeout: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception)
        {
            throw new OpenClickyAgentException($"failed to launch {string.Join(" ", cliCommand())}: {exception.Message}", exception);
        }
        if (result.ExitCode != 0)
        {
            var errorText = result.StandardError.Trim();
            throw new OpenClickyAgentException(errorText.Length == 0 ? $"openclicky exited with status {result.ExitCode}" : errorText);
        }
        return result.StandardOutput;
    }

    private async Task<OpenClickyAgentRunResult> ExecuteAsync(IReadOnlyList<string> arguments, Action<string>? onEvent, CancellationToken cancellationToken)
    {
        var (_, request) = Launch(arguments);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (runLock)
        {
            runningCancellation = cancellation;
        }
        var collector = new OpenClickyAgentEventCollector(onEvent);
        var collectorLock = new object();
        try
        {
            IRunningProcess process;
            try
            {
                process = processRunner.Start(request, cancellation.Token);
            }
            catch (ProcessRunnerException exception)
            {
                throw new OpenClickyAgentException($"failed to launch {string.Join(" ", cliCommand())}: {exception.Message}", exception);
            }
            await using (process.ConfigureAwait(false))
            {
                process.CloseStandardInput();
                var readOutput = Task.Run(async () =>
                {
                    await foreach (var line in process.StandardOutputLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        lock (collectorLock) collector.ConsumeStandardOutputLine(line);
                    }
                }, CancellationToken.None);
                var readErrors = Task.Run(async () =>
                {
                    await foreach (var line in process.StandardErrorLines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        lock (collectorLock) collector.ConsumeStandardError(line);
                    }
                }, CancellationToken.None);

                int terminationStatus;
                try
                {
                    terminationStatus = await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }
                await Task.WhenAll(readOutput, readErrors).ConfigureAwait(false);

                lock (collectorLock) collector.Flush();
                var result = collector.Result;
                if (terminationStatus != 0 && result.ErrorMessage is null)
                {
                    result.ErrorMessage = collector.LastStandardErrorLine ?? $"openclicky exited with status {terminationStatus}";
                }
                if (result.ErrorMessage is { } errorMessage && terminationStatus != 0)
                {
                    throw new OpenClickyAgentException(errorMessage);
                }
                return result;
            }
        }
        finally
        {
            lock (runLock)
            {
                if (ReferenceEquals(runningCancellation, cancellation)) runningCancellation = null;
            }
        }
    }
}

/// <summary>Lenient JSON reads for the CLI's output.</summary>
internal static class AgentJson
{
    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException exception) { throw new OpenClickyAgentException($"openclicky printed unreadable JSON: {exception.Message}", exception); }
    }

    public static JsonObject? TryParseObject(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return null;
        try { return JsonNode.Parse(trimmed) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>A status string, or the `type` of a Codex status object ({"type":"idle"}).</summary>
    public static string? Status(JsonNode? node) => String(node) ?? (node is JsonObject json ? String(json["type"]) : null);

    public static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) ? number : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(String).OfType<string>().ToList() : [];
}
