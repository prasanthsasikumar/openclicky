//
//  PermissionPrompt.cs
//  OpenClicky (Windows port of PermissionPrompt.swift's model and controller)
//
//  The permission card on the island: one permission at a time, a row of dashes for progress, a
//  headline, one line of copy, grant and later. On Windows the microphone is a permission
//  (Settings → Privacy & security → Microphone), and so is "Online speech recognition" (Settings →
//  Privacy & security → Speech), but only once Windows speech has refused to dictate without it.
//  Accessibility, Screen Recording and Screen Content have no Windows counterpart, so their steps,
//  the drag helper and the relaunch flows are gone.
//

using OpenClicky.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClicky.Core.Companion;

public enum PermissionStep
{
    Microphone,
    /// <summary>Windows' "Online speech recognition", which the Windows speech engine cannot dictate without.</summary>
    OnlineSpeech,
}

public static class PermissionSteps
{
    /// <summary>The cards' order (the Mac's was Accessibility → Microphone → Screen Recording → Screen Content).</summary>
    public static readonly IReadOnlyList<PermissionStep> AllCases = new[] { PermissionStep.Microphone, PermissionStep.OnlineSpeech };

    /// <summary>The steps this person's cards count: online speech only while it is being asked for.</summary>
    public static IReadOnlyList<PermissionStep> Relevant(PermissionStatus status) =>
        AllCases.Where(step => step != PermissionStep.OnlineSpeech || !status.OnlineSpeech).ToList();

    /// <summary>What the permission is for, in the card's own words.</summary>
    public static string Headline(this PermissionStep step) => step switch
    {
        PermissionStep.OnlineSpeech => L.T("windows dictation needs online speech"),
        _ => L.T("i need the microphone to hear you"),
    };

    /// <summary>The line under the headline before the button was pressed.</summary>
    public static string Detail(this PermissionStep step) => step switch
    {
        PermissionStep.OnlineSpeech => L.T("it sends what you dictate to microsoft’s speech service. turn it on?"),
        _ => L.T("only while you hold a shortcut or go hands-free."),
    };

    /// <summary>The line once the request is out and Windows' privacy settings are open.</summary>
    public static string WaitingDetail(this PermissionStep step) => step switch
    {
        PermissionStep.OnlineSpeech => L.T("switch on online speech recognition in settings, then dictate again."),
        _ => L.T("switch on microphone access for desktop apps in settings."),
    };

    public static string ButtonTitle(this PermissionStep step) => step switch
    {
        PermissionStep.OnlineSpeech => L.T("turn on"),
        _ => L.T("grant"),
    };

    public static string WaitingButtonTitle(this PermissionStep step) => step switch
    {
        _ => L.T("open"),
    };
}

/// <summary>The permission flags, snapshotted so the card logic is testable without the OS. OnlineSpeech is
/// true unless Windows speech refused to dictate without it.</summary>
public sealed record PermissionStatus(bool Microphone = false, bool OnlineSpeech = true)
{
    public bool IsGranted(PermissionStep step) => step switch
    {
        PermissionStep.OnlineSpeech => OnlineSpeech,
        _ => Microphone,
    };
}

public enum PermissionPromptStage
{
    /// <summary>The button still offers to ask for the permission.</summary>
    Ask,
    /// <summary>The request is out: Windows has the ball, and the card explains where to look.</summary>
    Waiting,
}

public sealed record PermissionPrompt(PermissionStep Step, PermissionStatus Status, PermissionPromptStage Stage = PermissionPromptStage.Ask)
{
    public string Headline => Step.Headline();
    public string Detail => Stage == PermissionPromptStage.Waiting ? Step.WaitingDetail() : Step.Detail();
    public string ButtonTitle => Stage == PermissionPromptStage.Waiting ? Step.WaitingButtonTitle() : Step.ButtonTitle();

    /// <summary>The Mac's stale-Accessibility-row escape hatch; there is no such row on Windows.</summary>
    public bool OffersAccessibilityTrustReset => false;

    /// <summary>"step 1 of 1", what a screen reader says for the dashes.</summary>
    public string ProgressLabel
    {
        get
        {
            var steps = PermissionSteps.Relevant(Status);
            return L.F("step {0} of {1}", steps.ToList().IndexOf(Step) + 1, steps.Count);
        }
    }
}

/// <summary>Turns the permission flags into the one card the island should show, and performs the grant the button asks
/// for. The orchestrator feeds it from its permission poll, so the card goes away on its own once access lands.</summary>
public sealed partial class PermissionPromptController : ObservableObject
{
    [ObservableProperty] private PermissionPrompt? currentPrompt;

    /// <summary>The card's "later": no more cards until the next launch.</summary>
    public bool IsDismissedForThisLaunch { get; private set; }

    /// <summary>Performs the request for a step (opens ms-settings:privacy-microphone, or switches online speech
    /// on). Injected so tests open nothing. A request that lands at once refreshes the status itself.</summary>
    public Action<PermissionStep> PerformRequest { get; set; } = _ => { };

    /// <summary>The gear on the card: the window's settings.</summary>
    public Action OpenSettings { get; set; } = () => { };

    /// <summary>The first permission that is still missing, in the cards' order.</summary>
    public static PermissionStep? NextStep(PermissionStatus status)
    {
        foreach (var step in PermissionSteps.AllCases)
        {
            if (!status.IsGranted(step)) return step;
        }
        return null;
    }

    /// <summary>Called on every permission refresh. Keeps the stage while asking for the same permission.</summary>
    public void Update(PermissionStatus status)
    {
        if (IsDismissedForThisLaunch || NextStep(status) is not { } step)
        {
            CurrentPrompt = null;
            return;
        }
        var stage = CurrentPrompt?.Step == step ? CurrentPrompt.Stage : PermissionPromptStage.Ask;
        CurrentPrompt = new PermissionPrompt(step, status, stage);
    }

    /// <summary>The card's button: ask Windows for the current permission, then wait on the person.</summary>
    public void Grant()
    {
        if (CurrentPrompt is not { } prompt) return;
        PerformRequest(prompt.Step);
        // Online speech is switched on directly: the refresh inside the request has already dropped the card.
        if (CurrentPrompt?.Step == prompt.Step) CurrentPrompt = CurrentPrompt with { Stage = PermissionPromptStage.Waiting };
    }

    public void Dismiss()
    {
        IsDismissedForThisLaunch = true;
        CurrentPrompt = null;
    }

    public void OpenPanel() => OpenSettings();
}
