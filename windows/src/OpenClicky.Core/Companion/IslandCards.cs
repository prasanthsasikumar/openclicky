//
//  IslandCards.cs
//  OpenClicky (Windows port of the card models in AppConnectPrompt.swift, FirstRunTour.swift and
//  NotchHUDPanels.swift's agent result card)
//
//  The cards the island draws: "connect <app> to openclicky?", the first-run tour's steps (with
//  their words, Windows keys in place of the Mac's), and the agent result card's state. The
//  controllers that decide when a card shows (AppConnectPromptController, FirstRunTourController)
//  belong to the orchestrator; the cards only raise what was clicked.
//

using CommunityToolkit.Mvvm.ComponentModel;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

// MARK: - Connect card

public enum AppConnectPromptStage
{
    /// <summary>never / not now / connect.</summary>
    Ask,
    /// <summary>The person said yes but Composio is not configured: explain, offer to open shell.json.</summary>
    ComposioMissing,
}

/// <summary>The "connect &lt;app&gt; to openclicky?" card.</summary>
/// <param name="AppName">What the card calls the app: the skill's name ("Gmail").</param>
/// <param name="Integration">The Composio toolkit from the skill's <c>integration</c>.</param>
/// <param name="FrontProcessPath">The front app when the card opened (its icon); the browser for a site skill.</param>
public sealed record AppConnectPrompt(
    string SkillId,
    string AppName,
    string Integration,
    string? FrontProcessPath,
    IReadOnlyList<string> ExamplePrompts,
    AppConnectPromptStage Stage = AppConnectPromptStage.Ask)
{
    public string Headline => Stage == AppConnectPromptStage.Ask
        ? $"connect {AppName.ToLowerInvariant()} to openclicky?"
        : $"{AppName.ToLowerInvariant()} skill is on";

    public string Detail => Stage == AppConnectPromptStage.Ask
        ? $"clicky could act in your {AppName.ToLowerInvariant()} account when you ask."
        : "account actions need composio: set COMPOSIO_MCP_URL in shell.json.";

    public string NeverTitle => $"never for {AppName.ToLowerInvariant()}";
    public string NeverToolTip => $"don't ask again; {AppName}'s skill is left out of what clicky knows";

    /// <summary>Records compare lists by reference; two cards with the same words are the same card.</summary>
    public bool Equals(AppConnectPrompt? other) =>
        other is not null && SkillId == other.SkillId && AppName == other.AppName && Integration == other.Integration
        && FrontProcessPath == other.FrontProcessPath && Stage == other.Stage && ExamplePrompts.SequenceEqual(other.ExamplePrompts);

    public override int GetHashCode() => HashCode.Combine(SkillId, AppName, Integration, Stage);
}

/// <summary>The connect card's buttons (AppConnectPromptController.Answer).</summary>
public enum AppConnectAnswer
{
    Yes,
    No,
    NotNow,
    DismissNotice,
    OpenSettings,
}

// MARK: - First-run tour

public enum FirstRunTourStep
{
    Meet,
    Dictate,
    Talk,
    Act,
    Account,
    Done,
    /// <summary>Not part of the tour: asked once, after a few uses.</summary>
    OpenAtLogin,
}

public enum FirstRunTourFieldKind
{
    Email,
    Code,
}

/// <summary>What the island's tour card shows.</summary>
/// <param name="Keys">Keycaps drawn pressed and held; empty → <see cref="Symbol"/>.</param>
/// <param name="Symbol">A Segoe Fluent Icons glyph name from <see cref="TourSymbols"/>, when there are no keys.</param>
/// <param name="Status">A line under the copy: what was heard, or why it did not work.</param>
/// <param name="MicrophoneName">The dictate step names the microphone it listens through, with a way to change it.</param>
public sealed record FirstRunTourCard(
    FirstRunTourStep Step,
    string Title,
    string Detail,
    IReadOnlyList<string> Keys,
    string? Symbol,
    string? Status,
    bool StatusIsGood,
    string? MicrophoneName,
    bool MicrophoneNeedsAttention,
    FirstRunTourFieldKind? Field,
    string? PrimaryTitle,
    string SecondaryTitle)
{
    /// <summary>The three practice steps get a progress dash each.</summary>
    public int? PracticeIndex => Step switch
    {
        FirstRunTourStep.Dictate => 0,
        FirstRunTourStep.Talk => 1,
        FirstRunTourStep.Act => 2,
        _ => null,
    };

    public bool Equals(FirstRunTourCard? other) =>
        other is not null && Step == other.Step && Title == other.Title && Detail == other.Detail && Keys.SequenceEqual(other.Keys)
        && Symbol == other.Symbol && Status == other.Status && StatusIsGood == other.StatusIsGood && MicrophoneName == other.MicrophoneName
        && MicrophoneNeedsAttention == other.MicrophoneNeedsAttention && Field == other.Field && PrimaryTitle == other.PrimaryTitle
        && SecondaryTitle == other.SecondaryTitle;

    public override int GetHashCode() => HashCode.Combine(Step, Title, Status, Field);
}

