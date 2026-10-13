// Port of the logic in macos/OpenClicky/OpenClicky/AppConnectPrompt.swift (the card itself is drawn
// by the island in OpenClicky.Windows, bound to AppConnectPromptController).
//
// The "connect <app> to openclicky?" card. When an app or site that has an app-teaching skill
// comes to the front for the first time, the island opens with the app's icon, example prompts
// taken from the skill, and never for <app> / not now / connect.
//   connect         remembers the app as connected and, when Composio is configured, asks the agent
//                   to connect the account (the Composio toolkit named by the skill's `integration`);
//                   without Composio the card explains what to set up instead of pretending
//   never for <app> remembers the app as declined: its skill is left out of the voice prompts
//   not now         hides the card until the app next launches
// Only app skills with an `integration` (an account behind the app) get the card: Terminal,
// File Explorer or Visual Studio have nothing to connect.
//
// Windows: the front app is a process image name (chrome.exe) rather than a bundle id, so the card
// keeps the process name and executable path for its icon; "not now" is tracked by process id as on
// the Mac.

using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Skills;

namespace OpenClicky.Core.Agent;

public enum AppConnectPromptStage
{
    /// <summary>No / Not now / Yes.</summary>
    Ask,
    /// <summary>The user said Yes but Composio is not configured: explain, offer to open the settings file.</summary>
    ComposioMissing,
}

public sealed record AppConnectPrompt
{
    public required string SkillId { get; init; }

    /// <summary>What the card calls the app: the skill's name ("Gmail", "YouTube").</summary>
    public required string AppName { get; init; }

    /// <summary>The Composio toolkit that connects the account ("youtube"), from the skill's `integration`.</summary>
    public required string Integration { get; init; }

    /// <summary>The frontmost app's bundle identifier when the card opened (Mac skills / tests); usually null on Windows.</summary>
    public string? FrontBundleIdentifier { get; init; }

    /// <summary>The frontmost app's image name when the card opened (the browser for a site skill).</summary>
    public string? FrontProcessName { get; init; }

    /// <summary>The frontmost app's executable, for its icon (IFrontmostAppObserver.AppIconPng); null shows a globe.</summary>
    public string? FrontExecutablePath { get; init; }

    public IReadOnlyList<string> ExamplePrompts { get; init; } = [];

    public AppConnectPromptStage Stage { get; init; } = AppConnectPromptStage.Ask;

    /// <summary>The card's first line.</summary>
    public string Headline => Stage switch
    {
        AppConnectPromptStage.Ask => $"connect {AppName.ToLowerInvariant()} to openclicky?",
        _ => $"{AppName.ToLowerInvariant()} skill is on",
    };

    /// <summary>The card's second line.</summary>
    public string Detail => Stage switch
    {
        AppConnectPromptStage.Ask => $"clicky could act in your {AppName.ToLowerInvariant()} account when you ask.",
        _ => "account actions need composio: set COMPOSIO_MCP_URL in shell.json.",
    };

    /// <summary>The "No" button's title.</summary>
    public string NeverButtonTitle => $"never for {AppName.ToLowerInvariant()}";

    /// <summary>The "No" button's tooltip.</summary>
    public string NeverButtonHelp => $"don't ask again; {AppName}'s skill is left out of what clicky knows";

    /// <summary>The chips the card shows (at most four; the island leaves out any that don't fit whole).</summary>
    public IReadOnlyList<string> ExampleChips => ExamplePrompts.Take(4).Select(example => $"“{example}”").ToList();

    public bool Equals(AppConnectPrompt? other) =>
        other is not null && SkillId == other.SkillId && AppName == other.AppName && Integration == other.Integration
        && FrontBundleIdentifier == other.FrontBundleIdentifier && FrontProcessName == other.FrontProcessName
        && FrontExecutablePath == other.FrontExecutablePath && Stage == other.Stage && ExamplePrompts.SequenceEqual(other.ExamplePrompts);

    public override int GetHashCode() => HashCode.Combine(SkillId, Stage);
}

/// <summary>The example prompts a card shows for a skill, read out of the SKILL.md body.</summary>
public static class AppSkillExamples
{
    private static readonly char[] QuoteCharacters = ['"', '“', '”'];

