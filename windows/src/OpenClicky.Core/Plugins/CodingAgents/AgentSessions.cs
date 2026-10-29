//
//  AgentSessions.cs
//  OpenClicky (Windows port of Plugins/CodingAgents/AgentSessions.swift)
//
//  The Coding agents plugin's eyes: what a Claude Code or Codex session is doing, read from the
//  session's own log on disk (no network, nothing written), when a session counts as live, and the
//  watcher that lists the recent ones. The parsers are pure; the watcher polls.
//
//  Windows keeps the logs where the Mac does, under the user's profile: %CLAUDE_CONFIG_DIR% (default
//  %USERPROFILE%\.claude)\projects and %USERPROFILE%\.codex\sessions.
//

using System.Text.Json.Nodes;
using OpenClicky.Core.Companion;

namespace OpenClicky.Core.Plugins.CodingAgents;

/// <summary>The plugin's component: the coding agents running on this PC.</summary>
public static class CodingAgentsPlugin
{
    /// <summary>Up to three session rows on the left, Claude's plan-limit rings on the right.</summary>
    public static IslandComponent Component { get; } = new(
        IslandPluginRegistry.AgentsId, "Coding agents", "", 76, IsPlugin: true,
        SettingsDetail: "Follows Claude Code and Codex sessions on this PC from their own logs, and reads Claude Code's sign-in on this PC for the plan limits.");
}

public enum AgentKind
{
    ClaudeCode,
    Codex,
}

public static class AgentKinds
{
    public static string DisplayName(this AgentKind kind) => kind == AgentKind.ClaudeCode ? "claude" : "codex";
}

/// <summary>What the session's last records say the agent is doing.</summary>
public abstract record AgentActivity
{
    public sealed record Tool(string Name, string? Detail) : AgentActivity;
    public sealed record Thinking : AgentActivity;
    public sealed record Writing(string Text) : AgentActivity;
    /// <summary>The last record is a prompt or a tool result: the model is working on it.</summary>
    public sealed record Waiting : AgentActivity;
    /// <summary>The answer is finished and nothing followed.</summary>
    public sealed record Idle : AgentActivity;

    /// <summary>One short line: "editing Hero.swift", "running: git status", "thinking", "writing", "waiting", "done".</summary>
    public string Line => this switch
    {
        Tool tool => ToolLine(tool.Name, tool.Detail),
        Thinking => "thinking",
        Writing => "writing",
        Waiting => "waiting",
        _ => "done",
    };

    private static string ToolLine(string name, string? detail)
    {
        var verb = name.ToLowerInvariant() switch
        {
            "edit" or "write" or "multiedit" or "notebookedit" or "apply_patch" => "editing",
            "read" => "reading",
            "bash" or "shell" or "exec" or "exec_command" or "shell_command" or "powershell" => "running:",
            "grep" or "glob" => "searching",
            "webfetch" or "websearch" => "browsing",
            var other => other,
        };
        if (string.IsNullOrEmpty(detail)) return verb == "running:" ? "running" : verb;
        return verb + " " + detail;
    }
}

public sealed record AgentSession(string Id, AgentKind Agent, string Project, string? Title, AgentActivity Activity, DateTimeOffset LastEventAt)
{
    /// <summary>How long after its last write a working session still counts as live.</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(120);
    /// <summary>How long a session stays listed after it stops.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(600);

    public bool IsLive(DateTimeOffset now) => Activity is not AgentActivity.Idle && now - LastEventAt < LiveWindow;
}

/// <summary>What the tail of one log yields.</summary>
public sealed record AgentTailParse(AgentActivity Activity, string? Title, string? Cwd);

public static class AgentSessionParser
{
    private const int DetailLength = 40;

