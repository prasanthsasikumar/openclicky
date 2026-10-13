// New on Windows (the Swift client has no unit tests): the `--events` JSON Lines parsing with lines
// shaped exactly as agent/src/cli.ts emits them (lane / event / delta / answer / result / error),
// the threads and integrations JSON as `threads list|show --json` and `integrations status --json`
// print it, and how the client launches the CLI through IProcessRunner (the configured cliCommand,
// resolved like an npm .cmd shim, in the workspace, with the CLI environment).

using System.Threading.Channels;
using OpenClicky.Core.Agent;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Agent;

/// <summary>A process runner that plays back canned output and records what was launched.</summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    public Dictionary<string, string> Executables { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openclicky"] = @"C:\Users\tester\AppData\Roaming\npm\openclicky.cmd",
    };

    public List<ProcessLaunchRequest> Launched { get; } = [];

    /// <summary>What the next started process prints and exits with, by its first CLI argument.</summary>
    public Func<ProcessLaunchRequest, (IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr, int ExitCode)> Script { get; set; } =
        _ => ([], [], 0);

    public string? ResolveExecutable(string command) => Executables.GetValueOrDefault(command) ?? (Path.IsPathRooted(command) ? command : null);

    public IRunningProcess Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        Launched.Add(request);
        var (stdout, stderr, exitCode) = Script(request);
        return new ScriptedProcess(stdout, stderr, exitCode);
    }

    public Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Launched.Add(request);
        var (stdout, stderr, exitCode) = Script(request);
        return Task.FromResult(new ProcessRunResult(exitCode, string.Concat(stdout.Select(line => line + "\n")), string.Concat(stderr.Select(line => line + "\n")), false));
    }

    private sealed class ScriptedProcess : IRunningProcess
    {
        private readonly Channel<string> standardOutput = Channel.CreateUnbounded<string>();
        private readonly Channel<string> standardError = Channel.CreateUnbounded<string>();
        private readonly int exitCode;

        public ScriptedProcess(IReadOnlyList<string> stdout, IReadOnlyList<string> stderr, int exitCode)
        {
            foreach (var line in stdout) standardOutput.Writer.TryWrite(line);
            foreach (var line in stderr) standardError.Writer.TryWrite(line);
            standardOutput.Writer.Complete();
            standardError.Writer.Complete();
            this.exitCode = exitCode;
        }

        public int ProcessId => 4242;
        public ChannelReader<string> StandardOutputLines => standardOutput.Reader;
        public ChannelReader<string> StandardErrorLines => standardError.Reader;
        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void CloseStandardInput() { }
        public void Kill() { }
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(exitCode);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class OpenClickyAgentEventCollectorTests
{
    // Captured from `openclicky run "make a report" --events` (agent/src/cli.ts emit()).
    private static readonly string[] RunLines =
    [
        """{"type":"event","line":"codex app-server started (codex-cli 0.48.0)"}""",
        """{"type":"event","line":"started thread 019a2b3c-4d5e-7f60-8a9b-0c1d2e3f4a5b (model gpt-5-codex, provider openclicky)"}""",
        """{"type":"delta","text":"Writing "}""",
        """{"type":"delta","text":"the report."}""",
        """{"type":"result","threadId":"019a2b3c-4d5e-7f60-8a9b-0c1d2e3f4a5b","turnId":"7","status":"completed","finalMessage":"Done: report.md is in your OpenClicky folder.","artifacts":["C:\\Users\\tester\\OpenClicky\\report.md"]}""",
    ];

    [Fact]
    public void ARunsResultCarriesTheThreadTheArtifactsAndTheFinalMessage()
    {
        var events = new List<string>();
        var collector = new OpenClickyAgentEventCollector(events.Add);
        foreach (var line in RunLines) collector.ConsumeStandardOutputLine(line);
        collector.Flush();

        var result = collector.Result;
        Assert.Equal("019a2b3c-4d5e-7f60-8a9b-0c1d2e3f4a5b", result.ThreadId);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Done: report.md is in your OpenClicky folder.", result.Text);
        Assert.Equal([@"C:\Users\tester\OpenClicky\report.md"], result.Artifacts);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.Lane);
        Assert.Equal(2, events.Count);
        Assert.StartsWith("started thread", events[1]);
    }

    [Fact]
    public void AnEmptyFinalMessageFallsBackToTheStreamedText()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutputLine("""{"type":"delta","text":"half "}""");
        collector.ConsumeStandardOutputLine("""{"type":"delta","text":"done"}""");
        collector.ConsumeStandardOutputLine("""{"type":"result","threadId":"t1","turnId":"1","status":"failed","finalMessage":"","artifacts":[],"error":"sandbox denied"}""");
        collector.Flush();
        Assert.Equal("half done", collector.Result.Text);
        Assert.Equal("failed", collector.Result.Status);
        Assert.Equal("sandbox denied", collector.Result.ErrorMessage);
        Assert.Empty(collector.Result.Artifacts);
    }

    [Fact]
    public void TheGateLineGivesTheLane()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutputLine("""{"type":"lane","lane":"ask","gated":true,"reason":"a question about the screen"}""");
        collector.Flush();
        Assert.Equal("ask", collector.Result.Lane);
        Assert.Equal("unknown", collector.Result.Status);
    }

    [Fact]
    public void AnAskAnswerCompletesWithItsText()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutputLine("""{"type":"lane","lane":"ask","forced":true}""");
        collector.ConsumeStandardOutputLine("""{"type":"delta","text":"html is "}""");
        collector.ConsumeStandardOutputLine("""{"type":"answer","text":"html is the skeleton of a web page."}""");
        collector.Flush();
        Assert.Equal("html is the skeleton of a web page.", collector.Result.Text);
        Assert.Equal("completed", collector.Result.Status);
    }

    [Fact]
    public void AnErrorLineIsKept()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutputLine("""{"type":"error","message":"missing credentials: sign in to OpenClicky"}""");
        collector.ConsumeStandardOutputLine("""{"type":"error"}""");
        Assert.Equal("unknown error", collector.Result.ErrorMessage);
    }

    [Fact]
    public void ChunksAreSplitOnNewlinesAndAPartialLineWaitsForFlush()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutput("{\"type\":\"lane\",\"la");
        Assert.Null(collector.Result.Lane);
        collector.ConsumeStandardOutput("ne\":\"agent\"}\r\n{\"type\":\"delta\",\"text\":\"a\"}\n{\"type\":\"delta\",");
        Assert.Equal("agent", collector.Result.Lane);
        collector.ConsumeStandardOutput("\"text\":\"b\"}");
        collector.Flush();
        Assert.Equal("ab", collector.Result.Text);
    }

    [Fact]
    public void PlainTextLinesAreKeptAsTheAnswerWhenThereIsNoJson()
    {
        var collector = new OpenClickyAgentEventCollector();
        collector.ConsumeStandardOutputLine("agent");
        collector.ConsumeStandardOutputLine("");
        collector.ConsumeStandardOutputLine("{not json either");
        collector.Flush();
        Assert.Equal("agent\n{not json either\n", collector.Result.Text);
    }

    [Fact]
    public void StderrLinesBecomeEventsWithoutTheirArrow()
    {
        var events = new List<string>();
        var collector = new OpenClickyAgentEventCollector(events.Add);
        collector.ConsumeStandardError("▸ lane: agent — wants a file\r\n\n  error: boom  \n");
        Assert.Equal(["lane: agent — wants a file", "error: boom"], events);
        Assert.Equal("error: boom", collector.LastStandardErrorLine);
    }
}