    /// <summary>
    /// The quoted questions in "## Pointing hints" bullets (`- "How do I …?" — point at …`) first,
    /// then the lead phrase of every "## Common tasks" bullet (`- Compose: …` → "Compose"), up to
    /// <paramref name="limit"/> entries and without duplicates. Skills with neither section yield an empty list.
    /// </summary>
    public static IReadOnlyList<string> Extract(string body, int limit = 6)
    {
        var examples = new List<string>();
        var lines = body.Split('\n').Select(line => line.Trim(' ', '\t', '\r')).ToList();
        var currentSection = "";
        foreach (var line in lines)
        {
            if (line.StartsWith('#'))
            {
                currentSection = line.Trim('#', ' ').ToLowerInvariant();
                continue;
            }
            if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
            var bullet = line[2..];
            if (currentSection.Contains("pointing", StringComparison.Ordinal) && FirstQuotedPhrase(bullet) is { } quoted)
            {
                Append(quoted, examples);
            }
        }
        if (examples.Count < limit)
        {
            currentSection = "";
            foreach (var line in lines)
            {
                if (line.StartsWith('#'))
                {
                    currentSection = line.Trim('#', ' ').ToLowerInvariant();
                    continue;
                }
                if (!currentSection.Contains("common tasks", StringComparison.Ordinal) || !line.StartsWith("- ", StringComparison.Ordinal)) continue;
                var bullet = line[2..];
                var colon = bullet.IndexOf(':');
                if (colon >= 0)
                {
                    var lead = bullet[..colon].Trim(' ', '\t');
                    if (lead.Length > 0 && lead.Length <= 48) Append(lead, examples);
                }
            }
        }
        return examples.Take(limit).ToList();
    }

    /// <summary>The text between the first pair of straight or curly double quotes.</summary>
    private static string? FirstQuotedPhrase(string text)
    {
        var open = text.IndexOfAny(QuoteCharacters);
        if (open < 0) return null;
        var close = text.IndexOfAny(QuoteCharacters, open + 1);
        if (close < 0) return null;
        var trimmed = text[(open + 1)..close].Trim(' ', '\t');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void Append(string example, List<string> examples)
    {
        if (!examples.Any(existing => string.Equals(existing, example, StringComparison.OrdinalIgnoreCase))) examples.Add(example);
    }
}

public enum AppConnectDecision
{
    Connected,
    Declined,
}

public enum AppConnectAnswer
{
    Yes,
    No,
    NotNow,
    /// <summary>Dismisses the "Composio missing" notice.</summary>
    DismissNotice,
    /// <summary>Dismisses the notice and reveals shell.json.</summary>
    OpenSettings,
}

/// <summary>
/// Watches the frontmost app / browser site, opens the card the first time a skill matches, and
/// remembers the answers in Preferences (`appSkillConnectDecisions`: skill id → "connected" |
/// "declined"). The talk lanes ask <see cref="DeclinedSkillIds"/> before injecting an app skill.
/// The island binds to <see cref="CurrentPrompt"/>; changes are raised on the main thread when one
/// is given.
/// </summary>
public sealed partial class AppConnectPromptController : ObservableObject, IDisposable
{
    public const string DecisionsDefaultsKey = "appSkillConnectDecisions";

    /// <summary>The front context is polled this often (browser tab changes raise no event).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>How long the "Composio missing" notice stays before it closes by itself.</summary>
    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(15);

    private readonly Preferences preferences;
    private readonly IMainThread? mainThread;
    private readonly object stateLock = new();
    private readonly Dictionary<string, int> dismissedSkillIdsByProcessIdentifier = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, AppConnectDecision> decisions;
    private IFrontmostAppObserver? frontmostAppObserver;
    private Func<IReadOnlyList<SkillFile>>? appSkills;
    private Timer? pollTimer;
    private CancellationTokenSource? noticeDismissal;
    private int isPolling;

    [ObservableProperty]
    private AppConnectPrompt? currentPrompt;

    public AppConnectPromptController(Preferences? preferences = null, IMainThread? mainThread = null)
    {
        this.preferences = preferences ?? Preferences.Shared;
        this.mainThread = mainThread;
        decisions = ReadDecisions(this.preferences);
    }

    /// <summary>Every remembered answer, by skill id.</summary>
    public IReadOnlyDictionary<string, AppConnectDecision> Decisions
    {
        get { lock (stateLock) return decisions; }
    }