/// <summary>The tour's symbols (the Mac's SF Symbols); the island maps each to a Segoe Fluent Icons glyph.</summary>
public static class TourSymbols
{
    public const string Wave = "hand.wave.fill";
    public const string Waveform = "waveform";
    public const string Envelope = "envelope.fill";
    public const string Sparkles = "sparkles";
    public const string Eye = "eye.fill";
    public const string Checkmark = "checkmark.circle.fill";
    public const string Power = "power";
}

/// <summary>What one tour card needs to know to be written (FirstRunTourController.makeCard's inputs).</summary>
public sealed record FirstRunTourCardInputs(
    DictationHotkey DictationKey,
    bool CanAsk,
    bool UsesAgent,
    bool IsShowingDemo = false,
    bool AccountCardAsksForEmail = false,
    bool NeedsCode = false,
    string? Status = null,
    bool StatusIsGood = false,
    string? MicrophoneName = null,
    bool OffersMicrophoneChange = false,
    bool SuggestsBuiltInMicrophone = false);

/// <summary>The tour's cards, worded as on the Mac with the Windows keys and names ("right ctrl + windows", "this pc", the
/// island at the top). The Mac's speech-recognition and "free fn" cards have no Windows counterpart.</summary>
public static class FirstRunTourCards
{
    public static FirstRunTourCard Make(FirstRunTourStep step, FirstRunTourCardInputs inputs)
    {
        var none = Array.Empty<string>();
        var dictationKeyLabel = inputs.DictationKey.KeycapLabel();
        switch (step)
        {
            case FirstRunTourStep.Meet:
                return new FirstRunTourCard(step, "hi, i’m clicky",
                    "your pointer’s new sidekick. i’ll show you three things. it takes about a minute.",
                    none, TourSymbols.Wave, null, false, null, false, null, "let’s go", "skip tour");
            case FirstRunTourStep.Dictate:
                // Many laptops have no right Ctrl; right Alt (beside the space bar) works from its first press.
                return new FirstRunTourCard(step,
                    inputs.DictationKey == DictationHotkeys.Default ? "hold right ctrl or right alt and say something" : $"hold {dictationKeyLabel} and say something",
                    inputs.DictationKey == DictationHotkeys.Default
                        ? "no right ctrl? right alt is just right of the space bar. try “hello openclicky, this is my first take”, then let go."
                        : "try “hello openclicky, this is my first take”, then let go.",
                    new[] { IslandTexts.ShortKeycapLabel(inputs.DictationKey) }, null, inputs.Status, inputs.StatusIsGood,
                    inputs.MicrophoneName, inputs.OffersMicrophoneChange, null,
                    inputs.SuggestsBuiltInMicrophone ? "use built-in mic" : null, "skip");
            case FirstRunTourStep.Account when inputs.AccountCardAsksForEmail:
                return new FirstRunTourCard(step, "switch on asking",
                    "add your email for a free account and i’ll answer questions and do things for you. no password.",
                    none, TourSymbols.Envelope, inputs.Status, inputs.StatusIsGood, null, false,
                    inputs.NeedsCode ? FirstRunTourFieldKind.Code : FirstRunTourFieldKind.Email,
                    inputs.NeedsCode ? "confirm" : "start", "skip");
            case FirstRunTourStep.Account:
                return new FirstRunTourCard(step, "you’re on a free account",
                    "it has a limited number of free credits. if you already pay for openai, claude or another ai, add that key in settings and use it right here.",
                    none, TourSymbols.Sparkles, null, false, null, false, null, "add my key", "continue");
            case FirstRunTourStep.Talk when inputs.IsShowingDemo:
                return new FirstRunTourCard(step, "watch this",
                    "i’m looking at your screen. i’ll point at something and tell you about it.",
                    none, TourSymbols.Eye, null, false, null, false, null, null, "skip");
            case FirstRunTourStep.Talk:
                return new FirstRunTourCard(step, inputs.CanAsk ? "your turn: ask me something" : "you can ask me things too",
                    inputs.CanAsk
                        ? $"hold {inputs.DictationKey.TalkKeysText()} and ask “what’s this?” about anything on your screen. i answer out loud and point."
                        : "about anything on your screen, out loud, and i’ll point at it. add your email to switch it on, free.",
                    inputs.DictationKey.TalkKeycapLabels(), null, inputs.Status, inputs.StatusIsGood, null, false, null,
                    inputs.CanAsk ? "next" : "add email", "skip");
            case FirstRunTourStep.Act:
                return new FirstRunTourCard(step, inputs.CanAsk ? "hand me a task" : "i can do things for you too",
                    inputs.CanAsk ? TaskExamples(inputs.UsesAgent, inputs.DictationKey) : "open apps, make folders, and more, just by asking. add your email to switch it on, free.",
                    inputs.DictationKey.TalkKeycapLabels(), null, inputs.Status, inputs.StatusIsGood, null, false, null,
                    inputs.CanAsk ? "next" : "add email", "skip");
            case FirstRunTourStep.Done:
                return new FirstRunTourCard(step, "you’re all set",
                    "hover the island at the top any time. keys, microphone and the rest live in settings.",
                    none, TourSymbols.Checkmark, null, false, null, false, null, "open settings", "done");
            default:
                return new FirstRunTourCard(step, "open me when your pc starts?",
                    $"then {dictationKeyLabel} works from the moment you sign in, without opening me first.",
                    none, TourSymbols.Power, inputs.Status, inputs.StatusIsGood, null, false, null,
                    inputs.StatusIsGood ? null : "yes, open at sign-in", inputs.StatusIsGood ? "close" : "not now");
        }
    }