public class OpenClickyAgentJsonTests
{
    [Fact]
    public void ThreadsListDecodes()
    {
        const string json = """
            [
              {
                "id": "019a2b3c",
                "preview": "make a report about my week",
                "cwd": "C:\\Users\\tester\\OpenClicky",
                "createdAt": 1760000000,
                "updatedAt": 1760000123.5,
                "status": "idle",
                "modelProvider": "openclicky"
              },
              { "preview": "no id: skipped" }
            ]
            """;
        var threads = OpenClickyThreadDetail.DecodeList(json);
        var thread = Assert.Single(threads);
        Assert.Equal("019a2b3c", thread.Id);
        Assert.Equal(@"C:\Users\tester\OpenClicky", thread.Cwd);
        Assert.Equal("idle", thread.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000123500), thread.UpdatedDate);
    }

    [Fact]
    public void ThreadDetailDecodesAndGivesTheLastAgentMessage()
    {
        const string json = """
            {
              "thread": { "id": "t1", "preview": "p", "cwd": "C:\\w", "createdAt": 1, "updatedAt": 2, "status": "notLoaded", "modelProvider": "openclicky" },
              "turns": [
                { "id": "1", "status": "completed", "startedAt": 10, "completedAt": 12, "user": ["hi"], "agent": ["first", "second"], "commands": ["dir"] },
                { "id": "2", "status": { "type": "interrupted" }, "user": ["again"], "agent": [], "commands": [] }
              ]
            }
            """;
        var detail = OpenClickyThreadDetail.Decode(json);
        Assert.Equal("t1", detail.Thread.Id);
        Assert.Equal(2, detail.Turns.Count);
        Assert.Equal(10, detail.Turns[0].StartedAt);
        Assert.Null(detail.Turns[1].StartedAt);
        Assert.Equal("interrupted", detail.Turns[1].Status);
        Assert.Equal(["dir"], detail.Turns[0].Commands);
        Assert.Equal("second", detail.LastAgentMessage);
    }

    [Fact]
    public void IntegrationsStatusDecodes()
    {
        var status = OpenClickyIntegrationsStatus.Decode("""{ "configured": true, "composio": null, "loggedIn": false }""");
        Assert.Equal(new OpenClickyIntegrationsStatus(Configured: true, LoggedIn: false), status);
    }

    [Fact]
    public void UnreadableJsonIsAnAgentError() =>
        Assert.Throws<OpenClickyAgentException>(() => OpenClickyThreadDetail.DecodeList("error: not json"));
}