    /// <summary>
    /// <c>.claude\projects\*\*.jsonl</c>: one JSON record a line. The activity comes from the last <c>assistant</c>
    /// record (idle once its answer finished), waiting when a <c>user</c> record follows it or no assistant record
    /// is in the tail. Other record types only lend their <c>cwd</c>.
    /// </summary>
    public static AgentTailParse ParseClaudeTail(string text)
    {
        string? cwd = null;
        string? title = null;
        JsonObject? lastAssistant = null;
        var userAfterAssistant = false;
        foreach (var record in Records(text))
        {
            if (String(record["cwd"]) is { } recordCwd) cwd = recordCwd;
            switch (String(record["type"]))
            {
                case "assistant":
                    lastAssistant = record;
                    userAfterAssistant = false;
                    break;
                case "user":
                    if (lastAssistant is not null) userAfterAssistant = true;
                    break;
                case "ai-title":
                    if (String(record["aiTitle"]) is { Length: > 0 } aiTitle) title = aiTitle;
                    break;
            }
        }
        AgentTailParse Parse(AgentActivity activity) => new(activity, title, cwd);
        if (lastAssistant?["message"] is not JsonObject message) return Parse(new AgentActivity.Waiting());
        if (userAfterAssistant) return Parse(new AgentActivity.Waiting());
        if (String(message["stop_reason"]) is "end_turn" or "stop_sequence") return Parse(new AgentActivity.Idle());
        if (message["content"] is not JsonArray { Count: > 0 } blocks || blocks[^1] is not JsonObject last) return Parse(new AgentActivity.Waiting());
        return String(last["type"]) switch
        {
            "tool_use" => Parse(new AgentActivity.Tool(String(last["name"]) ?? "tool",
                ToolDetail(String(last["input"]?["file_path"]), String(last["input"]?["command"])))),
            "thinking" => Parse(new AgentActivity.Thinking()),
            "text" => Parse(new AgentActivity.Writing(FirstLine(String(last["text"]) ?? "", 60))),
            _ => Parse(new AgentActivity.Waiting()),
        };
    }

    /// <summary><c>.codex\sessions\**\rollout-*.jsonl</c>: <c>session_meta</c> for the folder, <c>response_item</c>s for
    /// what the model does, <c>event_msg</c> <c>task_complete</c> once a turn is over.</summary>
    public static AgentTailParse ParseCodexTail(string text)
    {
        AgentActivity activity = new AgentActivity.Waiting();
        string? title = null;
        string? cwd = null;
        foreach (var record in Records(text))
        {
            if (record["payload"] is not JsonObject payload) continue;
            switch (String(record["type"]))
            {
                case "session_meta":
                    if (String(payload["cwd"]) is { } metaCwd) cwd = metaCwd;
                    break;
                case "response_item":
                    switch (String(payload["type"]))
                    {
                        case "function_call":
                            activity = new AgentActivity.Tool(String(payload["name"]) ?? "tool", CodexArgumentsDetail(String(payload["arguments"])));
                            break;
                        case "custom_tool_call":
                            var input = String(payload["input"]);
                            activity = new AgentActivity.Tool(String(payload["name"]) ?? "tool", input is null ? null : Prefix(input, DetailLength));
                            break;
                        case "reasoning":
                            activity = new AgentActivity.Thinking();
                            break;
                        case "message":
                            var messageText = (payload["content"] as JsonArray)?.Select(part => String(part?["text"])).FirstOrDefault(part => part is not null) ?? "";
                            if (String(payload["role"]) == "assistant")
                            {
                                activity = new AgentActivity.Writing(FirstLine(messageText, 60));
                            }
                            else
                            {
                                activity = new AgentActivity.Waiting();
                                if (title is null && messageText.Length > 0) title = FirstLine(messageText, 60);
                            }
                            break;
                    }
                    break;
                case "event_msg":
                    switch (String(payload["type"]))
                    {
                        case "task_complete": activity = new AgentActivity.Idle(); break;
                        case "task_started": activity = new AgentActivity.Waiting(); break;
                    }
                    break;
            }
        }
        return new AgentTailParse(activity, title, cwd);
    }

    /// <summary>OpenClicky's own plan answers run Codex in a scratch folder under the temporary directory; they are
    /// not the user's sessions.</summary>
    public static bool IsOwnScratchRollout(string? cwd)
    {
        if (cwd is null) return false;
        var scratchRoot = Path.Combine(Path.GetTempPath(), "openclicky-plan-answers");
        return cwd.StartsWith(scratchRoot, StringComparison.OrdinalIgnoreCase)
            || cwd.Contains("\\openclicky-plan-answers\\", StringComparison.OrdinalIgnoreCase)
            || cwd.Contains("/openclicky-plan-answers/", StringComparison.Ordinal);
    }

    // MARK: Pieces

