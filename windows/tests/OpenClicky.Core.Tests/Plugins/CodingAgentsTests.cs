// Ports AgentSessionsTests.swift and ClaudeUsageTests.swift: what a coding agent's own session log says it is
// doing, when a session counts as live, the watcher that lists them, and Claude's plan limits (the reply, how a
// reset reads, and the store's states). Logs are fixture strings written to a temporary folder.

using System.Text.Json.Nodes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Plugins.CodingAgents;

namespace OpenClicky.Core.Tests.Plugins;

public class CodingAgentsTests
{
    private static string Line(JsonObject record) => record.ToJsonString();

    private static string Assistant(JsonArray blocks, string? stopReason, string cwd = "C:\\Users\\me\\Code\\app")
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = blocks };
        if (stopReason is not null) message["stop_reason"] = stopReason;
        return Line(new JsonObject { ["type"] = "assistant", ["cwd"] = cwd, ["message"] = message });
    }

    private static JsonObject Block(string type, params (string Key, JsonNode Value)[] fields)
    {
        var block = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields) block[key] = value;
        return block;
    }

    [Fact]
    public void AClaudeToolCallReadsAsWhatItIsDoing()
    {
        var tail = string.Join("\n",
            Line(new JsonObject { ["type"] = "user", ["cwd"] = "C:\\Users\\me\\Code\\app", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "fix it" } }),
            Assistant([Block("tool_use", ("name", "Edit"), ("input", new JsonObject { ["file_path"] = "C:\\Users\\me\\Code\\app\\src\\Hero.cs" }))], "tool_use"),
            Line(new JsonObject { ["type"] = "attachment", ["attachment"] = new JsonObject() }),
            Line(new JsonObject { ["type"] = "api-request", ["requestId"] = "x" }));
        var parse = AgentSessionParser.ParseClaudeTail(tail);
        Assert.Equal(new AgentActivity.Tool("Edit", "Hero.cs"), parse.Activity);
        Assert.Equal("editing Hero.cs", parse.Activity.Line);
        Assert.Equal("C:\\Users\\me\\Code\\app", parse.Cwd);
        var bash = AgentSessionParser.ParseClaudeTail(Assistant([Block("tool_use", ("name", "Bash"), ("input", new JsonObject { ["command"] = "git status --short && git log --oneline -5" }))], "tool_use"));
        // The first 40 characters of the command, trailing space trimmed.
        Assert.Equal("running: git status --short && git log --oneline", bash.Activity.Line);
    }

    [Fact]
    public void AFinishedClaudeAnswerIsIdleAndAFollowUpPromptIsWaiting()
    {
        var finished = Assistant([Block("text", ("text", "Done, the hero is blue."))], "end_turn");
        Assert.IsType<AgentActivity.Idle>(AgentSessionParser.ParseClaudeTail(finished).Activity);
        var streaming = Assistant([Block("text", ("text", "Let me look"))], null);
        Assert.Equal(new AgentActivity.Writing("Let me look"), AgentSessionParser.ParseClaudeTail(streaming).Activity);
        var thinking = Assistant([Block("thinking", ("thinking", "hmm"))], "tool_use");
        Assert.IsType<AgentActivity.Thinking>(AgentSessionParser.ParseClaudeTail(thinking).Activity);
        var followUp = string.Join("\n", finished, Line(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "now red" } }));
        Assert.IsType<AgentActivity.Waiting>(AgentSessionParser.ParseClaudeTail(followUp).Activity);
        var titled = string.Join("\n", Line(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Blue hero" }), finished);
        Assert.Equal("Blue hero", AgentSessionParser.ParseClaudeTail(titled).Title);
    }

    [Fact]
    public void NoiseWithoutAnAssistantRecordIsWaitingAndBrokenLinesAreSkipped()
    {
        var tail = "not json at all\n" + Line(new JsonObject { ["type"] = "api-request-blob", ["cwd"] = "C:\\x\\y" }) + "\n{\"type\": \"progress\"";
        var parse = AgentSessionParser.ParseClaudeTail(tail);
        Assert.IsType<AgentActivity.Waiting>(parse.Activity);
        Assert.Equal("C:\\x\\y", parse.Cwd);
    }

    [Fact]
    public void CodexRolloutsReadTheSameWay()
    {
        var meta = Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["cwd"] = "C:\\Users\\me\\Code\\site", ["id"] = "abc" } });
        var call = Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "function_call", ["name"] = "shell", ["arguments"] = "{\"command\":[\"npm\",\"test\"]}" } });
        var parse = AgentSessionParser.ParseCodexTail(string.Join("\n", meta, call));
        Assert.Equal("C:\\Users\\me\\Code\\site", parse.Cwd);
        Assert.Equal("running: npm test", parse.Activity.Line);
        var done = Line(new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_complete", ["last_agent_message"] = "ok" } });
        Assert.IsType<AgentActivity.Idle>(AgentSessionParser.ParseCodexTail(string.Join("\n", meta, call, done)).Activity);
        var reasoning = Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() } });
        Assert.IsType<AgentActivity.Thinking>(AgentSessionParser.ParseCodexTail(reasoning).Activity);
        var message = Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Here is the plan" }) } });
        Assert.Equal(new AgentActivity.Writing("Here is the plan"), AgentSessionParser.ParseCodexTail(message).Activity);
        var firstPrompt = Line(new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "make the site faster please" }) } });
        Assert.Equal("make the site faster please", AgentSessionParser.ParseCodexTail(string.Join("\n", meta, firstPrompt, call)).Title);
        Assert.True(AgentSessionParser.IsOwnScratchRollout(Path.Combine(Path.GetTempPath(), "openclicky-plan-answers", "abc")));
        Assert.False(AgentSessionParser.IsOwnScratchRollout("C:\\Users\\me\\Code\\site"));
    }

    [Fact]
    public void ASessionIsLiveForTwoMinutesUnlessItIsIdle()
    {
        var now = DateTimeOffset.Now;
        Assert.True(new AgentSession("a", AgentKind.ClaudeCode, "app", null, new AgentActivity.Thinking(), now.AddSeconds(-119)).IsLive(now));
        Assert.False(new AgentSession("b", AgentKind.ClaudeCode, "app", null, new AgentActivity.Thinking(), now.AddSeconds(-121)).IsLive(now));
        Assert.False(new AgentSession("c", AgentKind.Codex, "app", null, new AgentActivity.Idle(), now).IsLive(now));
    }

    private static string WriteLog(string path, string text, DateTimeOffset modifiedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modifiedAt.UtcDateTime);
        return path;
    }

    [Fact]
    public void TheWatcherListsRecentLogsLiveFirstAndSkipsItsOwnScratchRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agents-tests-{Guid.NewGuid():N}");
        try
        {
            var claude = Path.Combine(root, "claude", "projects", "C--Users-me-app");
            var now = DateTimeOffset.Now;
            // Today's folder: the watcher only reads the day folders its recent window touches.
            var codex = Path.Combine(root, "codex", "sessions", now.Year.ToString("D4"), now.Month.ToString("D2"), now.Day.ToString("D2"));
            WriteLog(Path.Combine(claude, "done.jsonl"), Assistant([Block("text", ("text", "done"))], "end_turn"), now.AddSeconds(-30));
            WriteLog(Path.Combine(claude, "stale.jsonl"), Assistant([Block("thinking", ("thinking", "x"))], null), now.AddSeconds(-900));
            var scratch = Path.Combine(Path.GetTempPath(), "openclicky-plan-answers", "x");
            WriteLog(Path.Combine(codex, "rollout-own.jsonl"), Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["cwd"] = scratch } }), now);
            WriteLog(Path.Combine(codex, "rollout-site.jsonl"), Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["cwd"] = "C:\\Users\\me\\site" } }), now.AddSeconds(-10));
            WriteLog(Path.Combine(claude, "live.jsonl"), Assistant([Block("tool_use", ("name", "Read"), ("input", new JsonObject { ["file_path"] = "C:\\Users\\me\\app\\README.md" }))], "tool_use"), now);
            var watcher = new AgentSessionWatcher(new AgentLogRoots(Path.Combine(root, "claude", "projects"), Path.Combine(root, "codex", "sessions")), () => now);
            watcher.RefreshNow();
            Assert.Equal(["app", "site", "app"], watcher.Sessions.Select(session => session.Project));
            Assert.Equal("reading README.md", watcher.Sessions[0].Activity.Line);
            Assert.Equal(2, watcher.LiveCount);   // the Codex one is waiting on its prompt
            Assert.Equal("claude · reading README.md", watcher.PeekLine);
            Assert.False(watcher.IsPolling);
            watcher.SetEnabled(true);
            Assert.True(watcher.IsPolling);
            Assert.Equal(AgentSessionWatcher.BackgroundInterval, watcher.PollInterval);
            watcher.BeginWatching();
            Assert.Equal(AgentSessionWatcher.WatchedInterval, watcher.PollInterval);
            watcher.EndWatching();
            Assert.Equal(AgentSessionWatcher.BackgroundInterval, watcher.PollInterval);
            watcher.SetEnabled(false);
            Assert.False(watcher.IsPolling);
            Assert.Empty(watcher.Sessions);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ASessionGoneQuietLeavesTheLiveSetAndSubagentLogsAreNotSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agents-tests-{Guid.NewGuid():N}");
        try
        {
            var folder = Path.Combine(root, "claude", "projects", "C--Users-me-app");
            var clock = DateTimeOffset.Now;
            WriteLog(Path.Combine(folder, "abc.jsonl"), Assistant([Block("tool_use", ("name", "Bash"), ("input", new JsonObject { ["command"] = "npm test" }))], "tool_use"), clock);
            WriteLog(Path.Combine(folder, "abc", "subagents", "agent-x.jsonl"), Assistant([Block("thinking", ("thinking", "x"))], null), clock);
            var watcher = new AgentSessionWatcher(new AgentLogRoots(Path.Combine(root, "claude", "projects"), Path.Combine(root, "codex", "sessions")), () => clock);
            var changes = 0;
            watcher.Changed += () => changes++;
            watcher.RefreshNow();
            Assert.Single(watcher.Sessions);
            Assert.Equal(["abc.jsonl"], watcher.LiveIds.Select(Path.GetFileName));
            clock = clock.AddSeconds(121);
            watcher.RefreshNow();
            Assert.Single(watcher.Sessions);       // still listed as recent
            Assert.Empty(watcher.LiveIds);         // but no longer live
            Assert.Null(watcher.PeekLine);
            Assert.Equal(2, changes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void RowsShowThreeSessionsAndCountTheRest()
    {
        var now = DateTimeOffset.Now;
        var sessions = Enumerable.Range(0, 5).Select(index => new AgentSession(
            $"{index}", index % 2 == 0 ? AgentKind.ClaudeCode : AgentKind.Codex, $"p{index}", null,
            index < 2 ? new AgentActivity.Thinking() : new AgentActivity.Idle(), now.AddSeconds(-60 * index))).ToList();
        var rows = AgentRow.For(sessions, now);
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Agent == "claude" && rows[0].IsLive && rows[0].Line == "thinking");
        Assert.Equal("done 2 min ago", rows[2].Line);
        Assert.Equal("+2 more", AgentRow.OverflowText(5));
        Assert.Null(AgentRow.OverflowText(3));
    }

    [Fact]
    public void ThePeekLineNamesALiveSessionWhenGivenOne()
    {
        Assert.Equal("claude · thinking", IslandTexts.PeekText("pointer follows you", "claude · thinking"));
        Assert.Equal("pointer follows you", IslandTexts.PeekText("pointer follows you", null));
    }

    // MARK: - Plan limits

    private const string Reply = """{"five_hour":{"utilization":38.0,"resets_at":"2026-10-21T15:00:00.000000+00:00"},"seven_day":{"utilization":37.0,"resets_at":"2026-10-25T09:00:00+00:00"},"extra_usage":{}}""";

    [Fact]
    public void TheReplyBecomesTwoWindows()
    {
        var usage = ClaudeUsage.Parse(Reply);
        Assert.NotNull(usage);
        Assert.Equal(0.38, usage.FiveHour.Fraction, 6);
        Assert.Equal(0.37, usage.SevenDay.Fraction, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T15:00:00Z"), usage.FiveHour.ResetsAt);
        Assert.Null(ClaudeUsage.Parse("{}"));
    }

    [Fact]
    public void ResetsReadShortAndTheSignInIsReadAtMostEveryHalfHour()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("resets 2 h 10 m", ClaudeUsagePolicy.ResetText(now.AddSeconds(2 * 3600 + 10 * 60 + 5), now));
        Assert.Equal("resets 35 m", ClaudeUsagePolicy.ResetText(now.AddSeconds(35 * 60), now));
        Assert.Equal("resets soon", ClaudeUsagePolicy.ResetText(now.AddSeconds(30), now));
        Assert.Equal("", ClaudeUsagePolicy.ResetText(null, now));
        Assert.True(ClaudeUsagePolicy.ShouldReadCredentials(null, now));
        Assert.False(ClaudeUsagePolicy.ShouldReadCredentials(now.AddSeconds(-1799), now));
        Assert.True(ClaudeUsagePolicy.ShouldReadCredentials(now.AddSeconds(-1801), now));
    }

    [Fact]
    public void TheCredentialsFileGivesItsAccessTokenOnly()
    {
        var token = FileClaudeCredentialReader.TokenFrom("""{"claudeAiOauth":{"accessToken":"tok","refreshToken":"r","expiresAt":1}}""");
        Assert.Equal(new ClaudeCredentialResult.Token("tok"), token);
        Assert.IsType<ClaudeCredentialResult.Missing>(FileClaudeCredentialReader.TokenFrom("""{"claudeAiOauth":{}}"""));
        Assert.IsType<ClaudeCredentialResult.Missing>(FileClaudeCredentialReader.TokenFrom("not json"));
        Assert.IsType<ClaudeCredentialResult.Missing>(new FileClaudeCredentialReader(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json")).ReadAccessToken());
    }

    private sealed class Reader : IClaudeCredentialReader
    {
        public ClaudeCredentialResult Result = new ClaudeCredentialResult.Missing();
        public int Reads;
        public ClaudeCredentialResult ReadAccessToken() { Reads++; return Result; }
    }

    [Fact]
    public async Task TheStoreTellsSignInFromARefusedFileFromStaleFigures()
    {
        var reader = new Reader();
        (string, int)? reply = (Reply, 200);
        var store = new ClaudeUsageStore(reader, (_, _) => Task.FromResult(reply));
        store.SetEnabled(true);
        await store.RefreshNowAsync();
        Assert.IsType<ClaudeUsageState.SignInNeeded>(store.State);
        await store.RefreshNowAsync();
        Assert.Equal(1, reader.Reads);            // no token is no reason to read the file again within 30 min
        store.SetEnabled(false);
        store.SetEnabled(true);
        reader.Result = new ClaudeCredentialResult.Refused();
        await store.RefreshNowAsync();
        Assert.IsType<ClaudeUsageState.CredentialsRefused>(store.State);
        await store.RefreshNowAsync();
        Assert.Equal(2, reader.Reads);            // a refusal is remembered
        store.SetEnabled(false);
        store.SetEnabled(true);
        reader.Result = new ClaudeCredentialResult.Token("t");
        await store.RefreshNowAsync();
        var figures = Assert.IsType<ClaudeUsageState.Figures>(store.State);
        Assert.False(figures.IsStale);
        Assert.Equal(0.38, figures.Usage.FiveHour.Fraction, 6);
        reply = (Reply, 500);
        await store.RefreshNowAsync();
        Assert.Equal(new ClaudeUsageState.Figures(figures.Usage, IsStale: true), store.State);
        reply = null;                             // the network failed
        await store.RefreshNowAsync();
        Assert.Equal(new ClaudeUsageState.Figures(figures.Usage, IsStale: true), store.State);
        reply = (Reply, 401);
        await store.RefreshNowAsync();
        Assert.IsType<ClaudeUsageState.SignInNeeded>(store.State);
    }
}