public class OpenClickyAgentClientTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "openclicky-agent-client-tests");

    private static OpenClickyAgentClient MakeClient(ScriptedProcessRunner runner, IReadOnlyList<string>? cliCommand = null, string? model = null) =>
        new(runner,
            cliCommand: () => cliCommand ?? ["openclicky"],
            cliEnvironment: () => new Dictionary<string, string> { ["PATH"] = @"C:\Windows", ["BACKEND_URL"] = "https://example.test" },
            workspacePath: () => Workspace,
            agentModelOverride: () => model);

    [Fact]
    public async Task TheGateRunsDoGateOnlyAndReadsTheLane()
    {
        var runner = new ScriptedProcessRunner
        {
            Script = _ => (["""{"type":"lane","lane":"ask","gated":true,"reason":"question"}"""], [], 0),
        };
        var lane = await MakeClient(runner).ClassifyLaneAsync("what is this?");
        Assert.Equal("ask", lane);
        var request = Assert.Single(runner.Launched);
        Assert.Equal(@"C:\Users\tester\AppData\Roaming\npm\openclicky.cmd", request.Command);
        Assert.Equal(["do", "what is this?", "--gate-only", "--events"], request.Arguments);
        Assert.Equal(Workspace, request.WorkingDirectory);
        Assert.Equal("https://example.test", request.Environment!["BACKEND_URL"]);
    }

    [Fact]
    public async Task AGateThatPrintsNoLaneMeansTheAgentAndAMissingToolMeansAsk()
    {
        var silent = new ScriptedProcessRunner();
        Assert.Equal("agent", await MakeClient(silent).ClassifyLaneAsync("do it"));

        var missing = new ScriptedProcessRunner();
        missing.Executables.Clear();
        var client = MakeClient(missing);
        Assert.False(client.IsToolInstalled);
        Assert.Equal("ask", await client.ClassifyLaneAsync("do it"));
        Assert.Empty(missing.Launched);
    }

    [Fact]
    public async Task AMissingToolSaysSoInPlainWords()
    {
        var runner = new ScriptedProcessRunner();
        runner.Executables.Clear();
        var error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(runner).RunAgentAsync("x", null, null, null));
        Assert.Equal(OpenClickyAgentException.ToolMissingMessage, error.Message);
        Assert.DoesNotContain("mac", error.Message);
    }

    [Fact]
    public async Task ARunPassesTheWorkspaceImageThreadAndModelAndCollectsTheResult()
    {
        var runner = new ScriptedProcessRunner
        {
            Script = _ => (
            [
                """{"type":"event","line":"resumed thread t9"}""",
                """{"type":"result","threadId":"t9","turnId":"2","status":"completed","finalMessage":"Done.","artifacts":["C:\\w\\a.txt"]}""",
            ], ["▸ codex warming up"], 0),
        };
        runner.Executables["node"] = @"C:\Program Files\nodejs\node.exe";
        var events = new List<string>();
        var result = await MakeClient(runner, cliCommand: ["node", @"C:\src\agent\dist\cli.js"], model: "gpt-5")
            .RunAgentAsync("tidy up", @"C:\tmp\shot.png", "t9", line => { lock (events) events.Add(line); });

        var request = Assert.Single(runner.Launched);
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", request.Command);
        Assert.Equal([@"C:\src\agent\dist\cli.js", "run", "tidy up", "--events", "--cwd", Workspace,
                      "--image", @"C:\tmp\shot.png", "--thread", "t9", "--model", "gpt-5"], request.Arguments);
        Assert.Equal("t9", result.ThreadId);
        Assert.Equal("Done.", result.Text);
        Assert.Equal([@"C:\w\a.txt"], result.Artifacts);
        Assert.Contains("resumed thread t9", events);
        Assert.Contains("codex warming up", events);
    }

    [Fact]
    public async Task AFailedRunThrowsItsErrorLineOrItsLastStderrLine()
    {
        var withError = new ScriptedProcessRunner
        {
            Script = _ => (["""{"type":"error","message":"turn failed: sandbox denied"}"""], ["error: ignored"], 1),
        };
        var error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(withError).RunAgentAsync("x", null, null, null));
        Assert.Equal("turn failed: sandbox denied", error.Message);

        var stderrOnly = new ScriptedProcessRunner { Script = _ => ([], ["node: bad option"], 9) };
        error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(stderrOnly).RunAgentAsync("x", null, null, null));
        Assert.Equal("node: bad option", error.Message);

        var silent = new ScriptedProcessRunner { Script = _ => ([], [], 3) };
        error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(silent).RunAgentAsync("x", null, null, null));
        Assert.Equal("openclicky exited with status 3", error.Message);
    }

    [Fact]
    public async Task ThreadsAndIntegrationsReadTheirJsonFromStdout()
    {
        var runner = new ScriptedProcessRunner
        {
            Script = request => request.Arguments[0] switch
            {
                "threads" when request.Arguments[1] == "list" => (["""[{"id":"a","preview":"","cwd":"","createdAt":0,"updatedAt":0,"status":"idle","modelProvider":""}]"""], [], 0),
                "threads" => (["""{"thread":{"id":"a","preview":"","cwd":"","createdAt":0,"updatedAt":0,"status":"idle","modelProvider":""},"turns":[]}"""], [], 0),
                "integrations" when request.Arguments[1] == "status" => (["""{"configured":true,"composio":null,"loggedIn":true}"""], [], 0),
                _ => ([], [], 0),
            },
        };
        var client = MakeClient(runner);
        Assert.Equal("a", Assert.Single(await client.ListThreadsAsync(limit: 5)).Id);
        Assert.Equal("a", (await client.ReadThreadAsync("a")).Thread.Id);
        Assert.True((await client.IntegrationsStatusAsync()).LoggedIn);
        await client.LoginIntegrationAsync();

        Assert.Equal(
        [
            "threads list --limit 5 --json",
            "threads show a --json",
            "integrations status --json",
            "integrations login composio",
        ], runner.Launched.Select(request => string.Join(" ", request.Arguments)));
    }

    [Fact]
    public async Task AFailedJsonCommandThrowsItsStderr()
    {
        var runner = new ScriptedProcessRunner { Script = _ => ([], ["  codex mcp list exited 1  "], 1) };
        var error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(runner).IntegrationsStatusAsync());
        Assert.Equal("codex mcp list exited 1", error.Message);
    }

    [Fact]
    public async Task AnEmptyCliCommandNamesTheSettingsFile()
    {
        var error = await Assert.ThrowsAsync<OpenClickyAgentException>(() => MakeClient(new ScriptedProcessRunner(), cliCommand: []).ListThreadsAsync());
        Assert.StartsWith("cliCommand is empty in ", error.Message);
    }
}