    private static IEnumerable<JsonObject> Records(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(trimmed); }
            catch (System.Text.Json.JsonException) { continue; }
            if (node is JsonObject record) yield return record;
        }
    }

    /// <summary>The node's string value, or null for anything else.</summary>
    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? ToolDetail(string? filePath, string? command)
    {
        if (!string.IsNullOrEmpty(filePath)) return LastPathComponent(filePath);
        if (!string.IsNullOrEmpty(command)) return FirstLine(command, DetailLength).Trim();
        return null;
    }

    /// <summary>Codex tool arguments are a JSON string: a <c>command</c> (array or string) or a path.</summary>
    private static string? CodexArgumentsDetail(string? arguments)
    {
        if (arguments is null) return null;
        JsonObject? parsed;
        try { parsed = JsonNode.Parse(arguments) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (parsed is null) return null;
        if (parsed["command"] is JsonArray parts)
            return Prefix(string.Join(" ", parts.Select(part => String(part) ?? "")), DetailLength).Trim();
        if (String(parsed["command"]) is { } command) return FirstLine(command, DetailLength).Trim();
        if ((String(parsed["path"]) ?? String(parsed["file_path"])) is { } path) return LastPathComponent(path);
        return null;
    }

    /// <summary>The last component of a Windows or POSIX path (a log written on another machine may hold either).</summary>
    public static string LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private static string FirstLine(string text, int limit)
    {
        var line = text.Trim().Split('\n', '\r').FirstOrDefault(part => part.Length > 0) ?? "";
        return Prefix(line, limit);
    }

    private static string Prefix(string text, int limit) => text.Length > limit ? text[..limit] : text;
}

// MARK: - The watcher

/// <summary>Where the two agents keep their session logs.</summary>
public sealed record AgentLogRoots(string ClaudeProjects, string CodexSessions)
{
    /// <summary><c>%CLAUDE_CONFIG_DIR%</c> (default <c>%USERPROFILE%\.claude</c>) and <c>%USERPROFILE%\.codex</c>.</summary>
    public static AgentLogRoots Standard
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured ? configured : Path.Combine(home, ".claude");
            return new AgentLogRoots(Path.Combine(claudeConfig, "projects"), Path.Combine(home, ".codex", "sessions"));
        }
    }
}