    public IReadOnlySet<string> DeclinedSkillIds
    {
        get
        {
            lock (stateLock)
            {
                return decisions.Where(pair => pair.Value == AppConnectDecision.Declined).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Whether Composio (the account integrations) is configured; `Yes` connects through it.</summary>
    public Func<bool> IsComposioConfigured { get; set; } = () => OpenClickyConfiguration.ActiveComposioMcpUrl is not null;

    /// <summary>
    /// Hands a task to the agent lane (Codex with the Composio MCP server) and opens its result card.
    /// The companion wires it to <see cref="AppConnectFlow.ConnectIntegrationThenRunAsync"/>.
    /// </summary>
    public Action<string>? SubmitToAgent { get; set; }

    /// <summary>"open shell.json": reveals the settings file (the Windows side sets it; explorer /select).</summary>
    public Action? RevealSettingsFile { get; set; }

    /// <summary>The frontmost app's process id, for "not now". Defaults to the observer's front app.</summary>
    public Func<int> FrontProcessIdentifier { get; set; } = () => 0;

    /// <summary>
    /// Starts polling the front app. <paramref name="appSkills"/> returns the app skills now
    /// (SkillLibraryStore.AppSkills); the controller keeps the talk ones.
    /// </summary>
    public void Start(IFrontmostAppObserver frontmostAppObserver, Func<IReadOnlyList<SkillFile>> appSkills,
                      Action<string> submitToAgent, Func<bool>? isComposioConfigured = null)
    {
        this.frontmostAppObserver = frontmostAppObserver;
        this.appSkills = appSkills;
        SubmitToAgent = submitToAgent;
        if (isComposioConfigured is not null) IsComposioConfigured = isComposioConfigured;
        FrontProcessIdentifier = () => frontmostAppObserver.CurrentWithoutUrl()?.ProcessId ?? 0;
        pollTimer?.Dispose();
        pollTimer = new Timer(_ => _ = PollFrontmostAppAsync(), null, TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        pollTimer?.Dispose();
        pollTimer = null;
        SetCurrentPrompt(null);
    }

    public void Dispose() => Stop();

    /// <summary>The card's buttons.</summary>
    public void Answer(AppConnectAnswer answer)
    {
        if (CurrentPrompt is not { } prompt) return;
        SetCurrentPrompt(Apply(answer, prompt));
    }

    /// <summary>
    /// Records one answer for a card and returns what the island should show next: null to close, or
    /// the same card in its "Composio missing" stage. (The buttons call <see cref="Answer"/>; tests call this.)
    /// </summary>
    public AppConnectPrompt? Apply(AppConnectAnswer answer, AppConnectPrompt prompt)
    {
        AppLog.Write($"app connect: {prompt.AppName} → {answer}");
        switch (answer)
        {
            case AppConnectAnswer.Yes:
                Record(AppConnectDecision.Connected, prompt.SkillId);
                if (!IsComposioConfigured()) return prompt with { Stage = AppConnectPromptStage.ComposioMissing };
                SubmitToAgent?.Invoke(ConnectionTask(prompt));
                return null;
            case AppConnectAnswer.No:
                Record(AppConnectDecision.Declined, prompt.SkillId);
                return null;
            case AppConnectAnswer.NotNow:
                lock (stateLock) dismissedSkillIdsByProcessIdentifier[prompt.SkillId] = FrontProcessIdentifier();
                return null;
            case AppConnectAnswer.OpenSettings:
                RevealSettingsFile?.Invoke();
                return null;
            default:
                return null;
        }
    }

    /// <summary>What the agent is asked to do when the user connects an app through Composio.</summary>
    public static string ConnectionTask(AppConnectPrompt prompt) =>
        $"Connect my {prompt.AppName} account through the composio MCP tools (toolkit \"{prompt.Integration}\"). " +
        "Call COMPOSIO_MANAGE_CONNECTIONS to start the connection; if it returns an authorization link, run `explorer \"<link>\"` so it opens in my browser and also print it on its own line, " +
        $"then call COMPOSIO_WAIT_FOR_CONNECTIONS. When the account is connected (or already was), use COMPOSIO_SEARCH_TOOLS to tell me in one short paragraph what you can now do in {prompt.AppName}.";

    /// <summary>The frontmost skill match, or null. Exposed for tests; the poll uses the same rule.</summary>
    public AppConnectPrompt? Prompt(FrontAppContext front, IReadOnlyList<SkillFile> skills, string? frontExecutablePath = null)
    {
        if (AppSkillMatcher.Match(front, skills) is not { } skill) return null;
        // Only apps with an account to connect (Gmail, YouTube…); Terminal or File Explorer have none.
        if (skill.Integration is not { } integration) return null;
        lock (stateLock)
        {
            if (decisions.ContainsKey(skill.Id)) return null;
            if (dismissedSkillIdsByProcessIdentifier.TryGetValue(skill.Id, out var dismissedProcessIdentifier))
            {
                if (dismissedProcessIdentifier == FrontProcessIdentifier()) return null;
                dismissedSkillIdsByProcessIdentifier.Remove(skill.Id);
            }
        }
        return new AppConnectPrompt
        {
            SkillId = skill.Id,
            AppName = skill.Name,
            Integration = integration,
            FrontBundleIdentifier = front.BundleIdentifier,
            FrontProcessName = front.ProcessName,
            FrontExecutablePath = frontExecutablePath,
            ExamplePrompts = AppSkillExamples.Extract(skill.Body),
        };
    }

    /// <summary>The matcher's view of a front app the observer reported.</summary>
    public static FrontAppContext Context(FrontAppInfo front) => new()
    {
        ProcessName = front.ProcessName,
        AppName = front.DisplayName,
        Url = UrlFrom(front.Url),
        WindowTitle = front.WindowTitle,
        IsBrowser = front.IsBrowser,
    };

    private static Uri? UrlFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") return url;
        // An address bar often shows the host without its scheme.
        return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var withScheme) ? withScheme : null;
    }

    // MARK: - Private

    private async Task PollFrontmostAppAsync()
    {
        if (frontmostAppObserver is not { } observer || appSkills is not { } skillsSource) return;
        // A notice stays until dismissed (or its timer runs out); the poll must not replace it.
        if (CurrentPrompt?.Stage == AppConnectPromptStage.ComposioMissing) return;
        if (Interlocked.Exchange(ref isPolling, 1) == 1) return;
        try
        {
            var skills = skillsSource().Where(skill => skill.IsForTalk).ToList();
            var front = await observer.CurrentAsync(includeUrl: true).ConfigureAwait(false);
            var prompt = front is null ? null : Prompt(Context(front), skills, front.ExecutablePath);
            if (CurrentPrompt?.Stage == AppConnectPromptStage.ComposioMissing) return;
            SetCurrentPrompt(prompt);
        }
        catch (Exception exception)
        {
            AppLog.Write($"app connect: poll failed: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref isPolling, 0);
        }
    }

    private void SetCurrentPrompt(AppConnectPrompt? prompt)
    {
        void Apply()
        {
            if (Equals(CurrentPrompt, prompt)) return;
            CurrentPrompt = prompt;
            noticeDismissal?.Cancel();
            noticeDismissal = null;
            if (prompt?.Stage == AppConnectPromptStage.ComposioMissing)
            {
                var dismissal = new CancellationTokenSource();
                noticeDismissal = dismissal;
                _ = DismissNoticeLaterAsync(dismissal.Token);
            }
        }
        if (mainThread is null || mainThread.IsMainThread) Apply();
        else mainThread.Post(Apply);
    }

    private async Task DismissNoticeLaterAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(NoticeDuration, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (cancellationToken.IsCancellationRequested || CurrentPrompt?.Stage != AppConnectPromptStage.ComposioMissing) return;
        SetCurrentPrompt(null);
    }

    private void Record(AppConnectDecision decision, string skillId)
    {
        JsonObject stored;
        lock (stateLock)
        {
            decisions = new Dictionary<string, AppConnectDecision>(decisions, StringComparer.Ordinal) { [skillId] = decision };
            stored = new JsonObject();
            foreach (var (id, value) in decisions) stored[id] = RawValue(value);
        }
        preferences.SetJson(DecisionsDefaultsKey, stored);
        OnPropertyChanged(nameof(Decisions));
        OnPropertyChanged(nameof(DeclinedSkillIds));
    }

    private static string RawValue(AppConnectDecision decision) => decision == AppConnectDecision.Declined ? "declined" : "connected";

    private static IReadOnlyDictionary<string, AppConnectDecision> ReadDecisions(Preferences preferences)
    {
        var result = new Dictionary<string, AppConnectDecision>(StringComparer.Ordinal);
        if (preferences.Object(DecisionsDefaultsKey) is not JsonObject stored) return result;
        foreach (var (id, value) in stored)
        {
            var raw = AgentJson.String(value);
            if (raw == "connected") result[id] = AppConnectDecision.Connected;
            else if (raw == "declined") result[id] = AppConnectDecision.Declined;
        }
        return result;
    }
}

/// <summary>CompanionManager.connectIntegrationThenRun: what the card's "connect" does once Composio is configured.</summary>
public static class AppConnectFlow
{
    /// <summary>The activity line shown while the browser sign-in is open.</summary>
    public const string SigningInActivityText = "Signing in to Composio…";

    /// <summary>
    /// Makes sure Codex is logged into Composio (opens Composio's page in the browser the first time
    /// on this PC), then hands the agent the account-connection task. A failed status check or
    /// login is logged and the task still goes to the agent, which reports what is missing.
    /// </summary>
    public static async Task ConnectIntegrationThenRunAsync(OpenClickyAgentClient agentClient, string task,
                                                            Action<string> submitTextToAgent, Action<string?>? setActivityText = null,
                                                            CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await agentClient.IntegrationsStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Configured && !status.LoggedIn)
            {
                setActivityText?.Invoke(SigningInActivityText);
                await agentClient.LoginIntegrationAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                setActivityText?.Invoke(null);
            }
        }
        catch (Exception exception) when (exception is OpenClickyAgentException or ProcessRunnerException or IOException)
        {
            setActivityText?.Invoke(null);
            AppLog.Write($"app connect: Composio login failed: {exception.Message}");
        }
        submitTextToAgent(task);
    }
}
