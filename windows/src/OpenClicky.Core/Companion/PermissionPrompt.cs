//
//  PermissionPrompt.cs
//  OpenClicky (Windows port of PermissionPrompt.swift's model and controller)
//
//  The permission card on the island: one permission at a time, a row of dashes for progress, a
//  headline, one line of copy, grant and later. On Windows only the microphone is a permission
//  (Settings → Privacy & security → Microphone); Accessibility, Screen Recording and Screen Content
//  have no Windows counterpart, so their steps, the drag helper and the relaunch flows are gone.
//

using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenClicky.Core.Companion;

public enum PermissionStep
{
    Microphone,
}

public static class PermissionSteps
{
    /// <summary>The cards' order (the Mac's was Accessibility → Microphone → Screen Recording → Screen Content).</summary>
    public static readonly IReadOnlyList<PermissionStep> AllCases = new[] { PermissionStep.Microphone };

    /// <summary>What the permission is for, in the card's own words.</summary>
    public static string Headline(this PermissionStep step) => step switch
    {
        _ => "i need the microphone to hear you",
    };

    /// <summary>The line under the headline before the button was pressed.</summary>
    public static string Detail(this PermissionStep step) => step switch
    {
        _ => "only while you hold a shortcut or go hands-free.",
    };

    /// <summary>The line once the request is out and Windows' privacy settings are open.</summary>
    public static string WaitingDetail(this PermissionStep step) => step switch
    {
        _ => "switch on microphone access for desktop apps in settings.",
    };

    public static string ButtonTitle(this PermissionStep step) => "grant";

    public static string WaitingButtonTitle(this PermissionStep step) => step switch
    {
        _ => "open",
    };
}

/// <summary>The permission flags, snapshotted so the card logic is testable without the OS.</summary>
public sealed record PermissionStatus(bool Microphone = false)
{
    public bool IsGranted(PermissionStep step) => step switch
    {
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
            var index = PermissionSteps.AllCases.ToList().IndexOf(Step);
            return $"step {index + 1} of {PermissionSteps.AllCases.Count}";
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

    /// <summary>Performs the request for a step (opens ms-settings:privacy-microphone). Injected so tests open nothing.</summary>
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
        CurrentPrompt = prompt with { Stage = PermissionPromptStage.Waiting };
    }

    public void Dismiss()
    {
        IsDismissedForThisLaunch = true;
        CurrentPrompt = null;
    }

    public void OpenPanel() => OpenSettings();
}