/// <summary>
/// One per app. Lists the logs written in the last ten minutes, reads the tail of each, and polls: every 3 s while
/// a Coding agents row is on screen, every 20 s while the plugin is merely on (so the peek line can name a live
/// session), never otherwise. Start and stop it on the UI thread: the polls come back on the thread that started it.
/// </summary>
public sealed class AgentSessionWatcher(AgentLogRoots roots, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int TailBytes = 65_536;
    public static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(20);

    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.Now);
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    private bool isEnabled;
    private int watchers;
    private CancellationTokenSource? polling;

    /// <summary>Live first, then newest.</summary>
    public IReadOnlyList<AgentSession> Sessions { get; private set; } = [];
    /// <summary>Which of them are live right now: tracked on its own, so a session that simply stops writing (its
    /// records never change) still re-draws as "done" when its two minutes are up.</summary>
    public IReadOnlySet<string> LiveIds { get; private set; } = new HashSet<string>();

    /// <summary>The sessions or which of them are live changed.</summary>
    public event Action? Changed;

    public int LiveCount => LiveIds.Count;

    /// <summary>"claude · editing Foo.cs" for the newest live session, for the island's peek line.</summary>
    public string? PeekLine => Sessions.FirstOrDefault(session => LiveIds.Contains(session.Id)) is { } live
        ? $"{live.Agent.DisplayName()} · {live.Activity.Line}"
        : null;

    public bool IsEnabled => isEnabled;
    public bool IsPolling => polling is not null;
    public TimeSpan PollInterval => watchers > 0 ? WatchedInterval : BackgroundInterval;

    /// <summary>The plugin's switch: on starts the slow poll, off stops everything and forgets.</summary>
    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled) return;
        isEnabled = enabled;
        if (enabled) RefreshNow();
        else Publish([]);
        RestartPolling();
    }

    /// <summary>A row appeared: poll fast while any is on screen.</summary>
    public void BeginWatching()
    {
        watchers++;
        if (watchers != 1) return;
        if (isEnabled) RefreshNow();
        RestartPolling();
    }

    public void EndWatching()
    {
        watchers = Math.Max(0, watchers - 1);
        if (watchers == 0) RestartPolling();
    }

    /// <summary>
    /// One pass over both trees. The walks are bounded to where the logs live: Claude's <c>projects\&lt;folder&gt;\&lt;id&gt;.jsonl</c>
    /// (a session's subagent transcripts sit a level deeper and are not sessions of their own), Codex's
    /// <c>sessions\&lt;year&gt;\&lt;month&gt;\&lt;day&gt;\rollout-*.jsonl</c> with only the recent days opened.
    /// </summary>
    public void RefreshNow()
    {
        var current = now();
        var cutoff = current - AgentSession.RecentWindow;
        var found = new List<AgentSession>();
        foreach (var folder in Subdirectories(roots.ClaudeProjects))
        {
            foreach (var (path, modifiedAt) in RecentFiles(folder, cutoff))
            {
                if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                var parse = AgentSessionParser.ParseClaudeTail(Tail(path));
                found.Add(new AgentSession(path, AgentKind.ClaudeCode, Project(parse.Cwd, "claude"), parse.Title, parse.Activity, modifiedAt));
            }
        }
        foreach (var day in RecentCodexDayFolders(roots.CodexSessions, cutoff, current))
        {
            foreach (var (path, modifiedAt) in RecentFiles(day, cutoff))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith("rollout-", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                var parse = AgentSessionParser.ParseCodexTail(Tail(path));
                if (AgentSessionParser.IsOwnScratchRollout(parse.Cwd)) continue;
                found.Add(new AgentSession(path, AgentKind.Codex, Project(parse.Cwd, "codex"), parse.Title, parse.Activity, modifiedAt));
            }
        }
        Publish(found.OrderByDescending(session => session.IsLive(current)).ThenByDescending(session => session.LastEventAt).ToList());
    }

    private void Publish(IReadOnlyList<AgentSession> found)
    {
        var current = now();
        var live = found.Where(session => session.IsLive(current)).Select(session => session.Id).ToHashSet();
        var changed = !found.SequenceEqual(Sessions) || !live.SetEquals(LiveIds);
        Sessions = found;
        LiveIds = live;
        if (changed) Changed?.Invoke();
    }

    // MARK: Pieces

    private void RestartPolling()
    {
        polling?.Cancel();
        polling = null;
        if (!isEnabled) return;
        var cancellation = new CancellationTokenSource();
        polling = cancellation;
        _ = PollAsync(PollInterval, cancellation.Token);
    }

    private async Task PollAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await delay(interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (cancellationToken.IsCancellationRequested) return;
            RefreshNow();
        }
    }

    private static IEnumerable<string> Subdirectories(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetDirectories(directory) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The regular files right inside <paramref name="directory"/> written after <paramref name="cutoff"/>.</summary>
    private static IEnumerable<(string Path, DateTimeOffset ModifiedAt)> RecentFiles(string directory, DateTimeOffset cutoff)
    {
        string[] files;
        try { files = Directory.Exists(directory) ? Directory.GetFiles(directory) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
        {
            DateTimeOffset modifiedAt;
            try { modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (modifiedAt > cutoff) yield return (file, modifiedAt);
        }
    }

    /// <summary><c>sessions\&lt;year&gt;\&lt;month&gt;\&lt;day&gt;</c> folders for the days the recent window touches (today and, just
    /// after midnight, yesterday); older days are never opened. Codex names them by local date.</summary>
    private static IEnumerable<string> RecentCodexDayFolders(string root, DateTimeOffset cutoff, DateTimeOffset current) =>
        new[] { cutoff, current }
            .Select(date => date.ToLocalTime())
            .Select(date => Path.Combine(root, date.Year.ToString("D4"), date.Month.ToString("D2"), date.Day.ToString("D2")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last 64 KB of the log, from the first whole line. Opened for sharing: the agent is writing it.</summary>
    private static string Tail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (start > 0 && text.IndexOf('\n') is var newline and >= 0) text = text[(newline + 1)..];
            return text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Project(string? cwd, string fallback) =>
        string.IsNullOrEmpty(cwd) ? fallback : AgentSessionParser.LastPathComponent(cwd);
}

/// <summary>One session row, ready to draw.</summary>
public sealed record AgentRow(string Id, string Agent, string Project, string Line, bool IsLive)
{
    public const int MaximumRows = 3;

    /// <summary>The first three sessions as rows; a stopped one says how long ago.</summary>
    public static IReadOnlyList<AgentRow> For(IReadOnlyList<AgentSession> sessions, DateTimeOffset now) =>
        sessions.Take(MaximumRows).Select(session =>
        {
            var isLive = session.IsLive(now);
            string line;
            if (isLive)
            {
                line = session.Activity.Line;
            }
            else
            {
                var minutes = Math.Max(0, (int)((now - session.LastEventAt).TotalSeconds / 60));
                line = minutes == 0 ? "done just now" : $"done {minutes} min ago";
            }
            return new AgentRow(session.Id, session.Agent.DisplayName(), session.Project, line, isLive);
        }).ToList();

    public static string? OverflowText(int count) => count > MaximumRows ? $"+{count - MaximumRows} more" : null;
}