    /// <summary>What to try on the task step: the agent (own keys) works in apps; a free account has the quick actions.</summary>
    public static string TaskExamples(bool usesAgent, DictationHotkey dictationKey = DictationHotkeys.Default) => usesAgent
        ? $"hold {dictationKey.TalkKeysText()} and say “add a note to my notes: buy milk”, or “organize my downloads folder so it’s easier to find things”."
        : $"hold {dictationKey.TalkKeysText()} and say “make a folder on my desktop called hello clicky”, or “open notepad”.";

    /// <summary>The keycaps' press, pressed down together and held, then let go, every 2.4 s (KeyboardSketch.pressAmount).</summary>
    public static double PressAmount(double timeSeconds)
    {
        var phase = timeSeconds % 2.4;
        if (phase < 0) phase += 2.4;
        if (phase < 0.5) return 0;
        if (phase < 0.75) return (phase - 0.5) / 0.25;
        if (phase < 1.75) return 1;
        if (phase < 2.0) return 1 - (phase - 1.75) / 0.25;
        return 0;
    }
}

// MARK: - Agent result card

/// <summary>The floating card (top-right) when an agent finishes or one is opened: title, status, the agent's summary,
/// Copy, and a "Follow up with agent" field (AgentResultPanelState + what AgentResultView reads from CompanionManager).</summary>
public sealed partial class AgentResultCardModel : ObservableObject
{
    [ObservableProperty] private string threadId = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(StatusKind))] private string title = "Agent";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(StatusKind))] private string status = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isVisible;
    /// <summary>The companion is working (voice processing): the badge says "Working" and the field waits.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(StatusKind))] private bool isWorking;
    /// <summary>The follow-up field takes text only while the companion is idle.</summary>
    [ObservableProperty] private bool canFollowUp = true;
    /// <summary>The latest milestone of a running agent turn, shown under the field while working.</summary>
    [ObservableProperty] private string? agentActivityText;
    /// <summary>The freshest result for this thread (lastAgentResult within the hour), preferred over the loaded history.</summary>
    [ObservableProperty] private string? liveResultText;

    /// <summary>Follow up with agent: (text, thread id).</summary>
    public event Action<string, string>? FollowUpRequested;
    public event Action? CloseRequested;

    public string LiveSummary => string.IsNullOrEmpty(LiveResultText) ? Summary : LiveResultText!;

    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(LiveSummary));
    partial void OnLiveResultTextChanged(string? value) => OnPropertyChanged(nameof(LiveSummary));

    public string StatusLabel
    {
        get
        {
            if (IsWorking) return "Working";
            return Status switch
            {
                "completed" or "done" => "Done",
                "loading" => "…",
                "error" => "Error",
                "" => "",
                _ => char.ToUpperInvariant(Status[0]) + Status[1..],
            };
        }
    }

    public AgentResultStatusKind StatusKind =>
        IsWorking ? AgentResultStatusKind.Working : Status == "error" ? AgentResultStatusKind.Error : AgentResultStatusKind.Done;

    /// <summary>The card opens on a thread: "Agent", loading, until the orchestrator fills it in.</summary>
    public void BeginLoading(string threadIdToShow)
    {
        ThreadId = threadIdToShow;
        Title = "Agent";
        Summary = "";
        Status = "loading";
        IsLoading = true;
        LiveResultText = null;
        IsVisible = true;
    }

    public void SendFollowUp(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        FollowUpRequested?.Invoke(trimmed, ThreadId);
    }

    public void Close()
    {
        IsVisible = false;
        CloseRequested?.Invoke();
    }
}

public enum AgentResultStatusKind
{
    Done,
    Working,
    Error,
}
